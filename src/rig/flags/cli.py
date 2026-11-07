"""`rig flags`: list feature flags and store overrides in rig.db."""

from __future__ import annotations

import typer
from rich.console import Console
from rich.table import Table

from rig.flags.registry import UnknownFlagError
from rig.flags.store import clear_override, flag_states, set_override

console = Console()
app = typer.Typer(
    help="Feature flags shared across the rig stack (defaults in code, overrides in rig.db).",
    no_args_is_help=True,
)


def _on_off(value: bool) -> str:
    return "on" if value else "off"


@app.command("list")
def list_cmd() -> None:
    """Show every flag, its default, any stored override and the effective state."""
    table = Table()
    table.add_column("Flag", no_wrap=True)
    for heading in ("Effective", "Default", "Override", "Description"):
        table.add_column(heading)
    for state in flag_states():
        override = "-" if state.override is None else _on_off(state.override)
        table.add_row(
            state.flag.name,
            _on_off(state.enabled),
            _on_off(state.flag.default),
            override,
            state.flag.description,
        )
    console.print(table)


def _set(name: str, enabled: bool) -> None:
    try:
        set_override(name, enabled)
    except UnknownFlagError as error:
        console.print(f"[red]{error}[/red]")
        raise typer.Exit(code=1) from error
    console.print(f"{name}: {_on_off(enabled)} (stored override)")


@app.command("enable")
def enable_cmd(name: str = typer.Argument(..., help="Flag name")) -> None:
    """Store an override switching the flag on."""
    _set(name, True)


@app.command("disable")
def disable_cmd(name: str = typer.Argument(..., help="Flag name")) -> None:
    """Store an override switching the flag off."""
    _set(name, False)


@app.command("reset")
def reset_cmd(name: str = typer.Argument(..., help="Flag name")) -> None:
    """Remove the stored override so the default applies."""
    try:
        removed = clear_override(name)
    except UnknownFlagError as error:
        console.print(f"[red]{error}[/red]")
        raise typer.Exit(code=1) from error
    console.print(f"{name}: {'override removed' if removed else 'no override stored'}")
