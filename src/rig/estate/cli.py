"""`estate` — manage the code assets cloned on this machine.

Commands, ordered by the decision each one supports:

``audit``    the dashboard. One row per clone: what is at risk, what is
             reclaimable, how much sprawl.
``risk``     only what exists here and nowhere else. The precondition for
             deleting anything.
``reclaim``  the disk that regenerates from a command.
``trim``     the same, inside worktrees nobody has used for a week.
``tidy``     stale worktrees and finished branches.
``doctor``   one clone, every finding, each with its fix.
``sync``     `fetch --prune` across the estate, so `tidy` tells the truth about
             upstreams that are gone.
``prune-caches``  the tool caches that refill themselves (uv, pnpm, NuGet http,
             Claude temp, Docker build cache).
``decisions``     what only a human can decide: dubious ownership, unpushed or
             PR-less idle worktrees, directories a delete left behind.
``volume``   (Windows) where a whole drive's space went, from a WizTree MFT scan.
``shelve``   resolve a worktree `tidy` refuses: push a `TEMP-SHELVED:` snapshot to
             `shelved/<user>/<name>`, verify it on origin, remove the worktree.
             ``unshelve`` restores one; ``shelved`` lists and prunes them.

Read-only by default throughout. `reclaim`, `trim`, `tidy`, `prune-caches` and `shelve` take `--apply`;
`reclaim` and `tidy` refuse a clone holding uncommitted or unpushed work unless
`--force`, and `trim` refuses any directory git tracks or does not ignore.

Per § *Agent DX* in `docs/agents/agent-consistency.md`, `--json` writes the payload to
stdout with no Rich formatting; progress narration goes to stderr.
"""

from __future__ import annotations

import sys

import typer

from rig import csharp_cli, tracing
from rig.estate import shelve_cli

app = typer.Typer(add_completion=False, help="Manage the code assets cloned on this machine.")
# The subcommands in csharp_cli.PORTED_SUBCOMMANDS["estate"] run on the C# CLI (src/Rig.Cli/Estate).
csharp_cli.add_group_stubs(app, "estate")

app.add_typer(shelve_cli.app)


def main() -> None:
    """Entry point for `estate`: each run is one trace in `temp/traces/sdk/estate/`.

    A root span named after the subcommand parents every clone scan and every git and gh
    call beneath it. Spans also flush every 30s, so a long sweep that is killed still says
    where its time went.
    """
    tracing.init_tracing("estate")
    tracing.flush_periodically(30)
    command = next((arg for arg in sys.argv[1:] if not arg.startswith("-")), "")
    with tracing.span(f"estate {command}".rstrip(), scope="rig.estate.cli", **{"cli.argv": sys.argv[1:]}):
        app()


if __name__ == "__main__":
    main()
