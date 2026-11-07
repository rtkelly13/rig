"""Tools installation step."""

from __future__ import annotations

from rich.console import Console
from rich.table import Table

from ..config.loader import load_all_builtin_tools
from ..config.models import ToolDefinition
from ..engine.context import StepContext
from ..tool_manager import ToolManager

console = Console()


def run_tools_setup(ctx: StepContext) -> None:
    """Check and install development tools."""
    tool_mgr = ToolManager(ctx.platform)
    all_tools = load_all_builtin_tools()

    table = Table(title="Development Tools Status", show_header=True)
    table.add_column("Tool", style="cyan")
    table.add_column("Category", style="magenta")
    table.add_column("Status", justify="center")
    table.add_column("Description", style="dim")

    to_install: list[ToolDefinition] = []

    for tool in all_tools:
        installed = tool_mgr.is_tool_installed(tool)
        status_str = "[green]Installed[/green]" if installed else "[dim]Not Installed[/dim]"
        table.add_row(tool.name, tool.category, status_str, tool.description)

        # In express/agent mode, install required tools + role matches
        if not installed and (ctx.mode in ("express", "agent") and tool.required or ctx.mode == "full"):
            to_install.append(tool)

    console.print(table)

    if not to_install:
        console.print("[green]All target tools are already installed![/green]")
        return

    if ctx.dry_run:
        console.print(f"[dim]Dry run: would install {len(to_install)} tool(s).[/dim]")
        return

    console.print(f"\n[cyan]Installing {len(to_install)} missing tool(s)...[/cyan]")
    for tool in to_install:
        console.print(f"Installing [bold]{tool.name}[/bold]...")
        success = tool_mgr.install_tool(tool, silent=ctx.use_silent_mode)
        if success:
            console.print(f"[green]✓ Installed {tool.name}[/green]")
        else:
            console.print(f"[yellow]! Failed to install {tool.name} via package manager.[/yellow]")
