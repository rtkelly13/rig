"""Compatibility spelling for the coordinator commands owned by C#."""

from __future__ import annotations

import typer

from rig import csharp_cli


def uninstall(ctx: typer.Context) -> None:
    """Remove the coordinator's scheduled tasks through the C# owner."""
    raise typer.Exit(code=csharp_cli.run(["jobs", "uninstall", *ctx.args]))


def install(ctx: typer.Context) -> None:
    """Register the coordinator's scheduled tasks through the C# owner."""
    raise typer.Exit(code=csharp_cli.run(["jobs", "install", *ctx.args]))


def status(ctx: typer.Context) -> None:
    """Show the jobs' status."""
    raise typer.Exit(code=csharp_cli.run(["jobs", "status", *ctx.args]))


def last(ctx: typer.Context) -> None:
    """Show the latest run."""
    raise typer.Exit(code=csharp_cli.run(["jobs", "last", *ctx.args]))


def log(ctx: typer.Context) -> None:
    """Show a job's log."""
    raise typer.Exit(code=csharp_cli.run(["jobs", "log", *ctx.args]))


def run(ctx: typer.Context) -> None:
    """Run the due jobs."""
    raise typer.Exit(code=csharp_cli.run(["jobs", "run", *ctx.args]))
