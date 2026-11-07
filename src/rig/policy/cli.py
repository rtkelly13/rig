"""CLI for inspecting estate-wide command and skill allowlists."""

from __future__ import annotations

from pathlib import Path

import typer
from rich.console import Console
from rich.table import Table

from rig.policy.engine import audit_project_commands, check_command, load_policy, summarize_history
from rig.policy.models import ProjectionStatus
from rig.policy.projections import project_native_permissions

policy_cli = typer.Typer(name="policy", help="Audit and manage estate-wide CLI allowlists.", no_args_is_help=True)
console = Console()


def _resolve_systems(system_args: list[str] | None) -> list[str]:
    """Normalize CLI surface names, including the user-facing AGY alias."""
    if not system_args:
        return ["opencode", "claude", "codex", "antigravity"]
    resolved: list[str] = []
    for value in system_args:
        name = "antigravity" if value.strip().lower() == "agy" else value.strip().lower()
        if name == "all":
            return ["opencode", "claude", "codex", "antigravity"]
        if name not in {"opencode", "claude", "codex", "antigravity"}:
            console.print(f"[red]Unknown CLI system:[/red] {value}")
            raise typer.Exit(code=2)
        resolved.append(name)
    return resolved


@policy_cli.command("check")
def check(command: str = typer.Argument(..., help="Command string to classify.")) -> None:
    """Classify a command as safe, local, external, or denied."""
    risk = check_command(command)
    console.print(f"{risk.value}: {command}")
    if risk.value == "denied":
        raise typer.Exit(code=1)


@policy_cli.command("audit")
def audit(
    root: Path = typer.Option(Path.home() / "code" / "personal", "--root", help="Estate root to inspect."),
) -> None:
    """Show manifest commands and normalized command-family history counts."""
    commands = audit_project_commands(root)
    table = Table(title=f"Estate Commands ({len(commands)})")
    table.add_column("Project")
    table.add_column("Command")
    table.add_column("Risk")
    for item in commands:
        table.add_row(item.project, item.invocation, item.risk.value)
    console.print(table)
    history = summarize_history()
    console.print("\nNormalized shell-history families:")
    if history:
        for family, count in history.most_common():
            console.print(f"  {family}: {count}")
    else:
        console.print("  no matching history entries found")


@policy_cli.command("list")
def list_rules() -> None:
    """List the configured command rules and active skill count."""
    policy = load_policy()
    console.print(f"Active skills: {len(policy.skills_allowlist)}")
    for rule in policy.command_rules:
        console.print(f"{rule.risk.value:8} {rule.pattern}")


@policy_cli.command("sync")
def sync_permissions(
    system: list[str] = typer.Option(
        None,
        "--system",
        "-s",
        help="Permission surfaces to update (opencode, claude, codex, agy, all).",
    ),
    dry_run: bool = typer.Option(False, "--dry-run", help="Preview changes without writing native config."),
) -> None:
    """Project the checked-in policy into native agent permission files."""
    results = project_native_permissions(load_policy(), systems=_resolve_systems(system), dry_run=dry_run)
    for result in results:
        path = str(result.path) if result.path else "no native path"
        style = "green" if result.status in {ProjectionStatus.APPLIED, ProjectionStatus.UNCHANGED} else "yellow"
        console.print(f"[{style}]{result.system}: {result.status.value}[/{style}] — {path} ({result.detail})")
    if any(result.status is ProjectionStatus.FAILED for result in results):
        raise typer.Exit(code=1)
