"""Core engine for AI agent skill discovery, symlink synchronization, and auditing."""

from __future__ import annotations

import fnmatch
import shutil
from collections.abc import Sequence
from dataclasses import dataclass
from pathlib import Path

from pydantic import StrictStr, TypeAdapter, ValidationError
from yaml import YAMLError

from rig.fs_links import is_link, link_directory, link_or_exists
from rig.skills.models import (
    CLI_SYSTEM_DISPLAY_NAMES,
    DEFAULT_PRIMARY_SYSTEMS,
    CliSystem,
    SkillManifest,
    SkillsAuditReport,
    SystemSkillStatus,
)
from rig.typed_json import Lenient, try_decode
from rig.typed_yaml import load_yaml


def get_canonical_repo_skills_dir() -> Path:
    """Returns the skills/ directory inside the shared-utilities repository."""
    # src/rig/skills/engine.py -> repo root is parents[3]
    repo_root = Path(__file__).resolve().parents[3]
    return repo_root / "skills"


def get_canonical_estate_allowlist_path() -> Path:
    """Returns the path to config/skills-allowlist.json inside shared-utilities."""
    repo_root = Path(__file__).resolve().parents[3]
    return repo_root / "config" / "skills-allowlist.json"


def load_estate_allowlist(allowlist_path: Path | None = None) -> list[str] | None:
    """Loads the estate-wide skill allowlist if configured, otherwise returns None."""
    target = allowlist_path or get_canonical_estate_allowlist_path()
    if not target.is_file():
        return None
    try:
        adapter = TypeAdapter(list[str])
        return adapter.validate_json(target.read_bytes())
    except (OSError, ValidationError):
        return None


def is_skill_allowed(skill_name: str, allowlist: Sequence[str] | None) -> bool:
    """Check whether a skill name matches an allowlist pattern."""
    if allowlist is None or "*" in allowlist:
        return True
    return any(fnmatch.fnmatch(skill_name, pat) for pat in allowlist)


def get_skill_activation_state_path(home: Path | None = None) -> Path:
    """Return the user-local ledger for temporary hidden skill activations."""
    base_home = home or Path.home()
    return base_home / ".config" / "devenv" / "skill-activations.json"


def load_skill_activation_state(home: Path | None = None) -> list[str]:
    """Load names activated outside the checked-in catalog."""
    target = get_skill_activation_state_path(home)
    if not target.is_file():
        return []
    try:
        raw = TypeAdapter(list[str]).validate_json(target.read_bytes())
    except (OSError, ValidationError):
        return []
    return sorted(set(raw))


@dataclass(frozen=True, slots=True)
class _SkillSource:
    """A directory searched for skills, and the label its skills are reported under."""

    name: str
    path: Path


def _save_skill_activation_state(names: Sequence[str], home: Path | None = None) -> None:
    target = get_skill_activation_state_path(home)
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_bytes(TypeAdapter(list[str]).dump_json(sorted(set(names)), indent=2) + b"\n")


def _resolve_activation_names(
    names: Sequence[str],
    all_skills: bool,
    search_paths: Sequence[Path] | None = None,
) -> list[str]:
    discovered = discover_all_skills(
        search_paths=search_paths,
        allowlist=None,
        use_estate_allowlist=False,
    )
    by_name = {skill.name: skill for skill in discovered}
    if all_skills:
        return sorted(by_name)
    selected = sorted({name.strip() for name in names if name.strip()})
    unknown = [name for name in selected if name not in by_name]
    if unknown:
        raise ValueError(f"Unknown skill(s): {', '.join(unknown)}")
    return selected


def activate_skills(
    names: Sequence[str],
    systems: Sequence[CliSystem] | None = None,
    home: Path | None = None,
    all_skills: bool = False,
    dry_run: bool = False,
    search_paths: Sequence[Path] | None = None,
) -> dict[CliSystem, int]:
    """Add a reversible overlay of selected hidden skills to each target system."""
    selected = _resolve_activation_names(names, all_skills, search_paths)
    allowlist = load_estate_allowlist()
    overlay = [name for name in selected if not is_skill_allowed(name, allowlist)]
    results = sync_skill_symlinks(
        systems=systems,
        prune=False,
        dry_run=dry_run,
        home=home,
        search_paths=search_paths,
        allowlist=selected,
        use_estate_allowlist=False,
    )
    if not dry_run:
        _save_skill_activation_state([*load_skill_activation_state(home), *overlay], home)
    return results


