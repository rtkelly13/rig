"""`vault` — link health, orphans, metadata and filename maintenance for the Obsidian vault.

Ported from the retired `the-vault-system`, which was thinned to this one job before
being folded in here. Everything reports before it repairs: each `--apply` command is
read-only without the flag.

The vault exists twice — see `config.py`. Maintenance targets the git copy.
"""

from __future__ import annotations

import typer

from rig.obsidian.commands import graph_app
from rig.obsidian.commands.knowledge_server import server_app

app = typer.Typer(
    name="vault",
    help="Obsidian vault maintenance: links, orphans, metadata, filenames.",
    no_args_is_help=True,
    rich_markup_mode="rich",
)
app.add_typer(graph_app, name="graph")
app.add_typer(server_app, name="knowledge-server")


@app.command("paths")
def show_paths() -> None:
    """Show which vault maintenance targets, and where the Drive copy is."""
    from rich.console import Console

    from rig.obsidian.config import Config

    console = Console()
    cfg = Config()
    console.print(f"[cyan]maintained[/cyan]  {cfg.vault_path}")
    console.print(f"[dim]drive copy[/dim]  {cfg.drive_vault_path}")


def main() -> None:
    app()


if __name__ == "__main__":
    main()
