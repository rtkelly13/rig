"""`vault` — link health, orphans, metadata and filename maintenance for the Obsidian vault.

Ported from the retired `the-vault-system`, which was thinned to this one job before
being folded in here. Everything reports before it repairs: each `--apply` command is
read-only without the flag.

The vault exists twice — see `config.py`. Maintenance targets the git copy.
"""

from __future__ import annotations

import sys

import typer

from rig.obsidian.commands import graph_app

app = typer.Typer(
    name="vault",
    help="Obsidian vault maintenance: links, orphans, metadata, filenames.",
    no_args_is_help=True,
    rich_markup_mode="rich",
)
app.add_typer(graph_app, name="graph")


@app.command("paths")
def show_paths() -> None:
    """Show which vault maintenance targets, and where the Drive copy is."""
    from rich.console import Console

    from rig.obsidian.config import Config

    console = Console()
    cfg = Config()
    console.print(f"[cyan]maintained[/cyan]  {cfg.vault_path}")
    console.print(f"[dim]drive copy[/dim]  {cfg.drive_vault_path}")


def md_lint_entry() -> None:
    """Standalone entry point: `md-lint [paths...] [--fix]`."""
    import subprocess

    from rig.obsidian.config import get_vault_path

    fix = "--fix" in sys.argv
    raw = [a for a in sys.argv[1:] if a != "--fix"]
    if raw:
        targets = raw
    else:
        vault = get_vault_path()
        if not vault.exists():
            sys.stderr.write(f"Vault not found at {vault} — set VAULT_PATH to override.\n")
            sys.exit(1)
        targets = [str(vault)]
    sys.exit(subprocess.run(["mdformat", *([] if fix else ["--check"]), *targets], check=False).returncode)


def main() -> None:
    app()


if __name__ == "__main__":
    main()
