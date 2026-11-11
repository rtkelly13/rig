"""Typer CLI entrypoint for development environment management."""

from __future__ import annotations

import typer
from rich.console import Console

from rig import csharp_cli

from .doctor.core import run_doctor
from .engine.context import StepContext, get_current_platform
from .engine.runner import run_steps
from .engine.step import Step
from .steps import ai_tools, fonts, git, github_cli, runtimes, terminal, tools
from .steps.ai_tools.claude_code import setup_claude_code
from .tool_manager import ToolManager

app = typer.Typer(
    name="devenv",
    help="Unified development environment manager (tools, runtimes, fonts, git, AI coding agents).",
    no_args_is_help=True,
)
console = Console()

# The order `devenv setup` runs in, and what `--step N` counts.
SETUP_STEPS: list[Step] = [
    Step(
        name="AI Coding Tools & Skills",
        description="Configure AI coding assistants (AGY, Claude, Codex, OpenCode) & link skills into them",
        func=ai_tools.run_ai_tools_setup,
        confirm_message="Would you like to configure AI coding tools and link skills into them?",
    ),
    Step(
        name="Font Installation",
        description="Install developer Nerd Fonts (JetBrainsMono, CascadiaCode, FiraCode)",
        func=fonts.run_fonts_setup,
        confirm_message="Would you like to install Nerd Fonts?",
    ),
    Step(
        name="Git & SSH Setup",
        description="Configure Git author info, conditional directory routing, and SSH keys",
        func=git.run_git_setup,
        confirm_message="Would you like to configure Git and SSH credentials?",
    ),
    Step(
        name="GitHub CLI Scopes",
        description="Install the personal gh scope overlay read by the workspace devenv",
        func=github_cli.run_github_cli_setup,
        confirm_message="Would you like to install the gh scope overlay?",
    ),
    Step(
        name="Terminal Customization",
        description="Configure prompt themes, shell profile integration, and terminal settings",
        func=terminal.run_terminal_setup,
        confirm_message="Would you like to configure terminal customization?",
        default_confirm=False,
    ),
    Step(
        name="Runtimes",
        description="Configure Python (uv) and Node.js (fnm) runtime environments",
        func=runtimes.run_runtimes_setup,
        confirm_message="Would you like to configure Python and Node.js runtimes?",
    ),
    Step(
        name="Development Tools",
        description="Install and update curated developer tools & CLI utilities",
        func=tools.run_tools_setup,
        confirm_message="Would you like to install/update development tools?",
    ),
]


@app.command("setup")
def setup_cmd(
    mode: str = typer.Option("full", "--mode", "-m", help="Setup mode: full, express, agent"),
    step: int = typer.Option(1, "--step", "-s", help="Start from step N"),
    dry_run: bool = typer.Option(False, "--dry-run", help="Report planned changes without modifying system"),
    interactive: bool = typer.Option(False, "--interactive", help="Prompt for interactive tool installs (Windows)"),
) -> None:
    """Run the development environment onboarding wizard."""
    platform = get_current_platform()
    ctx = StepContext(
        platform=platform,
        mode=mode,
        dry_run=dry_run,
        use_silent_mode=not interactive,
    )

    console.print(f"[bold cyan]Starting Development Environment Setup ({platform.value} - {mode} mode)[/bold cyan]")

    try:
        run_steps(ctx, SETUP_STEPS, start_step=step)
        console.print("\n[bold green]✓ Setup sequence complete![/bold green]")
    except KeyboardInterrupt:
        console.print("\n[yellow]Setup interrupted by user.[/yellow]")
        raise typer.Exit(code=130) from None


@app.command("update")
def update_cmd() -> None:
    """Update installed development tools. Skills are updated by `rig skills update`."""
    console.print("[bold cyan]Updating development tools...[/bold cyan]\n")
    ToolManager(get_current_platform()).update_all_tools()
    console.print("\n[bold green]✓ Environment update complete![/bold green]")


@app.command("doctor")
def doctor_cmd() -> None:
    """Validate environment health across tools, runtimes, git, fonts, and AI assistants.

    For lint, type and test gates on this repo, run `uv run status`.
    """
    raise typer.Exit(code=run_doctor())


@app.command("tools")
def tools_cmd(
    update: bool = typer.Option(
        False,
        "--update",
        "-u",
        help="Also upgrade every package the platform package manager owns (brew upgrade / winget upgrade --all)",
    ),
) -> None:
    """Inspect and manage development tools."""
    platform = get_current_platform()
    ctx = StepContext(platform=platform, mode="full")
    tools.run_tools_setup(ctx)
    if update and not ToolManager(platform).update_all_tools():
        console.print("[bold red]Package upgrade did not run: no package manager found, or it failed.[/bold red]")
        raise typer.Exit(code=1)


@app.command("runtimes")
def runtimes_cmd() -> None:
    """Configure Python (uv) and Node.js (fnm) runtime environments."""
    platform = get_current_platform()
    ctx = StepContext(platform=platform, mode="full")
    runtimes.run_runtimes_setup(ctx)


@app.command("fonts")
def fonts_cmd() -> None:
    """Install or check developer Nerd Fonts."""
    platform = get_current_platform()
    ctx = StepContext(platform=platform, mode="full")
    fonts.run_fonts_setup(ctx)


@app.command("terminal")
def terminal_cmd(
    dry_run: bool = typer.Option(False, "--dry-run", help="Report planned changes without modifying system"),
) -> None:
    """Configure terminal environment and emulator settings."""
    platform = get_current_platform()
    ctx = StepContext(platform=platform, mode="full", dry_run=dry_run)
    terminal.run_terminal_setup(ctx)


@app.command("git")
def git_cmd() -> None:
    """Configure Git identity and SSH keys."""
    platform = get_current_platform()
    ctx = StepContext(platform=platform, mode="full")
    git.run_git_setup(ctx)


@app.command("gh")
def gh_cmd(
    dry_run: bool = typer.Option(False, "--dry-run", help="Show what would be written without writing it"),
) -> None:
    """Install the personal gh scope overlay that the workspace devenv enforces."""
    platform = get_current_platform()
    ctx = StepContext(platform=platform, mode="full", dry_run=dry_run)
    github_cli.run_github_cli_setup(ctx)


@app.command("claude")
def claude_cmd(
    dry_run: bool = typer.Option(False, "--dry-run", help="Show what would change without writing it"),
) -> None:
    """Merge the Claude Code settings baseline and install the session-ledger SessionEnd hook."""
    setup_claude_code(dry_run=dry_run)


@app.command("ai")
def ai_cmd() -> None:
    """Configure AI coding assistants (AGY, Claude Code baseline, OpenCode, browser profiles, skills)."""
    platform = get_current_platform()
    ctx = StepContext(platform=platform, mode="full")
    ai_tools.run_ai_tools_setup(ctx)


csharp_cli.add_stub(app, "models")


def main() -> None:
    app()


if __name__ == "__main__":
    main()
