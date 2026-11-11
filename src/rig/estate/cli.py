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

import json
import pathlib
import sys

import typer
from rich.console import Console
from rich.table import Table

from rig import csharp_cli, roots, tracing
from rig.estate import apps as apps_mod
from rig.estate import (
    apps_windows,
    orca_cli,
    orca_worktrees_cli,
    shelve_cli,
    volume_report,
    wrapup_cli,
)
from rig.estate import downloads as downloads_mod
from rig.estate.reclaim import human

app = typer.Typer(add_completion=False, help="Manage the code assets cloned on this machine.")
# The subcommands in csharp_cli.PORTED_SUBCOMMANDS["estate"] run on the C# CLI (src/Rig.Cli/Estate).
csharp_cli.add_group_stubs(app, "estate")
console = Console()
err = Console(stderr=True)

# ---------------------------------------------------------------------------


def _parse_size(text: str) -> int:
    """Parse `500MB` / `2GB` / `1024`. Falls back to 0 on nonsense."""
    raw = text.strip().upper()
    for suffix, factor in (("TB", 1024**4), ("GB", 1024**3), ("MB", 1024**2), ("KB", 1024), ("B", 1)):
        if raw.endswith(suffix):
            head = raw[: -len(suffix)].strip()
            try:
                return int(float(head) * factor)
            except ValueError:
                return 0
    return int(raw) if raw.isdigit() else 0


@app.command(name="apps")
def apps_command(
    min_size: str = typer.Option("100MB", "--min", help="Ignore apps smaller than this"),
    json_output: bool = typer.Option(False, "--json", help="Emit for a machine"),
) -> None:
    """Audit installed desktop applications by size, version, and last-modified date."""
    floor = _parse_size(min_size)
    if sys.platform == "win32":
        items = apps_windows.scan_applications(min_size=floor)
    else:
        items = apps_mod.scan_applications(min_size=floor)

    if json_output:
        typer.echo(
            json.dumps(
                [
                    {
                        "name": a.name,
                        "path": str(a.path),
                        "bytes": a.bytes_used,
                        "version": a.version,
                        "modified": a.modified.isoformat(),
                        "accessed": a.accessed.isoformat(),
                        "is_dormant": a.is_dormant,
                    }
                    for a in items
                ],
                indent=2,
            )
        )
        return

    table = Table(title="Installed Applications", title_style="bold")
    table.add_column("Application")
    table.add_column("Size", justify="right")
    table.add_column("Version", style="dim")
    table.add_column("Last Modified", style="dim")
    table.add_column("Status", justify="right")

    dormant_bytes = 0
    for a in items:
        status = "[yellow]dormant[/yellow]" if a.is_dormant else "[green]active[/green]"
        if a.is_dormant:
            dormant_bytes += a.bytes_used
        table.add_row(
            a.name,
            human(a.bytes_used),
            a.version,
            a.modified.strftime("%Y-%m-%d"),
            status,
        )

    console.print(table)
    total_bytes = sum(a.bytes_used for a in items)
    console.print(
        f"\n[bold]{human(total_bytes)}[/bold] across {len(items)} applications · "
        f"[yellow]{human(dormant_bytes)}[/yellow] in dormant apps (>180 days untouched)"
    )


@app.command(name="downloads")
def downloads_command(
    min_size: str = typer.Option("20MB", "--min", help="Ignore files smaller than this"),
    json_output: bool = typer.Option(False, "--json", help="Emit for a machine"),
) -> None:
    """Audit ~/Downloads directory, categorizing large files and checking for Google Drive duplicates."""
    floor = _parse_size(min_size)
    items = downloads_mod.scan_downloads(min_size=floor)

    if json_output:
        typer.echo(
            json.dumps(
                [
                    {
                        "name": item.name,
                        "path": str(item.path),
                        "bytes": item.bytes_used,
                        "category": str(item.category),
                        "is_dir": item.is_dir,
                        "cloud_duplicate": str(item.cloud_duplicate_path) if item.cloud_duplicate_path else None,
                    }
                    for item in items
                ],
                indent=2,
            )
        )
        return

    table = Table(title="Downloads Triage", title_style="bold")
    table.add_column("Item")
    table.add_column("Category", style="dim")
    table.add_column("Size", justify="right")
    table.add_column("Cloud Backup Status", justify="right")

    dupe_bytes = 0
    for item in items:
        if item.has_cloud_duplicate and item.cloud_duplicate_path:
            dupe_bytes += item.bytes_used
            status = f"[bold green]duplicate in Drive[/bold green] ({item.cloud_duplicate_path.name})"
        else:
            status = "[dim]unique local file[/dim]"
        table.add_row(
            item.name,
            str(item.category),
            human(item.bytes_used),
            status,
        )

    console.print(table)
    total_bytes = sum(item.bytes_used for item in items)
    console.print(
        f"\n[bold]{human(total_bytes)}[/bold] in large downloads · "
        f"[bold green]{human(dupe_bytes)}[/bold green] already backed up to Google Drive"
    )


_ = app.command(name="orca")(orca_cli.orca_layout)
app.add_typer(wrapup_cli.wrapup_app, name="wrapup")
_ = app.command(name="orca-worktrees")(orca_worktrees_cli.orca_worktrees)


@app.command()
def volume(
    drive: str = typer.Option("C", "--drive", help="Drive letter to scan"),
    csv: str | None = typer.Option(None, "--csv", help="Reuse an existing WizTree folder export instead of scanning"),
    keep_csv: bool = typer.Option(False, "--keep-csv", help="Keep the export (it is ~400 MB) for a later --csv"),
    code_root: str | None = typer.Option(None, "--code-root", help="Clone root for the dev-artefact partition"),
    min_size: str = typer.Option("5GB", "--min", help="Smallest folder shown in the drill-down"),
    depth: int = typer.Option(4, "--depth", help="Drill-down depth below the drive root"),
    json_output: bool = typer.Option(False, "--json", help="Emit for a machine"),
) -> None:
    """(Windows) Where a whole drive's space went: drill-down, dev-artefact partition, known hogs."""
    request = volume_report.VolumeRequest(
        drive=drive,
        csv=pathlib.Path(csv) if csv else None,
        keep_csv=keep_csv,
        code_root=pathlib.Path(code_root) if code_root else roots.code_root(),
        min_bytes=_parse_size(min_size),
        min_label=min_size,
        depth=depth,
    )
    volume_report.run(request, json_output=json_output, console=console, err=err)


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
