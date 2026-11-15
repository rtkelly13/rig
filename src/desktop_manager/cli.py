"""CLI entry point for the desktop-manager windows, tabs and web tools.

The workspace layout manager and display guard moved to C# as ``rig desktop workspace ...``
(issue #243), and the Claude session commands were retired in #224; this CLI keeps the
groups that still run in Python.
"""

from __future__ import annotations

import sys

import typer
from typer.core import TyperGroup, TyperOption

from desktop_manager.chrome.cli import chrome_tabs_app
from desktop_manager.cli_common import console
from desktop_manager.jobs.cli import jobs_app
from desktop_manager.runtime_cli import runtime_app
from desktop_manager.web_cli import web_app
from desktop_manager.work.cli import work_app
from rig import tracing

app = typer.Typer(name="desktop-manager", no_args_is_help=True, add_completion=False)

app.add_typer(chrome_tabs_app)
app.add_typer(runtime_app)
app.add_typer(work_app)
app.add_typer(jobs_app)
app.add_typer(web_app)


@app.command(name="help")
def help_cmd() -> None:
    """Show a compact overview of every command, option, and subcommand."""

    from rich.text import Text

    # --- header ---
    console.print()
    console.print(
        Text("desktop-manager", style="bold cyan"),
        Text(" — shared-utilities virtual monorepo CLI", style="dim"),
    )
    console.print()

    # --- render a command group ---
    def _render_group(group_app: typer.Typer, group_name: str) -> None:
        group = typer.main.get_command(group_app)
        commands = group.commands if isinstance(group, TyperGroup) else {group.name or group_name: group}
        for name, command in commands.items():
            doc = (command.help or "").strip().split("\n")[0]

            console.print(f"  [green]{group_name} {name}[/green]")
            console.print(f"    {doc}")

            for param in command.params:
                if isinstance(param, TyperOption):
                    flags = ", ".join([*param.opts, *param.secondary_opts])
                    console.print(f"      [cyan]{flags}[/cyan]  [dim]{param.help or ''}[/dim]")

            console.print()

    # --- chrome tab commands ---
    console.rule("[bold]Chrome tab commands[/bold]", style="dim")
    console.print()
    _render_group(chrome_tabs_app, "chrome-tabs")
    console.print("  Trace reports: rig chrome-tabs traces --help\n")

    # --- work item commands ---
    console.rule("[bold]Work item commands[/bold]", style="dim")
    console.print()
    _render_group(work_app, "work")

    # --- machine jobs ---
    console.rule("[bold]Machine jobs[/bold]", style="dim")
    console.print()
    _render_group(jobs_app, "jobs")

    # --- usage hint ---
    console.print("[dim]Workspace layouts and the display guard:[/dim] rig desktop workspace --help")
    console.print("[dim]Run[/dim] desktop-manager <group> <command> --help [dim]for full details.[/dim]")
    console.print()


def main() -> None:
    """Entry point for the desktop-manager CLI.

    Each run is one trace: a root span named after the subcommand, with the
    per-window and per-tab work, virtual-desktop calls, and every helper subprocess
    as children. Spans land in ``temp/traces/sdk/desktop-manager/``, or, under the
    jobs coordinator from a runtime copy, in
    ``%LOCALAPPDATA%/shared-utilities/traces/desktop-manager/``.
    """
    tracing.init_tracing("desktop-manager")
    command = " ".join(arg for arg in sys.argv[1:3] if not arg.startswith("-"))
    with tracing.span(f"desktop-manager {command}".rstrip(), scope="desktop_manager.cli", **{"cli.argv": sys.argv[1:]}):
        app()


if __name__ == "__main__":
    main()