def deactivate_skills(
    names: Sequence[str],
    systems: Sequence[CliSystem] | None = None,
    home: Path | None = None,
    all_skills: bool = False,
    dry_run: bool = False,
    search_paths: Sequence[Path] | None = None,
) -> dict[CliSystem, int]:
    """Remove selected overlay links without touching user-owned or base links."""
    activated = load_skill_activation_state(home)
    requested = activated if all_skills else sorted({name.strip() for name in names if name.strip()})
    selected = [name for name in requested if name in activated]
    discovered = discover_all_skills(
        search_paths=search_paths,
        allowlist=None,
        use_estate_allowlist=False,
    )
    targets = {skill.name: skill.folder_path for skill in discovered}
    results: dict[CliSystem, int] = {}
    for system in systems or DEFAULT_PRIMARY_SYSTEMS:
        target_dir = get_system_directory(system, home=home)
        removed = 0
        if target_dir.is_dir():
            for name in selected:
                link = target_dir / name
                expected = targets.get(name)
                if expected is None or not is_link(link):
                    continue
                try:
                    if link.resolve(strict=True) != expected:
                        continue
                except (OSError, ValueError):
                    continue
                removed += 1
                if not dry_run:
                    link.unlink()
        results[system] = removed
    if not dry_run:
        remaining: list[str] = []
        for name in activated:
            if name not in selected:
                remaining.append(name)
                continue
            expected = targets.get(name)
            if expected is None:
                continue
            still_active = False
            for system in CliSystem:
                link = get_system_directory(system, home=home) / name
                if not is_link(link):
                    continue
                try:
                    still_active = link.resolve(strict=True) == expected
                except (OSError, ValueError):
                    still_active = False
                if still_active:
                    break
            if still_active:
                remaining.append(name)
        _save_skill_activation_state(remaining, home)
    return results


def get_system_directory(system: CliSystem, home: Path | None = None) -> Path:
    """Returns the canonical global skills directory for a given CLI system."""
    base_home = home or Path.home()
    match system:
        case CliSystem.OPENCODE:
            return base_home / ".config" / "opencode" / "skills"
        case CliSystem.CLAUDE:
            return base_home / ".claude" / "skills"
        case CliSystem.CODEX:
            return base_home / ".codex" / "skills"
        case CliSystem.ANTIGRAVITY:
            return base_home / ".gemini" / "config" / "skills"
        case CliSystem.AGENTS:
            return base_home / ".agents" / "skills"


@dataclass(frozen=True, slots=True)
class _FrontmatterInput:
    """The SKILL.md frontmatter fields this module reads."""

    name: Lenient[StrictStr] = None
    description: Lenient[StrictStr] = None


@dataclass(frozen=True, slots=True)
class SkillFrontmatter:
    """The name and one-line description a SKILL.md declares."""

    name: str
    description: str


def parse_skill_frontmatter(skill_md: Path) -> SkillFrontmatter:
    """Extract name and description from SKILL.md YAML frontmatter."""
    default_name = skill_md.parent.name
    default_desc = "Custom AI agent skill"

    if not skill_md.is_file():
        return SkillFrontmatter(default_name, default_desc)

    try:
        content = skill_md.read_text(encoding="utf-8", errors="replace")
        if not content.startswith("---"):
            return SkillFrontmatter(default_name, default_desc)

        parts = content.split("---", 2)
        if len(parts) < 3:
            return SkillFrontmatter(default_name, default_desc)

        metadata = try_decode(_FrontmatterInput, load_yaml(parts[1]))
        if metadata is None:
            return SkillFrontmatter(default_name, default_desc)

        name = metadata.name if metadata.name and metadata.name.strip() else default_name
        desc = metadata.description if metadata.description and metadata.description.strip() else default_desc
        return SkillFrontmatter(name.strip().strip("'\""), " ".join(desc.split()))
    except (OSError, UnicodeError, YAMLError):
        pass

    return SkillFrontmatter(default_name, default_desc)


