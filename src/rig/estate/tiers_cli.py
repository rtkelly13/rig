"""`estate decisions`: the scheduled cleanup tier only a human can settle.

Kept apart from `cli.py`, which registers it, so neither module grows past
the size ratchet.
"""

from __future__ import annotations

import pathlib

import typer
from rich.console import Console
from rich.table import Table

from rig.estate import decisions as decisions_mod
from rig.estate.reclaim import human
from rig.estate.trim import DEFAULT_IDLE_DAYS

console = Console()
err = Console(stderr=True)


def default_roots() -> list[pathlib.Path]:
    """The personal folder, its org folders and this machine's work folders, where they exist."""
    from rig import roots, scopes

    personal = roots.personal_dir().resolve()
    candidates = [*scopes.org_dirs(personal), *scopes.work_dirs(personal.parent)]
    return [personal, *(root for root in candidates if root.is_dir())]


def decisions(
    roots: list[str] = typer.Option([], "--root", help="Code root to scan; repeat for several"),
    as_json: bool = typer.Option(False, "--json", help="Write the feed as JSON to stdout"),
    write: str | None = typer.Option(None, "--write", help="Also write the feed JSON to this file"),
    idle_days: int = typer.Option(DEFAULT_IDLE_DAYS, "--idle-days", help="Worktree idle threshold"),
) -> None:
    """Worktrees and directories only a human can decide about.

    Dubious ownership, unpushed or uncommitted work, branches whose PR was
    closed or never opened, and directories an earlier delete left behind.
    Read-only: each decision carries the command that would act on it.
    """
    paths = [pathlib.Path(r).expanduser().resolve() for r in roots] if roots else default_roots()
    missing = [p for p in paths if not p.is_dir()]
    for path in missing:
        err.print(f"[dim]skipping {path}: not a directory[/dim]")
    found = decisions_mod.collect_roots(paths, idle_days=idle_days)

    if write:
        target = pathlib.Path(write).expanduser()
        decisions_mod.write_feed(target, found)
        err.print(f"[dim]wrote {len(found)} decision(s) to {target}[/dim]")

    if as_json:
        typer.echo(decisions_mod.dump_feed(found))
        return

    table = Table(title=f"{len(found)} decision(s)", title_style="bold")
    table.add_column("kind")
    table.add_column("repo")
    table.add_column("path", style="dim")
    table.add_column("size", justify="right")
    table.add_column("reason")
    table.add_column("command", style="cyan")
    for item in found:
        table.add_row(item.kind, item.repo, item.path, human(item.size_bytes), item.reason, item.command)
    console.print(table)
