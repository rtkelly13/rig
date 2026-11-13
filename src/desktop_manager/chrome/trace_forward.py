"""Compatibility spelling for the canonical C# trace reports; no report implementation lives here."""

from __future__ import annotations

import typer

from rig import csharp_cli


def forward(ctx: typer.Context) -> None:
    """Pass the entire line, including help and unknown options, to the canonical parser."""
    raise typer.Exit(code=csharp_cli.run(["chrome-tabs", "traces", *ctx.args]))
