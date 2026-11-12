"""Which repository a `rig gh` command acts on: ``--repo owner/name``, or this clone's origin.

A leaf module shared by `rig.gh_threads` and `rig.gh_pr`.
"""

from __future__ import annotations

from collections.abc import Callable

import typer

from rig import proc
from rig.repo_slug import RepoSlug

type OriginReader = Callable[[], RepoSlug | None]

#: Exit code for a repo that cannot be worked out, the same as any other error.
ERROR = 2


def origin_slug() -> RepoSlug | None:
    """The GitHub repo the working directory's ``origin`` points at."""
    return RepoSlug.from_remote(proc.git_out("remote", "get-url", "origin"))


def resolve_target(repo: str | None, origin: OriginReader) -> RepoSlug:
    """The repo *repo* names, or the origin's when it is None. Exits 2 with the reason when neither works."""
    slug = RepoSlug.from_path(repo) if repo else origin()
    if slug is None:
        typer.echo(
            f"Cannot tell which repo: pass --repo owner/name ({repo!r} is not one)"
            if repo
            else "Cannot tell which repo: no GitHub origin here; pass --repo owner/name",
            err=True,
        )
        raise typer.Exit(ERROR)
    return slug
