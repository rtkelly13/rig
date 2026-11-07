"""Core engine for virtual monorepo estate auditing, inspection, and cleanup.

Enforces the architectural invariant (ADR 0017) that nothing exists in the
personal folder (``<code root>/personal``) without the CLI managing it.
"""

from __future__ import annotations

import json
import pathlib
import shutil
from dataclasses import dataclass, field

from pydantic import StrictBool, StrictStr

from rig import proc, roots
from rig.typed_json import JsonValue, load_json, try_decode
from rig.workspace.layout import project_dir, project_relpath
from rig.workspace.models import (
    EstateItem,
    EstateItemKind,
    ManagedRepoSpec,
    WorkspaceAuditResult,
)
from rig.workspace_config import load_skip_repos

PROJECTS_TABLE_START_MARKER = "<!-- MANAGED_PROJECTS_START -->"
PROJECTS_TABLE_END_MARKER = "<!-- MANAGED_PROJECTS_END -->"

# File or directory names recognized as root workspace orchestration assets
KNOWN_SYSTEM_ENTRIES = frozenset(
    {
        ".claude",
        ".DS_Store",
        ".pnpm-store",
        "personal.code-workspace",
        "AGENTS.md",
        "CLAUDE.md",
        "TODO.md",
        "temp",
    }
)


def estate_root_path(explicit: str | pathlib.Path | None = None) -> pathlib.Path:
    """Returns the code estate root (defaults to the personal folder, see `rig.roots`)."""
    if explicit:
        return pathlib.Path(explicit).resolve()
    return roots.personal_dir().resolve()


def config_file_path(explicit: str | pathlib.Path | None = None) -> pathlib.Path:
    """Locates managed-projects.json registry file."""
    if explicit:
        return pathlib.Path(explicit).resolve()
    return roots.managed_projects_config()


@dataclass(frozen=True, slots=True)
class _ProjectsFile:
    projects: list[object] = field(default_factory=list[object])


@dataclass(frozen=True, slots=True)
class _ProjectOrca:
    group: StrictStr = ""


@dataclass(frozen=True, slots=True)
class _ProjectEntry:
    name: StrictStr = ""
    repo: StrictStr = ""
    description: StrictStr = ""
    domain: StrictStr = ""
    stack: StrictStr = ""
    branch: StrictStr | None = None
    enabled: StrictBool = True
    orca: _ProjectOrca | None = None


@dataclass(frozen=True, slots=True)
class _WorkspaceFile:
    settings: dict[str, JsonValue] = field(default_factory=dict[str, JsonValue])


@dataclass(frozen=True, slots=True)
class CleanOutcome:
    """Whether an estate item was (or would be) removed, and the line to show for it."""

    ok: bool
    message: str


def load_managed_specs(path: pathlib.Path | None = None) -> list[ManagedRepoSpec]:
    """Loads all managed repository specifications from JSON configuration.

    Retired projects in the ``archived-projects.json`` beside it come back disabled, so their clones
    stay retained archives and are never offered for cleanup.
    """
    cfg = path or config_file_path()
    if not cfg.is_file():
        return []
    retired = _decode_specs(cfg.with_name(roots.ARCHIVED_PROJECTS), retired=True)
    return _decode_specs(cfg, retired=False) + retired


def retired_project_names(path: pathlib.Path | None = None) -> frozenset[str]:
    """Names in the ``archived-projects.json`` beside *path*: projects that never belong in Orca again."""
    cfg = path or config_file_path()
    return frozenset(spec.name for spec in _decode_specs(cfg.with_name(roots.ARCHIVED_PROJECTS), retired=True))


def _decode_specs(cfg: pathlib.Path, *, retired: bool) -> list[ManagedRepoSpec]:
    if not cfg.is_file():
        return []
    try:
        config = try_decode(_ProjectsFile, load_json(cfg.read_text(encoding="utf-8")))
        if config is None:
            return []
        specs: list[ManagedRepoSpec] = []
        for item in config.projects:
            entry = try_decode(_ProjectEntry, item)
            if entry is None:
                continue
            specs.append(
                ManagedRepoSpec(
                    name=entry.name,
                    repo=entry.repo,
                    description=entry.description,
                    domain=entry.domain,
                    stack=entry.stack,
                    branch=entry.branch,
                    enabled=entry.enabled and not retired,
                    orca_group=entry.orca.group if entry.orca is not None else None,
                )
            )
        return specs
    except (OSError, ValueError):
        return []


