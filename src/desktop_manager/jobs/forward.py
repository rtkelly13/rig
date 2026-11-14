"""Compatibility spelling for the coordinator commands owned by C#."""

from __future__ import annotations

import typer

from rig import csharp_cli


def uninstall(ctx: typer.Context) -> None:
    """Remove the coordinator's scheduled tasks through the C# owner."""
    raise typer.Exit(code=csharp_cli.run(["jobs", "uninstall", *ctx.args]))
