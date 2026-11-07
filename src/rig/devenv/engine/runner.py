"""Step runner orchestrating interactive and batch execution with Rich output."""

from __future__ import annotations

from rich.console import Console
from rich.panel import Panel
from rich.prompt import Confirm

from .context import StepContext
from .step import Step

console = Console()


def print_step_header(step: Step, number: int, total_steps: int) -> None:
    """Render a clean Rich panel header for a step."""
    console.print()
    title = f"Step {number}/{total_steps}: {step.name}"
    console.print(
        Panel(
            f"[bold white]{step.description}[/bold white]",
            title=f"[bold cyan]{title}[/bold cyan]",
            border_style="cyan",
            expand=False,
        )
    )


def should_execute_step(step: Step, ctx: StepContext) -> bool:
    """Express mode takes each step's default, agent mode runs every step, full mode asks."""
    if ctx.mode == "express":
        return step.default_confirm

    if ctx.mode == "agent":
        return True

    prompt_msg = step.confirm_message or f"Would you like to run {step.name}?"
    return Confirm.ask(f"[bold yellow]{prompt_msg}[/bold yellow]", default=step.default_confirm)


def execute_step(step: Step, ctx: StepContext, number: int, total_steps: int) -> bool:
    """Execute a single step, handling confirmation and exceptions."""
    print_step_header(step, number, total_steps)

    if not should_execute_step(step, ctx):
        console.print(f"[dim]Skipped: {step.name}[/dim]")
        return True

    try:
        step.func(ctx)
    except Exception as e:
        console.print(f"[bold red]✗ Step '{step.name}' failed: {e}[/bold red]")
        # Agent mode has no one to answer a prompt: record the failure and carry on.
        if ctx.mode != "agent" and not Confirm.ask("[yellow]Continue with remaining steps?[/yellow]", default=True):
            raise
        return False
    console.print(f"[bold green]✓ Completed: {step.name}[/bold green]")
    return True


def run_steps(ctx: StepContext, steps: list[Step], start_step: int = 1) -> None:
    """Execute steps in order, numbered from 1, starting at ``start_step``."""
    total = len(steps)
    for number, s in enumerate(steps, start=1):
        if number >= start_step:
            execute_step(s, ctx, number, total)