def discover_all_skills(
    search_paths: Sequence[Path] | None = None,
    allowlist: Sequence[str] | None = None,
    use_estate_allowlist: bool = True,
) -> list[SkillManifest]:
    """Discover all skills across sources with silent strict priority.

    Priority hierarchy (first seen wins):
    1. Explicit search paths (if passed)
    2. Local workspace repository skills (`./skills`)
    3. Shared utilities repository skills (`shared-utilities/skills`)
    4. User custom overrides (`~/.config/devenv/skills`)
    5. Local external/community clones (`~/code/external/skills` or vendored plugins)
    """
    effective_allowlist = allowlist
    # Explicit search paths are test/embedding inputs and retain their historical
    # unfiltered behavior unless the caller supplies an allowlist directly.
    if effective_allowlist is None and use_estate_allowlist and search_paths is None:
        effective_allowlist = load_estate_allowlist()

    skills: list[SkillManifest] = []
    seen_names: set[str] = set()

    candidate_sources: list[_SkillSource] = []

    if search_paths is not None:
        candidate_sources.extend(_SkillSource(p.name, p) for p in search_paths)
    else:
        # 1. Local workspace repository skills (if present and distinct)
        cwd_skills = Path.cwd() / "skills"
        canonical_skills = get_canonical_repo_skills_dir()

        if cwd_skills.is_dir() and cwd_skills.resolve() != canonical_skills.resolve():
            candidate_sources.append(_SkillSource("workspace", cwd_skills))

        # 2. Canonical shared-utilities skills
        if canonical_skills.is_dir():
            candidate_sources.append(_SkillSource("shared-utilities", canonical_skills))

        # 3. User personal custom overrides
        user_custom = Path.home() / ".config" / "devenv" / "skills"
        if user_custom.is_dir():
            candidate_sources.append(_SkillSource("user-custom", user_custom))

        # 5. Vendored plugins store
        vendors_dir = Path.home() / ".config" / "devenv" / "vendors"
        if vendors_dir.is_dir():
            for vendor_folder in sorted(vendors_dir.iterdir()):
                if vendor_folder.is_dir() and not vendor_folder.name.startswith("."):
                    candidate_sources.append(_SkillSource(vendor_folder.name, vendor_folder))

    ignored_subdirs = frozenset({"node_modules", "dist", "build", "temp", ".git", "__pycache__"})

    for source in candidate_sources:
        source_name, source_dir = source.name, source.path
        if not source_dir.is_dir():
            continue

        for skill_md in sorted(source_dir.rglob("SKILL.md")):
            rel_parts = skill_md.relative_to(source_dir).parts
            if any(part in ignored_subdirs or part.startswith(".") for part in rel_parts):
                continue

            folder = skill_md.parent
            frontmatter = parse_skill_frontmatter(skill_md)
            name, desc = frontmatter.name, frontmatter.description

            effective_source = source_name
            if "vendored" in skill_md.parts:
                v_idx = skill_md.parts.index("vendored")
                if len(skill_md.parts) > v_idx + 1:
                    effective_source = skill_md.parts[v_idx + 1]

            if name not in seen_names:
                name_allowed = is_skill_allowed(name, effective_allowlist)
                folder_allowed = is_skill_allowed(folder.name, effective_allowlist)
                if not name_allowed and not folder_allowed:
                    continue

                skills.append(
                    SkillManifest(
                        name=name,
                        description=desc,
                        folder_path=folder.resolve(),
                        skill_md_path=skill_md.resolve(),
                        source_name=effective_source,
                    )
                )
                seen_names.add(name)

    return skills


def audit_skill_symlinks(
    systems: Sequence[CliSystem] | None = None,
    home: Path | None = None,
    search_paths: Sequence[Path] | None = None,
    allowlist: Sequence[str] | None = None,
    use_estate_allowlist: bool = True,
) -> SkillsAuditReport:
    """Audit the current symlink state across the specified AI agent CLI systems."""
    target_systems = list(systems or DEFAULT_PRIMARY_SYSTEMS)
    effective_allowlist = allowlist
    if effective_allowlist is None and use_estate_allowlist and search_paths is None:
        effective_allowlist = load_estate_allowlist()

    discovered = discover_all_skills(
        search_paths,
        allowlist=effective_allowlist,
        use_estate_allowlist=False,
    )
    catalog = {s.name: s for s in discovered}

    report = SkillsAuditReport(
        discovered_skills=discovered,
        allowlist_active=effective_allowlist is not None,
    )

    for sys in target_systems:
        target_dir = get_system_directory(sys, home=home)
        display = CLI_SYSTEM_DISPLAY_NAMES.get(sys, sys.value)

        status = SystemSkillStatus(
            system=sys,
            display_name=display,
            target_dir=target_dir,
            dir_exists=target_dir.is_dir(),
        )

        if not target_dir.is_dir():
            status.missing_skills = list(catalog.keys())
            report.systems[sys] = status
            continue

        present_names: set[str] = set()

        for item in sorted(target_dir.iterdir()):
            name = item.name
            if name.startswith("."):
                continue

            if not is_link(item):
                # A real file or directory is agent-owned state (Claude Code's
                # ``synced`` bucket, for one). It is only a collision when it
                # shadows a name the catalog wants to link.
                if name in catalog:
                    present_names.add(name)
                    status.total_links += 1
                    status.collisions.append(name)
                continue

            present_names.add(name)
            status.total_links += 1

            try:
                resolved = item.resolve(strict=True)
            except (OSError, ValueError):
                # Symlink target does not exist (broken)
                status.broken_links.append(name)
                continue

            if name not in catalog:
                # Symlink points to valid folder, but not in our active catalog
                status.stale_links.append(name)
            else:
                expected = catalog[name].folder_path
                if resolved == expected:
                    status.valid_links.append(name)
                else:
                    status.drifted_links.append(name)

        # Check for missing skills
        for expected_name in sorted(catalog.keys()):
            if expected_name not in present_names:
                status.missing_skills.append(expected_name)

        report.systems[sys] = status

    return report


