"""Core engine for virtual monorepo estate auditing, inspection, and cleanup.

Enforces the architectural invariant (ADR 0017) that nothing exists in the
personal folder (``<code root>/personal``) without the CLI managing it.
"""

from __future__ import annotations

import json
import pathlib
from dataclasses import dataclass, field

from pydantic import StrictBool, StrictStr

from rig import roots
from rig.typed_json import JsonValue, load_json, try_decode
from rig.workspace.layout import project_dir, project_relpath
from rig.workspace.models import ManagedRepoSpec

PROJECTS_TABLE_START_MARKER = "<!-- MANAGED_PROJECTS_START -->"
PROJECTS_TABLE_END_MARKER = "<!-- MANAGED_PROJECTS_END -->"


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
