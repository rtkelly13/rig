"""Point `gh repo view` at a clone's `origin`.

With no repository argument, `gh repo view` resolves the clone's remotes and prefers one named
`upstream` over `origin`. In a fork clone that describes the upstream project, so anything asking
"which repo is this?" got someone else's: governance refused to enforce on rtkelly-labs/parquet-dotnet
because it saw aloneguid/parquet-dotnet, and generated policies named the wrong project.
"""

from __future__ import annotations

import pathlib

from rig import proc

__all__ = ("origin_args",)


def origin_args(repo_path: pathlib.Path) -> list[str]:
    """`[<origin url>]` to pass to `gh repo view`, or `[]` (gh's own resolution) when there is no origin."""
    url = proc.git_out("remote", "get-url", "origin", cwd=repo_path)
    return [url] if url else []
