"""The repository this checkout of image_drift lives in.

image_drift is its own uv project under `tools/image-drift/`, so the nearest `pyproject.toml`
is that project, not the repository. The brand assets and the gitignored `temp/` belong to
the repository, so the root is the nearest ancestor holding `.git` (a directory in a clone,
a file in a worktree). Installed outside a checkout, it falls back to the working directory.
"""

from __future__ import annotations

from pathlib import Path


def find_repo_root(start: Path) -> Path:
    """The nearest ancestor of `start` (or `start` itself) that holds `.git`, else the cwd."""
    for candidate in (start, *start.parents):
        if (candidate / ".git").exists():
            return candidate
    return Path.cwd()


REPO_ROOT = find_repo_root(Path(__file__).resolve().parent)