def get_git_remote_url(repo_path: pathlib.Path) -> str | None:
    """Retrieves origin remote URL for a local git clone."""
    return proc.git_out("config", "--get", "remote.origin.url", cwd=repo_path) or None


def normalize_git_url(url: str | None) -> str | None:
    """Normalizes git URL for comparison (handling git@, https://, ssh://, trailing .git)."""
    if not url:
        return None
    cleaned = url.strip()
    if cleaned.endswith(".git"):
        cleaned = cleaned[:-4]
    if cleaned.startswith("git@"):
        cleaned = cleaned[4:].replace(":", "/", 1)
    elif cleaned.startswith("https://"):
        cleaned = cleaned[8:]
    elif cleaned.startswith("http://"):
        cleaned = cleaned[7:]
    elif cleaned.startswith("ssh://git@"):
        cleaned = cleaned[10:].replace(":", "/", 1)
    return cleaned.lower().strip("/")


def audit_estate(
    root: pathlib.Path | None = None,
    specs: list[ManagedRepoSpec] | None = None,
    skip: frozenset[str] | None = None,
) -> WorkspaceAuditResult:
    """Audits the estate root directory against declared managed specifications.

    A spec named in *skip* (by default this machine's `tools.estate.skip_repos`) is
    never reported missing: this machine is not meant to have it.
    """
    skipped = load_skip_repos() if skip is None else skip
    estate_dir = root or estate_root_path()
    managed_specs = specs if specs is not None else load_managed_specs()
    managed_names = {s.name for s in managed_specs if s.enabled}
    retained_names = {s.name for s in managed_specs if not s.enabled}

    result = WorkspaceAuditResult(estate_root=estate_dir)

    if not estate_dir.exists() or not estate_dir.is_dir():
        return result

    discovered_names: set[str] = set()

    for item in sorted(estate_dir.iterdir(), key=lambda p: p.name.lower()):
        result.total_items += 1
        name = item.name

        # 1. Known workspace orchestration system entries
        if name in KNOWN_SYSTEM_ENTRIES:
            result.known_system_items.append(
                EstateItem(
                    name=name,
                    path=item,
                    kind=EstateItemKind.KNOWN_SYSTEM,
                    is_dir=item.is_dir(),
                    detail="Workspace orchestration file/directory",
                )
            )
            continue

        if item.is_dir():
            git_path = item / ".git"
            if git_path.exists():
                if git_path.is_dir():
                    # It is a standalone git clone
                    remote_url = get_git_remote_url(item)
                    if name in managed_names:
                        spec = next((s for s in managed_specs if s.name == name), None)
                        remote_mismatch = False
                        if spec and spec.repo and remote_url and not spec.repo.startswith("local/"):
                            norm_remote = normalize_git_url(remote_url)
                            norm_spec = normalize_git_url(spec.repo)
                            if norm_remote and norm_spec and norm_remote != norm_spec:
                                remote_mismatch = True

                        if remote_mismatch and spec:
                            result.unmanaged_items.append(
                                EstateItem(
                                    name=name,
                                    path=item,
                                    kind=EstateItemKind.UNMANAGED_REPO,
                                    is_dir=True,
                                    remote_url=remote_url,
                                    detail=f"Remote URL mismatch: local '{remote_url}' != declared '{spec.repo}'",
                                )
                            )
                        else:
                            discovered_names.add(name)
                            result.managed_repos.append(
                                EstateItem(
                                    name=name,
                                    path=item,
                                    kind=EstateItemKind.MANAGED_REPO,
                                    is_dir=True,
                                    remote_url=remote_url,
                                    detail="Registered managed repository",
                                )
                            )
                    elif name in retained_names:
                        spec = next((s for s in managed_specs if s.name == name), None)
                        discovered_names.add(name)
                        desc = spec.description if spec else ""
                        detail = f"Retained archive ({desc})" if desc else "Retained archive"
                        result.retained_archives.append(
                            EstateItem(
                                name=name,
                                path=item,
                                kind=EstateItemKind.RETAINED_ARCHIVE,
                                is_dir=True,
                                remote_url=remote_url,
                                detail=detail,
                            )
                        )
                    else:
                        result.unmanaged_items.append(
                            EstateItem(
                                name=name,
                                path=item,
                                kind=EstateItemKind.UNMANAGED_REPO,
                                is_dir=True,
                                remote_url=remote_url,
                                detail="Git clone not registered in managed-projects.json",
                            )
                        )
                else:
                    # git file -> linked worktree (e.g. Parquet.SourceGenerator-golden)
                    result.linked_worktrees.append(
                        EstateItem(
                            name=name,
                            path=item,
                            kind=EstateItemKind.LINKED_WORKTREE,
                            is_dir=True,
                            detail="Linked git worktree sibling",
                        )
                    )
            elif name in retained_names:
                # Directory without .git registered as retained archive
                spec = next((s for s in managed_specs if s.name == name), None)
                discovered_names.add(name)
                desc = spec.description if spec else ""
                detail = f"Retained archive ({desc})" if desc else "Retained archive"
                result.retained_archives.append(
                    EstateItem(
                        name=name,
                        path=item,
                        kind=EstateItemKind.RETAINED_ARCHIVE,
                        is_dir=True,
                        detail=detail,
                    )
                )
            else:
                # Directory without .git
                result.unmanaged_items.append(
                    EstateItem(
                        name=name,
                        path=item,
                        kind=EstateItemKind.UNMANAGED_DIR,
                        is_dir=True,
                        detail="Directory not a git repository and unmanaged",
                    )
                )
        else:
            # File that is not in KNOWN_SYSTEM_ENTRIES
            result.unmanaged_items.append(
                EstateItem(
                    name=name,
                    path=item,
                    kind=EstateItemKind.UNMANAGED_FILE,
                    is_dir=False,
                    detail="Orphaned unmanaged file in personal directory root",
                )
            )

    # Org-owned repos live in sibling org folders; anything not found is missing
    for spec in managed_specs:
        if spec.enabled and spec.name not in discovered_names:
            expected_path = project_dir(estate_dir, spec.name, spec.repo)
            if expected_path.parent != estate_dir and (expected_path / ".git").is_dir():
                result.managed_repos.append(
                    EstateItem(
                        name=spec.name,
                        path=expected_path,
                        kind=EstateItemKind.MANAGED_REPO,
                        is_dir=True,
                        remote_url=get_git_remote_url(expected_path),
                        detail=f"Registered managed repository ({expected_path.parent.name} org folder)",
                    )
                )
            elif not expected_path.exists() and spec.name not in skipped:
                result.missing_managed_repos.append(spec)

    return result


