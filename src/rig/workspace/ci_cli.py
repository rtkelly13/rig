"""CLI commands for auditing and reporting CI utilisation across estate repos."""

from __future__ import annotations

import pathlib

import typer
from rich.console import Console
from rich.table import Table

from rig.estate.ci_telemetry import (
    RunTelemetry,
    fetch_repo_runs_via_gh,
    fetch_run_jobs_via_gh,
    parse_run_telemetry,
)
from rig.repo_slug import RepoSlug
from rig.workspace.engine import estate_root_path, load_managed_specs

ci_cli = typer.Typer(
    name="ci",
    help="Audit and analyze CI utilisation and runner costs across the estate.",
    no_args_is_help=True,
    add_completion=False,
)
console = Console()
err = Console(stderr=True)


@ci_cli.command(name="audit")
def audit_ci_command(
    repo: str | None = typer.Option(None, "--repo", "-r", help="Specific repository name (e.g. shared-utilities)"),
    limit: int = typer.Option(10, "--limit", "-l", help="Number of recent workflow runs to evaluate per repo"),
    config: str | None = typer.Option(None, "--config", "-c", help="Path to managed-projects.json registry file"),
    root: str | None = typer.Option(None, "--root", help="Path to estate root directory"),
) -> None:
    """Audit recent CI runs and billable runner minutes."""
    estate_dir = estate_root_path(root)
    default_config = estate_dir / "shared-utilities" / "config" / "managed-projects.json"
    cfg_path = pathlib.Path(config).resolve() if config else default_config
    specs = load_managed_specs(cfg_path)

    # Filter specs if single repo requested
    if repo:
        specs = [s for s in specs if s.name.lower() == repo.lower() or repo.lower() in s.repo.lower()]
        if not specs:
            err.print(f"[red]Error: repository '{repo}' not found in managed projects.[/red]")
            raise typer.Exit(1)

    table = Table(title="Estate CI Utilisation Audit", expand=True)
    table.add_column("Repository", style="cyan", no_wrap=True)
    table.add_column("Workflow", style="white")
    table.add_column("Event / Branch", style="blue")
    table.add_column("Conclusion", style="bold")
    table.add_column("Wall Time", justify="right")
    table.add_column("Billable Equiv.", justify="right", style="green")

    total_billable_secs = 0.0
    total_runs_checked = 0

    for spec in specs:
        slug = RepoSlug.from_remote(spec.repo)
        if slug is None:
            continue
        repo_slug = slug.full_name

        raw_runs = fetch_repo_runs_via_gh(repo_slug, limit=limit)
        for raw_run in raw_runs:
            raw_jobs = fetch_run_jobs_via_gh(repo_slug, raw_run.id)
            telemetry: RunTelemetry = parse_run_telemetry(spec.name, raw_run, raw_jobs)

            total_billable_secs += telemetry.total_billable_seconds
            total_runs_checked += 1

            if telemetry.conclusion == "success":
                status_style = "green"
            elif telemetry.conclusion == "failure":
                status_style = "red"
            else:
                status_style = "yellow"

            wall_min = int(telemetry.run_duration_seconds // 60)
            wall_sec = int(telemetry.run_duration_seconds % 60)
            wall_str = f"{wall_min}m {wall_sec}s"

            billable_min = int(telemetry.total_billable_seconds // 60)
            billable_sec = int(telemetry.total_billable_seconds % 60)
            billable_str = f"{billable_min}m {billable_sec}s"

            table.add_row(
                spec.name,
                telemetry.workflow_name,
                f"{telemetry.event} ({telemetry.head_branch[:18]})",
                f"[{status_style}]{telemetry.conclusion}[/{status_style}]",
                wall_str,
                billable_str,
            )

    console.print(table)
    console.print(
        f"\n[bold]Total Evaluated:[/bold] {total_runs_checked} runs | "
        f"[bold green]Total Billable Minutes Equivalent:[/bold green] {total_billable_secs / 60.0:.1f}m\n"
    )