def is_prunable_entry(item: Path) -> bool:
    """Whether a target-directory entry is a link this system may remove.

    Only symlinks are prunable. A real file or directory in an agent's skills
    directory belongs to that agent - Claude Code keeps its own ``synced``
    bucket there - and pruning it would destroy state this system never created.
    A dangling symlink is prunable: only a removed source could have produced it.
    """
    return is_link(item)


def sync_skill_symlinks(
    systems: Sequence[CliSystem] | None = None,
    prune: bool = True,
    dry_run: bool = False,
    home: Path | None = None,
    search_paths: Sequence[Path] | None = None,
    allowlist: Sequence[str] | None = None,
    use_estate_allowlist: bool = True,
) -> dict[CliSystem, int]:
    """Ensure all discovered skills are symlinked to each target CLI system.

    Returns mapping of system -> number of active links provisioned.
    """
    target_systems = list(systems or DEFAULT_PRIMARY_SYSTEMS)
    effective_allowlist = allowlist
    if effective_allowlist is None and use_estate_allowlist and search_paths is None:
        effective_allowlist = load_estate_allowlist()

    discovered = discover_all_skills(
        search_paths,
        allowlist=effective_allowlist,
        use_estate_allowlist=False,
    )
    results: dict[CliSystem, int] = {}

    for sys in target_systems:
        target_dir = get_system_directory(sys, home=home)
        active_names = {s.name for s in discovered}
        installed_count = 0

        if not dry_run:
            target_dir.mkdir(parents=True, exist_ok=True)

        for skill in discovered:
            dest_link = target_dir / skill.name
            if dry_run:
                installed_count += 1
                continue

            # Check existing entry
            if link_or_exists(dest_link):
                try:
                    if is_link(dest_link) and dest_link.resolve() == skill.folder_path:
                        installed_count += 1
                        continue
                except (OSError, ValueError):
                    pass

                # Remove stale or invalid symlink / collision
                if dest_link.is_dir() and not is_link(dest_link):
                    shutil.rmtree(dest_link)
                else:
                    dest_link.unlink()

            try:
                link_directory(dest_link, skill.folder_path)
                installed_count += 1
            except OSError:
                # Neither a symlink nor a junction could be made
                shutil.copytree(skill.folder_path, dest_link)
                installed_count += 1

        # Prune only the links this system owns; leave foreign entries alone.
        if prune and target_dir.is_dir() and not dry_run:
            for item in target_dir.iterdir():
                if item.name.startswith(".") or item.name in active_names:
                    continue
                if is_prunable_entry(item):
                    item.unlink()

        results[sys] = installed_count

    return results


def install_skill_link(name: str, folder_path: Path, target_dir: Path, dry_run: bool = False) -> bool:
    """Install one skill link using the same idempotent rules as catalog sync."""
    if dry_run:
        return True

    try:
        target_dir.mkdir(parents=True, exist_ok=True)
        dest_link = target_dir / name
        if link_or_exists(dest_link):
            try:
                if is_link(dest_link) and dest_link.resolve() == folder_path.resolve():
                    return True
            except (OSError, ValueError):
                pass
            if dest_link.is_dir() and not is_link(dest_link):
                shutil.rmtree(dest_link)
            else:
                dest_link.unlink()

        try:
            link_directory(dest_link, folder_path)
        except OSError:
            shutil.copytree(folder_path, dest_link)
        return True
    except (OSError, shutil.Error):
        return False


def prune_broken_skill_symlinks(
    systems: Sequence[CliSystem] | None = None,
    dry_run: bool = False,
    home: Path | None = None,
) -> dict[CliSystem, int]:
    """Scan target CLI systems and delete any broken/dangling symlinks.

    Returns mapping of system -> number of broken symlinks pruned.
    """
    target_systems = list(systems or DEFAULT_PRIMARY_SYSTEMS)
    pruned: dict[CliSystem, int] = {}

    for sys in target_systems:
        target_dir = get_system_directory(sys, home=home)
        count = 0
        if not target_dir.is_dir():
            pruned[sys] = 0
            continue

        for item in target_dir.iterdir():
            if is_link(item):
                try:
                    item.resolve(strict=True)
                except (OSError, ValueError):
                    count += 1
                    if not dry_run:
                        item.unlink()

        pruned[sys] = count

    return pruned
