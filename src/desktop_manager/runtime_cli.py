"""Compatibility spellings for the C# runtime command."""

from __future__ import annotations

import typer

from desktop_manager import runtime_forward

runtime_app = typer.Typer(
    name="runtime",
    no_args_is_help=True,
    help="The pinned copy of this repo that the scheduled tasks run from.",
)

for name, callback in (
    ("install", runtime_forward.install),
    ("rollback", runtime_forward.rollback),
    ("status", runtime_forward.status),
):
    runtime_app.command(
        name=name,
        context_settings={"allow_extra_args": True, "ignore_unknown_options": True},
        add_help_option=False,
    )(callback)
