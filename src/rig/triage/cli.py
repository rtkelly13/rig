"""`rig triage`: refresh the saved queue or verify Markdown without modifying it."""

from __future__ import annotations

import sqlite3
import subprocess
from pathlib import Path

import typer

from rig.triage import markdown, service

app = typer.Typer(no_args_is_help=True, help="Persistent PR and issue triage queue.")


@app.command()
def refresh(
    db: Path | None = typer.Option(None, help="Rig SQLite database override."),
    output: Path | None = typer.Option(None, help="Transient queue Markdown override."),
    config: Path | None = typer.Option(None, help="Managed project registry override."),
) -> None:
    """Refresh managed repositories and update the Markdown projection."""
    try:
        result = service.refresh(db, output, config)
    except (OSError, ValueError, sqlite3.Error, subprocess.SubprocessError) as error:
        typer.echo(f"Triage refresh failed: {error}", err=True)
        raise typer.Exit(1) from error
    typer.echo(f"Tracked {len(result)} items; queue: {output or service.queue_path()}")


@app.command()
def verify(
    path: Path | None = typer.Argument(None, help="Queue Markdown; defaults to rig's local transient queue."),
) -> None:
    """Check local Markdown structure and deterministic order. Never update any file or DB."""
    target = path or service.queue_path()
    try:
        markdown.verify(target.read_text(encoding="utf-8"))
    except (OSError, ValueError) as error:
        typer.echo(f"Invalid triage queue: {error}", err=True)
        raise typer.Exit(1) from error
    typer.echo(f"Valid triage queue: {target}")
