"""Compatibility spellings for the runtime commands owned by C#."""

from __future__ import annotations

import typer

from rig import csharp_cli


def install(ctx: typer.Context) -> None:
    """Build a copy of one commit and make it current through the C# owner."""
    raise typer.Exit(code=csharp_cli.run(["runtime", "install", *ctx.args]))


def rollback(ctx: typer.Context) -> None:
    """Point the scheduled tasks back at the previous copy through the C# owner."""
    raise typer.Exit(code=csharp_cli.run(["runtime", "rollback", *ctx.args]))


def status(ctx: typer.Context) -> None:
    """Show the copy in use through the C# owner."""
    raise typer.Exit(code=csharp_cli.run(["runtime", "status", *ctx.args]))
