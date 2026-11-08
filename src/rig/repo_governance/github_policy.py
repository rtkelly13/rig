"""The policy types for GitHub-side repo state: merge settings, the label taxonomy and Pages.

Kept apart from `policy_config` so that module stays under the ratchet's length limit; it imports
and re-exports them.
"""

from __future__ import annotations

from dataclasses import dataclass
from typing import Literal

__all__ = ("LabelSpec", "MergeSettings", "PagesPolicy")


@dataclass(frozen=True)
class MergeSettings:
    """The merge settings a repo carries on GitHub (`repo_settings`); set by one ``PATCH repos/<repo>``."""

    delete_branch_on_merge: bool = True
    allow_squash_merge: bool = True
    allow_merge_commit: bool = False
    allow_rebase_merge: bool = False
    allow_auto_merge: bool = True


@dataclass(frozen=True)
class LabelSpec:
    """One label of the taxonomy (`labels`); *color* is six hex digits without ``#``."""

    name: str
    color: str
    description: str


@dataclass(frozen=True)
class PagesPolicy:
    """GitHub Pages configuration policy for a repository."""

    enabled: bool = False
    build_type: Literal["workflow", "legacy"] = "workflow"
    cname: str | None = None
    https_enforced: bool = True
