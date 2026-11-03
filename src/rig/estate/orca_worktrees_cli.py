"""`estate orca-worktrees`: report Orca's finished worktrees and, with --apply, remove them.

The verdicts are in `orca_worktrees.py`; this module only picks the Orca profiles and prints.
"""

from __future__ import annotations

import pathlib
import time

import typer
from pydantic import TypeAdapter
from rich.console import Console
from rich.table import Table

from rig.estate import orca_worktrees as finished
from rig.estate.orca_runtime import OrcaRuntime, OrcaUnavailableError, estate_user_data, work_user_data
from rig.estate.orca_worktrees import Row

console = Console()
err = Console(stderr=True)

_ROWS: TypeAdapter[list[Row]] = TypeAdapter(list[Row])


def _profiles(user_data: str | None, work_data: str | None, *, include_work: bool) -> list[tuple[str, pathlib.Path]]:
    estate = pathlib.Path(user_data).expanduser() if user_data else estate_user_data()
    chosen = [("personal", estate)]
    work = pathlib.Path(work_data).expanduser() if work_data else work_user_data()
    if include_work and work is not None and work.resolve() != estate.resolve():
        chosen.append(("work", work))
    return chosen


def _print(rows: list[Row], *, show_kept: bool, applied: bool) -> None:
    removable = [r for r in rows if r.removable]
    shown = rows if show_kept else removable
    verb = "removed" if applied else "finished"
    console.print(f"{len(removable)} of {len(rows)} Orca worktree(s) {verb}; the rest are kept")
    if not shown:
        return
    table = Table(show_lines=False)
    for column in ("profile", "project", "worktree", "branch", "verdict", "reason"):
        table.add_column(column)
    if applied:
        table.add_column("outcome")
    for row in sorted(shown, key=lambda r: (not r.removable, r.profile, r.project, r.path)):
        style = "green" if row.removable else "dim"
        cells = [row.profile, row.project, pathlib.Path(row.path).name, row.branch, row.verdict.value, row.reason]
        if applied:
            cells.append(row.outcome)
        table.add_row(*cells, style=style)
    console.print(table)


def orca_worktrees(
    user_data: str | None = typer.Option(
        None, "--orca-data", help="The estate's Orca user-data folder (default: orca-personal on Windows)"
    ),
    work_data: str | None = typer.Option(
        None, "--work-orca-data", help="The work Orca profile (default: Orca's own folder on Windows; none elsewhere)"
    ),
    personal_only: bool = typer.Option(False, "--personal-only", help="Leave the work profile alone"),
    idle_days: float = typer.Option(
        finished.DEFAULT_IDLE_DAYS,
        "--idle-days",
        help="Days a worktree holding nothing beyond the default branch must sit untouched to count as landed",
    ),
    show_kept: bool = typer.Option(False, "--all", help="List kept worktrees too, with why each is kept"),
    apply: bool = typer.Option(False, "--apply", help="Remove the finished worktrees through Orca"),
    as_json: bool = typer.Option(False, "--json", help="Write every row as JSON to stdout"),
) -> None:
    """Orca's worktrees whose work is finished: merged, landed, or already half-deleted.

    Read-only unless --apply. A worktree is finished when its branch's newest PR merged with
    nothing local since (`merged`), when it holds nothing outside the default branch and has sat
    idle for --idle-days (`landed`), when its folder is gone (`missing`), or when an interrupted
    delete left a folder git no longer knows and every file in it matches the branch (`leftover`).
    A dirty tree, a running or waiting agent, an open or closed-unmerged PR, unpushed commits and
    the worktree this command runs in are always kept. --apply removes through Orca's own
    delete, which stops the worktree's terminals and keeps any branch git calls unmerged.
    Exit code 2 when the estate's Orca is not running.
    """
    now_ms = time.time() * 1000
    cwd = pathlib.Path.cwd()
    rows: list[Row] = []
    for index, (profile, folder) in enumerate(_profiles(user_data, work_data, include_work=not personal_only)):
        try:
            runtime = OrcaRuntime.connect(folder)
            found = finished.review(runtime, profile, cwd=cwd, now_ms=now_ms, idle_days=idle_days)
            rows += finished.apply(runtime, found) if apply else found
        except OrcaUnavailableError as e:
            if index == 0:
                err.print(f"[red]{e}[/red]")
                raise typer.Exit(2) from e
            err.print(f"[yellow]{profile} Orca profile not checked: {e}[/yellow]")
    if as_json:
        typer.echo(_ROWS.dump_json(rows, indent=2).decode())
        return
    _print(rows, show_kept=show_kept, applied=apply)
    if apply and any(r.outcome.startswith("failed") for r in rows):
        raise typer.Exit(1)
