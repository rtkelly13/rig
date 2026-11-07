"""Facts about a session's working directory: does it exist, and which repo, worktree and branch is it."""

from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path

from rig import proc


@dataclass(frozen=True)
class FolderFacts:
    path: str
    exists: bool
    #: Top of the git checkout containing `path`, or None outside git (or if it's gone).
    git_root: str | None = None
    branch: str | None = None
    #: True for a linked worktree (`git worktree add`), False for a main checkout.
    is_worktree: bool = False


def _git(path: str, *args: str) -> str | None:
    ran = proc.git(*args, cwd=Path(path), timeout=10)
    return ran.out.strip() if ran.ok else None


def folder_facts(path: str) -> FolderFacts:
    if not Path(path).is_dir():
        return FolderFacts(path=path, exists=False)
    lines = (_git(path, "rev-parse", "--show-toplevel", "--git-dir", "--git-common-dir") or "").splitlines()
    if len(lines) != 3:
        return FolderFacts(path=path, exists=True)
    root, git_dir, common_dir = lines
    branch = _git(path, "branch", "--show-current") or None
    is_worktree = Path(path, git_dir).resolve() != Path(path, common_dir).resolve()
    return FolderFacts(path=path, exists=True, git_root=root, branch=branch, is_worktree=is_worktree)
