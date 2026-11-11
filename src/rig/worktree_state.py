"""Where rig keeps state about one worktree: `<worktree>/temp/.rig/`.

Spec: docs/repo-governance/worktree-state.md. In short:

- One folder per worktree, `temp/.rig/`, owned by rig. Each feature keeps one file there, named after
  it (`wrapup.json`), whose top-level `"schema"` is `"rig.<feature>/<version>"`.
- A missing file means the feature has never recorded anything for this worktree. A file that cannot be
  read, or carries a schema the reader does not know, is treated as missing.
- The folder must be gitignored, so the state never makes a worktree dirty. Every managed repo
  ignores `temp/` (`gitignore_entries` in config/repo_policies.json), and `repo-governance gitignore`
  checks git really ignores this folder. Writers (`src/Rig.Core/Estate/WorktreeState.cs`) check too, and skip
  writing when it is not ignored.
- Writes replace the file atomically. Deleting the folder is always safe; only history is lost.

Python only reads it now; the C# side writes. This is a leaf module.
"""

from __future__ import annotations

from enum import StrEnum
from pathlib import Path, PurePosixPath

from rig import proc

#: The folder, relative to a worktree's root.
STATE_DIR = PurePosixPath("temp/.rig")
#: `git check-ignore` exits 1 for "not ignored" and 128 when the folder is not a git worktree.
_NOT_IGNORED = 1


class Ignored(StrEnum):
    """Whether git ignores the state folder in a worktree."""

    YES = "yes"
    NO = "no"
    NOT_A_REPO = "not-a-repo"


def state_dir(worktree: Path) -> Path:
    """The state folder of the worktree rooted at *worktree*."""
    return worktree.joinpath(*STATE_DIR.parts)


def state_file(worktree: Path, feature: str) -> Path:
    """`<worktree>/temp/.rig/<feature>.json`."""
    return state_dir(worktree) / f"{feature}.json"


def ignored(worktree: Path, *names: str) -> Ignored:
    """Ask git whether files in the state folder would be ignored. Nothing needs to exist yet.

    *names* are the files to check, `probe.json` by default. A writer names its own files: a repo
    can ignore the folder yet re-include one file in it (`!temp/.rig/wrapup.json`).
    """
    if not worktree.is_dir():
        return Ignored.NOT_A_REPO
    for name in names or ("probe.json",):
        ran = proc.git("check-ignore", "--quiet", "--no-index", (STATE_DIR / name).as_posix(), cwd=worktree)
        if not ran.ok:
            return Ignored.NO if ran.code == _NOT_IGNORED else Ignored.NOT_A_REPO
    return Ignored.YES


def read_text(worktree: Path, feature: str) -> str | None:
    """The feature's file, or None when it is missing or unreadable."""
    try:
        return state_file(worktree, feature).read_text(encoding="utf-8")
    except OSError:
        return None
