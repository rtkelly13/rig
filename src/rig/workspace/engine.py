"""Core engine for virtual monorepo estate auditing, inspection, and cleanup.

Enforces the architectural invariant (ADR 0017) that nothing exists in the
personal folder (``<code root>/personal``) without the CLI managing it.
"""

from __future__ import annotations

import pathlib
from dataclasses import dataclass, field

from pydantic import StrictBool, StrictStr

from rig import roots
from rig.typed_json import load_json, try_decode
from rig.workspace.models import ManagedRepoSpec


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
