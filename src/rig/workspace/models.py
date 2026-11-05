"""Data models for personal virtual monorepo estate management."""

from __future__ import annotations

import pathlib
from dataclasses import dataclass, field
from enum import StrEnum


class EstateItemKind(StrEnum):
    MANAGED_REPO = "managed_repo"
    LINKED_WORKTREE = "linked_worktree"
    UNMANAGED_REPO = "unmanaged_repo"
    UNMANAGED_DIR = "unmanaged_dir"
    UNMANAGED_FILE = "unmanaged_file"
    KNOWN_SYSTEM = "known_system"  # e.g. .claude, personal.code-workspace, AGENTS.md, CLAUDE.md
    RETAINED_ARCHIVE = "retained_archive"  # Registered with enabled=false (e.g. legacy clones or backups)


@dataclass(frozen=True)
class ManagedRepoSpec:
    """Declared specification for a managed estate repository."""

    name: str
    repo: str
    description: str = ""
    domain: str = ""
    stack: str = ""
    branch: str | None = None
    enabled: bool = True
    orca_group: str | None = None


@dataclass(frozen=True)
class EstateItem:
    """An entry discovered directly under the personal estate root."""

    name: str
    path: pathlib.Path
    kind: EstateItemKind
    is_dir: bool
    remote_url: str | None = None
    detail: str = ""


def _default_estate_items() -> list[EstateItem]:
    return []


def _default_repo_specs() -> list[ManagedRepoSpec]:
    return []


@dataclass
class WorkspaceAuditResult:
    """Audit outcome measuring estate alignment against managed specifications."""

    estate_root: pathlib.Path
    total_items: int = 0
    managed_repos: list[EstateItem] = field(default_factory=_default_estate_items)
    linked_worktrees: list[EstateItem] = field(default_factory=_default_estate_items)
    unmanaged_items: list[EstateItem] = field(default_factory=_default_estate_items)
    missing_managed_repos: list[ManagedRepoSpec] = field(default_factory=_default_repo_specs)
    known_system_items: list[EstateItem] = field(default_factory=_default_estate_items)
    retained_archives: list[EstateItem] = field(default_factory=_default_estate_items)
    # A declared org-folder location holding something other than that repo's clone: a clone of
    # another remote, a plain directory, a worktree or a file. Reported only; `clean` never
    # touches it, because it lies outside the estate root.
    blocked_managed_repos: list[EstateItem] = field(default_factory=_default_estate_items)

    @property
    def is_clean(self) -> bool:
        """Clean when nothing is unmanaged, missing, or blocking a declared location."""
        return not (self.unmanaged_items or self.missing_managed_repos or self.blocked_managed_repos)