def clean_estate_item(
    item: EstateItem,
    *,
    dry_run: bool = False,
) -> CleanOutcome:
    """Purges or removes an unmanaged item from the estate directory."""
    if item.kind in {EstateItemKind.MANAGED_REPO, EstateItemKind.KNOWN_SYSTEM, EstateItemKind.RETAINED_ARCHIVE}:
        return CleanOutcome(False, f"Refused: {item.name} is a managed, system, or retained archive item")

    if dry_run:
        return CleanOutcome(True, f"[dry-run] Would remove {item.kind.value}: {item.path}")

    try:
        if item.is_dir:
            # For git clones, ensure safety: check if there are unpushed/uncommitted changes
            if item.kind == EstateItemKind.UNMANAGED_REPO:
                status = proc.git("status", "--porcelain", cwd=item.path)
                if not status.ok or status.out.strip():
                    return CleanOutcome(False, f"Refused to delete dirty or unreadable unmanaged repo: {item.name}")
            shutil.rmtree(item.path)
        else:
            item.path.unlink()
        return CleanOutcome(True, f"Removed {item.name}")
    except (OSError, UnicodeError) as exc:
        return CleanOutcome(False, f"Failed to remove {item.name}: {exc}")


def render_managed_projects_table(specs: list[ManagedRepoSpec]) -> str:
    """Renders the Markdown domain map table for active managed projects."""
    lines: list[str] = [
        "| Repository | Domain | Tech Stack | Description | Scope Guide |",
        "|---|---|---|---|---|",
    ]
    for spec in specs:
        if not spec.enabled:
            continue
        domain = spec.domain or "General"
        stack = spec.stack or "-"
        desc = spec.description or "-"
        rel = project_relpath(spec.name, spec.repo)
        link = rel if rel.startswith("../") else f"./{rel}"
        guide = f"[{rel}/AGENTS.md]({link}/AGENTS.md)"
        lines.append(f"| [`{spec.name}`]({link}) | {domain} | {stack} | {desc} | {guide} |")
    return "\n".join(lines)


