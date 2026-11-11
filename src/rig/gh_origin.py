"""Point `gh repo view` at a clone's `origin`.

With no repository argument, `gh repo view` resolves the clone's remotes and prefers one named
`upstream` over `origin`. In a fork clone that describes the upstream project, so anything asking
"which repo is this?" got someone else's: governance refused to enforce on rtkelly-labs/parquet-dotnet
because it saw aloneguid/parquet-dotnet, and generated policies named the wrong project.
"""

from __future__ import annotations

import pathlib
from dataclasses import dataclass

from rig import proc

__all__ = ("RepoIdentity", "get_repo_root_and_origin", "origin_args")


def origin_args(repo_path: pathlib.Path) -> list[str]:
    """`[<origin url>]` to pass to `gh repo view`, or `[]` (gh's own resolution) when there is no origin."""
    url = proc.git_out("remote", "get-url", "origin", cwd=repo_path)
    return [url] if url else []


@dataclass(frozen=True, slots=True)
class RepoIdentity:
    """A clone's main checkout and its origin URL; both None outside a repository."""

    root: pathlib.Path | None
    origin_url: str | None


def get_repo_root_and_origin(path: pathlib.Path) -> RepoIdentity:
    """The main checkout holding *path* and its origin URL.

    A worktree answers for its main checkout, found through ``git rev-parse --git-common-dir``.
    """
    common_raw = proc.git_out("rev-parse", "--git-common-dir", cwd=path, timeout=5)
    if not common_raw:
        return RepoIdentity(root=None, origin_url=None)
    common_path = pathlib.Path(common_raw)
    if not common_path.is_absolute():
        common_path = (path / common_path).resolve()
    primary_root = common_path.parent
    url = proc.git_out("config", "--get", "remote.origin.url", cwd=primary_root, timeout=5)
    return RepoIdentity(root=primary_root, origin_url=url or None)
