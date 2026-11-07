"""`chrome-tabs traces`: reports over the organise runs' traces."""

from __future__ import annotations

import json
from dataclasses import asdict
from pathlib import Path
from typing import TYPE_CHECKING, Annotated

import typer
from rich.markup import escape

from desktop_manager.chrome.traces import KEEP_DAYS, Run

if TYPE_CHECKING:
    pass

from desktop_manager.chrome.cli_common import (
    console,
)

traces_app = typer.Typer(
    name="traces",
    no_args_is_help=True,
    help="Reports over the organise runs' traces: recent runs, one run's span tree, a summary, and pruning.",
)


_TracesDirOpt = Annotated[
    Path | None,
    typer.Option("--dir", help="Traces directory. Default: %LOCALAPPDATA%/shared-utilities/chrome-tabs/traces."),
]


_DaysOpt = Annotated[int, typer.Option("--days", min=1, help="How many days of trace files to read.")]


def _load_runs(directory: Path | None, days: int) -> list[Run]:
    from desktop_manager.chrome import traces

    return traces.runs(traces.load_spans(directory or traces.traces_dir(), days=days))


@traces_app.command(name="runs")
def traces_runs(
    days: _DaysOpt = 7,
    limit: int = typer.Option(20, "--limit", min=1, help="Only the N most recent runs."),
    as_json: bool = typer.Option(False, "--json", help="Machine-readable output."),
    directory: _TracesDirOpt = None,
) -> None:
    """Recent organise runs, newest first: duration, outcome, tabs closed, grouping, the wait and its last block."""
    from desktop_manager.chrome import traces

    found = _load_runs(directory, days)[-limit:]
    if as_json:
        typer.echo(json.dumps([traces.run_dict(run) for run in reversed(found)], indent=2, ensure_ascii=False))
        return
    if not found:
        console.print(f"[dim]No organise runs in the last {days} days.[/dim]")
        return
    console.print(f"[bold]{escape(traces.RUNS_HEADER)}[/bold]")
    for line in traces.render_runs(found):
        console.print(escape(line))


@traces_app.command(name="show")
def traces_show(
    run_id: str | None = typer.Argument(
        None, help="Run id from `traces runs` (a prefix will do). Default: the latest."
    ),
    days: _DaysOpt = 30,
    directory: _TracesDirOpt = None,
) -> None:
    """One run's span tree: every phase with its duration, attributes and events."""
    from desktop_manager.chrome import traces

    run = traces.find_run(_load_runs(directory, days), run_id)
    if run is None:
        console.print(f"[red]No organise run[/red] {escape(run_id or '')} in the last {days} days.")
        raise typer.Exit(code=1)
    for line in traces.render_tree(run):
        console.print(escape(line))


@traces_app.command(name="summary")
def traces_summary(
    days: _DaysOpt = 7,
    as_json: bool = typer.Option(False, "--json", help="Machine-readable output."),
    directory: _TracesDirOpt = None,
) -> None:
    """Organise runs over N days: success rate, how often grouping happened or gave up, waits and blocks."""
    from desktop_manager.chrome import traces

    summary = traces.summarise(_load_runs(directory, days))
    if as_json:
        payload = {
            **asdict(summary),
            "blocks": [[block.reason, block.count] for block in summary.blocks],
            "success_rate": summary.success_rate,
        }
        typer.echo(json.dumps(payload, indent=2))
        return
    for line in traces.render_summary(summary, days):
        console.print(escape(line), style=None if line.startswith(" ") else "bold")


@traces_app.command(name="prune")
def traces_prune(
    keep_days: int = typer.Option(KEEP_DAYS, "--keep-days", min=1, help="Keep this many days of trace files."),
    dry_run: bool = typer.Option(False, "--dry-run", help="List the files that would go; delete nothing."),
    directory: _TracesDirOpt = None,
) -> None:
    """Delete trace files older than --keep-days. The first scheduled run of each day does this anyway."""
    from desktop_manager.chrome import traces

    removed = traces.prune(directory or traces.traces_dir(), keep_days=keep_days, dry_run=dry_run)
    for path in removed:
        console.print(f"  {escape(path.name)}")
    verb = "Would delete" if dry_run else "Deleted"
    console.print(f"{verb} {len(removed)} trace files older than {keep_days} days")
