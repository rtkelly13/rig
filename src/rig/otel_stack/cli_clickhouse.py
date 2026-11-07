"""`otel-stack clickhouse`: the on-demand half. Nothing here runs until `up`, and `down` leaves nothing running."""

from __future__ import annotations

import re
from pathlib import Path
from typing import Annotated

import httpx
import typer
from rich.console import Console
from rich.table import Table

from rig.otel_stack import backfill as backfill_mod
from rig.otel_stack import clickhouse, clickhouse_config, pinned, replay
from rig.otel_stack.collector import UnsupportedPlatformError
from rig.otel_stack.paths import clickhouse_home
from rig.otel_stack.query import ClickHouse, ClickHouseError

app = typer.Typer(
    help="ClickHouse on demand: `up`, `backfill` the recorded files, `query`, then `down`. Tables keep 7 days.",
    no_args_is_help=True,
)
console = Console()
err = Console(stderr=True)
COLLECTOR_URL = f"http://{clickhouse_config.HOST}:{clickhouse_config.COLLECTOR_PORT}"
_FORMAT_CLAUSE = re.compile(r"\bFORMAT\s+\w+\s*;?\s*$", re.IGNORECASE)


def _clickhouse() -> ClickHouse:
    """The client every command uses; tests replace it with one on a mock transport."""
    return ClickHouse.local()


@app.command("install")
def install_command() -> None:
    """Download and verify the pinned ClickHouse and otelcol-contrib, without starting anything."""
    try:
        for pin in (pinned.clickhouse_pin(), pinned.contrib_pin()):
            console.print(f"[green]verified[/green] {pinned.install(pin)}")
    except UnsupportedPlatformError as exc:
        err.print(f"[yellow]{exc}[/yellow]")
        raise typer.Exit(code=1) from exc


@app.command("up")
def up_command() -> None:
    """Install what is missing, start ClickHouse and the contrib collector, wait until both answer."""
    try:
        result = clickhouse.up()
    except (UnsupportedPlatformError, clickhouse.StartupError) as exc:
        err.print(f"[red]{exc}[/red]")
        raise typer.Exit(code=1) from exc
    for name in result.started:
        console.print(f"[green]started[/green] {name}")
    for name in result.already:
        console.print(f"already running: {name}")
    console.print(
        f"ClickHouse http://{clickhouse_config.HOST}:{clickhouse_config.HTTP_PORT} (database "
        f"{clickhouse_config.DATABASE}); OTLP for backfill {COLLECTOR_URL}. `otel-stack clickhouse down` stops both."
    )


@app.command("down")
def down_command() -> None:
    """Stop the contrib collector and ClickHouse, and wait for both to exit. The data stays."""
    stopped = clickhouse.down()
    console.print(f"stopped: {', '.join(stopped)}" if stopped else "nothing was running")


@app.command("status")
def status_command() -> None:
    """Whether each part runs, its memory, and what the data folder and tables hold."""
    for part in clickhouse.status():
        if part.pid is None:
            console.print(f"{part.name}: not running")
        else:
            memory = f", {part.resident_mb:.1f} MB resident" if part.resident_mb is not None else ""
            console.print(f"{part.name}: pid {part.pid}{memory}")
    console.print(f"data: {clickhouse.data_bytes() / 1_048_576:.1f} MB in {clickhouse_home() / 'data'}")
    with _clickhouse() as client:
        if not client.ping():
            return
        table = Table("table", "rows", "on disk")
        for stats in client.table_stats():
            table.add_row(stats.table, str(stats.rows), f"{stats.bytes_on_disk / 1_048_576:.1f} MB")
        console.print(table)


@app.command("query")
def query_command(
    sql: Annotated[str, typer.Argument(help='SQL to run, e.g. "SELECT count() FROM otel.otel_traces".')],
    output_format: Annotated[
        str, typer.Option("--format", help="ClickHouse output format, unless the SQL names one.")
    ] = ("PrettyCompactMonoBlock"),
) -> None:
    """Run SQL against the on-demand ClickHouse and print the result."""
    statement = sql.strip().rstrip(";")
    if not _FORMAT_CLAUSE.search(statement):
        statement = f"{statement}\nFORMAT {output_format}"
    with _clickhouse() as client:
        try:
            typer.echo(client.text(statement), nl=False)
        except ClickHouseError as exc:
            err.print(f"[red]{exc}[/red]")
            raise typer.Exit(code=1) from exc


@app.command("backfill")
def backfill_command(
    path: Annotated[list[Path] | None, typer.Option(help="Also replay this NDJSON trace file (repeatable).")] = None,
    settle_seconds: Annotated[
        int, typer.Option(help="Leave spans that ended this recently for the next run.")
    ] = backfill_mod.SETTLE_SECONDS,
    dry_run: Annotated[bool, typer.Option(help="Count what would be sent; send and mark nothing.")] = False,
) -> None:
    """Load the recorded files into ClickHouse: spans by identity, logs and metrics by position. Run after `up`."""
    with _clickhouse() as client, httpx.Client(timeout=30.0) as http:
        if not client.ping():
            err.print("[red]ClickHouse is not answering; run `otel-stack clickhouse up` first. Nothing sent.[/red]")
            raise typer.Exit(code=1)
        sender = backfill_mod.Sender(http, COLLECTOR_URL)
        files = backfill_mod.discover(
            backfill_mod.default_roots(), [*backfill_mod.collector_trace_files(), *(path or [])]
        )
        report = backfill_mod.run_backfill(
            files, client, sender, timing=backfill_mod.Timing(settle_seconds=settle_seconds), dry_run=dry_run
        )
        replayed = replay.replay(sender, dry_run=dry_run)
    verb = "would send" if dry_run else "sent"
    console.print(
        f"traces: {len(files)} files, {report.unchanged} unchanged since done, {len(report.files)} checked, "
        f"{report.sent} spans {verb}"
    )
    console.print(f"logs and metrics: {sum(r.lines for r in replayed)} lines {verb} from {len(replayed)} files")
    problems = [*report.errors, *(r.error for r in replayed if r.error)]
    if problems:
        err.print("\n".join(problems))
        raise typer.Exit(code=1)
