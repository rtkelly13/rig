"""AI coding tools configuration step."""

from __future__ import annotations

from pathlib import Path

from rich.console import Console
from rich.table import Table

from rig import csharp_cli

from ...engine.context import StepContext
from .agent_launcher_setup import setup_agent_launcher
from .claude_code import setup_claude_code
from .discovery import detect_installed_agents
from .estate_rules import setup_estate_rules
from .obsidian_plugins import setup_obsidian_plugins
from .opencode import setup_opencode

console = Console()


def run_ai_tools_setup(ctx: StepContext) -> None:
    """Configure AI agent environments and baselines."""
    # 1. Report installed agents
    agents = detect_installed_agents()
    table = Table(title="Detected AI Coding Assistants", show_header=True)
    table.add_column("Assistant", style="cyan")
    table.add_column("Command", style="dim")
    table.add_column("Status", justify="center")

    for a in agents:
        status_str = "[green]Found[/green]" if a.is_installed else "[dim]Not Found[/dim]"
        table.add_row(a.display_name, a.command, status_str)

    console.print(table)
    console.print()

    # 2. Configure Claude Code settings baseline
    setup_claude_code(dry_run=ctx.dry_run)

    # 3. Configure OpenCode
    setup_opencode(dry_run=ctx.dry_run)

    # 4. Configure agent-browser profiles through `rig browser profiles`
    dry_run = ["--dry-run"] if ctx.dry_run else []
    failed = {"browser profiles": csharp_cli.run(["browser", "profiles", *dry_run])}

    # 5. Estate-wide agent rules for ~/code/personal
    setup_estate_rules(dry_run=ctx.dry_run)

    # 6. Obsidian plugins for the agent-facing vault
    vault = Path.home() / "code" / "personal" / "obsidian-vault"
    if (vault / ".obsidian").is_dir():
        console.print("\n[cyan]Installing Obsidian plugins...[/cyan]")
        setup_obsidian_plugins(vault, dry_run=ctx.dry_run)

    # 7. Link skills into every agent CLI through `rig skills sync`
    console.print("\n[cyan]Linking skills into agent CLIs...[/cyan]")
    failed["skills sync"] = csharp_cli.run(["skills", "sync", "--system", "all", *dry_run])

    # 8. Agent launcher root shim and shell shortcut
    console.print("\n[cyan]Configuring agent launcher root shim & shell shortcut...[/cyan]")
    setup_agent_launcher(dry_run=ctx.dry_run)

    # Every part runs even after one fails; the step then reports each failure.
    failures = [f"rig {command} exited {code}" for command, code in failed.items() if code != 0]
    if failures:
        raise RuntimeError("; ".join(failures))