def generate_root_agents_md(specs: list[ManagedRepoSpec]) -> str:
    """Generates the content for the estate root AGENTS.md router according to ADR 0017."""
    template_path = pathlib.Path(__file__).parent / "templates" / "root_agents_md.md"
    if not template_path.is_file():
        raise FileNotFoundError(f"Root AGENTS.md template not found: {template_path}")

    template_content = template_path.read_text(encoding="utf-8")
    table_content = render_managed_projects_table(specs)

    if PROJECTS_TABLE_START_MARKER not in template_content or PROJECTS_TABLE_END_MARKER not in template_content:
        raise ValueError(
            f"Root AGENTS.md template missing slot markers: "
            f"'{PROJECTS_TABLE_START_MARKER}' and '{PROJECTS_TABLE_END_MARKER}'"
        )

    before_marker, _, remainder = template_content.partition(PROJECTS_TABLE_START_MARKER)
    _, _, after_marker = remainder.partition(PROJECTS_TABLE_END_MARKER)

    injected_slot = f"{PROJECTS_TABLE_START_MARKER}\n{table_content}\n{PROJECTS_TABLE_END_MARKER}"

    return f"{before_marker.rstrip()}\n\n{injected_slot}\n\n{after_marker.lstrip()}"


def sync_estate_router(
    estate_dir: pathlib.Path,
    specs: list[ManagedRepoSpec],
) -> pathlib.Path:
    """Writes the root AGENTS.md, and removes any CLAUDE.md compatibility symlink.

    Claude Code reads AGENTS.md natively from v2.1.277, so the symlink is no longer
    needed. It is actively harmful to leave one lying around: when both files are
    present the default `claude-md-or-agents-md` mode prefers CLAUDE.md, so a stale
    or diverged copy silently shadows AGENTS.md instead of supplementing it.
    """
    agents_md_path = estate_dir / "AGENTS.md"
    claude_md_path = estate_dir / "CLAUDE.md"

    content = generate_root_agents_md(specs)
    agents_md_path.write_text(content, encoding="utf-8")

    # Only remove a link we would have created; never delete someone's real file.
    if claude_md_path.is_symlink():
        claude_md_path.unlink()

    return agents_md_path


def sync_estate_code_workspace(
    estate_dir: pathlib.Path,
    specs: list[ManagedRepoSpec],
) -> pathlib.Path:
    """Generates and updates personal.code-workspace with all active managed repositories."""
    workspace_file = estate_dir / "personal.code-workspace"
    folders: list[dict[str, str]] = []

    # Preserve present folders in sorted order
    for spec in sorted(specs, key=lambda s: s.name.lower()):
        if not spec.enabled:
            continue
        if project_dir(estate_dir, spec.name, spec.repo).exists():
            folders.append({"name": spec.name, "path": project_relpath(spec.name, spec.repo)})

    existing_settings: dict[str, JsonValue] = {}
    if workspace_file.is_file():
        try:
            existing = try_decode(_WorkspaceFile, load_json(workspace_file.read_text(encoding="utf-8")))
            if existing is not None:
                existing_settings = existing.settings
        except (OSError, ValueError):
            pass

    payload = {
        "folders": folders,
        "settings": existing_settings,
    }
    workspace_file.write_text(json.dumps(payload, indent=2) + "\n", encoding="utf-8")
    return workspace_file
