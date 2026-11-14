"""Compatibility spellings for the C# jobs command."""

from __future__ import annotations

import typer

from desktop_manager.jobs import forward

jobs_app = typer.Typer(
    name="jobs",
    no_args_is_help=True,
    help="Logon, every-15-minute and daily jobs that keep this machine aligned, run from one registry.",
)

for name, callback in (
    ("install", forward.install),
    ("uninstall", forward.uninstall),
    ("status", forward.status),
    ("last", forward.last),
    ("log", forward.log),
    ("run", forward.run),
):
    jobs_app.command(
        name=name,
        context_settings={"allow_extra_args": True, "ignore_unknown_options": True},
        add_help_option=False,
    )(callback)
