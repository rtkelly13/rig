"""Read-only git facts about a checkout's worktrees, shared by the Python estate commands.

The hygiene checks run on the C# CLI: src/Rig.Core/Estate/RepoChecks.cs reads, and
src/Rig.Core/Estate/Tidy.cs is the only place that deletes. These serve wrapup, orca, decisions,
shelve, trim and triage.

**A worktree is not a directory.** The convention puts them at `<repo>/temp/<name>`, which is
also where scratch files live, so a worktree is only ever removed through `git worktree remove`.
"""

from __future__ import annotations

import pathlib
from dataclasses import dataclass

from rig import proc

# ---------------------------------------------------------------------------
# worktrees
# ---------------------------------------------------------------------------


@dataclass(frozen=True)
class Worktree:
    """One entry from `git worktree list --porcelain`."""

    path: pathlib.Path
    head: str = ""
    branch: str = ""
    detached: bool = False
    bare: bool = False
    locked: bool = False
    lock_reason: str = ""
    prunable: bool = False
    prune_reason: str = ""
    is_main: bool = False

    @property
    def name(self) -> str:
        return self.path.name

    @property
    def unborn(self) -> bool:
        """HEAD names a branch whose ref does not exist; porcelain prints the null OID for it."""
        return bool(self.branch) and bool(self.head) and set(self.head) == {"0"}


@dataclass(slots=True)
class _PorcelainRecord:
    """One `git worktree list --porcelain` stanza as it accumulates line by line."""

    worktree: str | None = None
    head: str = ""
    branch: str = ""
    detached: bool = False
    bare: bool = False
    locked: str | None = None
    prunable: str | None = None

    def take(self, key: str, value: str) -> None:
        match key:
            case "worktree":
                self.worktree = value
            case "HEAD":
                self.head = value
            case "branch":
                self.branch = value
            case "detached":
                self.detached = True
            case "bare":
                self.bare = True
            case "locked":
                self.locked = value
            case "prunable":
                self.prunable = value
            case _:
                pass


def parse_worktrees(porcelain: str) -> list[Worktree]:
    """Parse `git worktree list --porcelain`. Pure, so it is testable offline.

    The first record is always the main worktree — the checkout itself — which
    matters because several checks apply only to the others.
    """
    out: list[Worktree] = []
    current = _PorcelainRecord()

    def flush() -> None:
        if current.worktree is not None:
            out.append(
                Worktree(
                    path=pathlib.Path(current.worktree),
                    head=current.head,
                    branch=current.branch.removeprefix("refs/heads/"),
                    detached=current.detached,
                    bare=current.bare,
                    locked=current.locked is not None,
                    lock_reason=current.locked or "",
                    prunable=current.prunable is not None,
                    prune_reason=current.prunable or "",
                    is_main=not out,
                )
            )

    for line in porcelain.splitlines():
        if not line.strip():
            flush()
            current = _PorcelainRecord()
            continue
        key, _, value = line.partition(" ")
        current.take(key, value)
    flush()
    return out


def list_worktrees(repo: pathlib.Path) -> list[Worktree]:
    """Return the worktrees registered against *repo*."""
    return parse_worktrees(proc.git_out("worktree", "list", "--porcelain", cwd=repo))


def is_dirty(path: pathlib.Path) -> bool:
    """Return True if *path* has uncommitted changes, staged or not."""
    return bool(proc.git_out("status", "--porcelain", cwd=path))


def orphaned_count(path: pathlib.Path) -> int:
    """Return commits reachable from HEAD and present on no remote.

    This is the measure that means "would be lost". "ahead of upstream" is not:
    a worktree tracking `origin/main` after its own branch was squash-merged
    reports its original commits as "ahead" *forever*, even though every one of
    them still exists on a remote ref. Measured on `design-system`,
    `temp/feat-headless-table` reported 4 ahead and 0 orphaned — a refusal for
    nothing.
    """
    out = proc.git_out("rev-list", "--count", "HEAD", "--not", "--remotes", cwd=path)
    return int(out) if out.isdigit() else 0


def merged_by_ancestry(repo: pathlib.Path, branch: str, *, base: str = "origin/main") -> bool:
    """Return True if *branch* is an ancestor of *base*.

    True only for merge-commit and fast-forward landings. A squash merge leaves
    no ancestry, which is why a False here means "unknown", not "unmerged".
    """
    return proc.git("merge-base", "--is-ancestor", branch, base, cwd=repo).ok
