"""CLI interface for managing the personal virtual monorepo estate.

Enforces ADR 0017: Nothing exists in the personal folder without the CLI managing it.
"""

from __future__ import annotations

import json
import pathlib

import typer
from rich.markup import escape
from rich.panel import Panel
from rich.table import Table

from rig import csharp_cli
from rig.console_ui import make_console
from rig.repo_governance.gitignore_manager import (
    audit_estate_gitignores,
    sync_estate_gitignores,
)
from rig.workspace.ci_cli import ci_cli
from rig.workspace.engine import (
    estate_root_path,
    load_managed_specs,
    sync_estate_code_workspace,
    sync_estate_router,
)
from rig.workspace.orca_cli import orca_cli
from rig.workspace.packages_cli import packages_cli

app = typer.Typer(
    name="workspace",
    help="Virtual monorepo estate management for the personal folder.",
    no_args_is_help=True,
    add_completion=False,
)
csharp_cli.add_stub(app, "skills")
csharp_cli.add_stub(app, "policy")
csharp_cli.add_group_stubs(app, "workspace")
app.add_typer(ci_cli, name="ci")
app.add_typer(packages_cli, name="packages")
app.add_typer(packages_cli, name="artifacts")
app.add_typer(orca_cli, name="orca")
gitignore_app = typer.Typer(
    name="gitignore",
    help="Audit and synchronize common .gitignore rules across estate repositories.",
    no_args_is_help=True,
)
app.add_typer(gitignore_app, name="gitignore")
console = make_console()


@gitignore_app.command(name="audit")
def gitignore_audit_command(
    root: str | None = typer.Option(None, "--root", "-r", help="Path to personal estate root directory"),
    repo: str | None = typer.Option(None, "--repo", help="Check only this repository name"),
    language: str | None = typer.Option(
        None, "--language", "-l", help="Language preset(s) to enforce (e.g. 'python', 'dotnet', 'node')"
    ),
    config: str | None = typer.Option(None, "--config", "-c", help="Path to managed-projects.json registry file"),
    json_output: bool = typer.Option(False, "--json", help="Output audit report as JSON"),
    strict: bool = typer.Option(False, "--strict", help="Fail with non-zero exit code if drift is detected"),
) -> None:
    """Audit common .gitignore rules across estate repositories."""
    estate_dir = estate_root_path(root)
    cfg_path = pathlib.Path(config).resolve() if config else None
    specs = load_managed_specs(cfg_path)
    target_names = [repo] if repo else [s.name for s in specs]
    languages = [part.strip() for part in language.split(",") if part.strip()] if language else None

    results = audit_estate_gitignores(estate_dir, repos=target_names, languages=languages)
    has_drift = any(not r.result.is_aligned for r in results)

    if json_output:
        typer.echo(json.dumps([r.to_json() for r in results], indent=2))
        if strict and has_drift:
            raise typer.Exit(1)
        return

    console.print(Panel.fit(f"[bold cyan]Estate .gitignore Audit: {estate_dir}[/bold cyan]", border_style="cyan"))

    table = Table(title="Managed Repositories .gitignore Alignment")
    table.add_column("Repository", style="bold white")
    table.add_column("Languages", style="cyan")
    table.add_column("Status", style="bold")
    table.add_column("Missing Patterns")

    for r in results:
        status = r.result.status
        missing = r.result.missing_lines
        langs = ", ".join(r.result.languages) or "[dim]common[/dim]"
        details = escape(", ".join(missing)) if missing else "[dim]None (Compliant)[/dim]"
        style = "green" if r.result.is_aligned else "yellow"
        table.add_row(r.repo_name, langs, f"[{style}]{escape(status)}[/{style}]", details)

    console.print(table)

    if has_drift:
        console.print("\n[yellow]Run 'workspace gitignore sync' or 'workspace sync' to align missing rules.[/yellow]")
        if strict:
            raise typer.Exit(1)
    else:
        console.print("\n[bold green]✓ All repositories have compliant common .gitignore entries.[/bold green]")


@gitignore_app.command(name="sync")
def gitignore_sync_command(
    root: str | None = typer.Option(None, "--root", "-r", help="Path to personal estate root directory"),
    repo: str | None = typer.Option(None, "--repo", help="Synchronize only this repository name"),
    language: str | None = typer.Option(
        None, "--language", "-l", help="Language preset(s) to enforce (e.g. 'python', 'dotnet', 'node')"
    ),
    config: str | None = typer.Option(None, "--config", "-c", help="Path to managed-projects.json registry file"),
    dry_run: bool = typer.Option(False, "--dry-run", help="Preview mutations without applying changes"),
    json_output: bool = typer.Option(False, "--json", help="Output sync results as JSON"),
) -> None:
    """Synchronize common .gitignore rules across estate repositories."""
    estate_dir = estate_root_path(root)
    cfg_path = pathlib.Path(config).resolve() if config else None
    specs = load_managed_specs(cfg_path)
    target_names = [repo] if repo else [s.name for s in specs]
    languages = [part.strip() for part in language.split(",") if part.strip()] if language else None

    results = sync_estate_gitignores(estate_dir, repos=target_names, dry_run=dry_run, languages=languages)

    if json_output:
        typer.echo(json.dumps([r.to_json() for r in results], indent=2))
        return

    mode_title = "DRY-RUN" if dry_run else "APPLY"
    console.print(
        Panel.fit(f"[bold cyan]Estate .gitignore Sync ({mode_title}): {estate_dir}[/bold cyan]", border_style="cyan")
    )

    table = Table(title="Synchronization Results")
    table.add_column("Repository", style="bold white")
    table.add_column("Languages", style="cyan")
    table.add_column("Action", style="bold")
    table.add_column("Patterns Added")

    for r in results:
        status = r.result.status
        added = r.result.added
        langs = ", ".join(r.result.languages) or "[dim]common[/dim]"
        details = escape(", ".join(added)) if added else "[dim]None (already compliant)[/dim]"
        style = "green" if status == "aligned" else "dim"
        table.add_row(r.repo_name, langs, f"[{style}]{escape(status)}[/{style}]", details)

    console.print(table)
    updated_count = sum(1 for r in results if r.result.status == "aligned")
    if updated_count > 0:
        action_verb = "Would update" if dry_run else "Updated"
        console.print(
            f"\n[bold green]✓ {action_verb} .gitignore in {updated_count} repository/repositories.[/bold green]"
        )
    else:
        console.print("\n[bold green]✓ All repositories already have compliant .gitignore files.[/bold green]")


@app.command(name="sync")
def sync_command(
    root: str | None = typer.Option(None, "--root", "-r", help="Path to personal estate root directory"),
    config: str | None = typer.Option(None, "--config", "-c", help="Path to managed-projects.json registry file"),
    gitignore: bool = typer.Option(
        True, "--gitignore/--no-gitignore", help="Synchronize common gitignores across managed projects"
    ),
) -> None:
    """Synchronize root AGENTS.md, personal.code-workspace, and common gitignores."""
    estate_dir = estate_root_path(root)
    cfg_path = pathlib.Path(config).resolve() if config else None
    specs = load_managed_specs(cfg_path)

    console.print(f"[bold cyan]Synchronizing estate orchestration in {estate_dir}...[/bold cyan]\n")

    # 1. Sync AGENTS.md (read natively by Claude Code; no CLAUDE.md shim)
    agents_path = sync_estate_router(estate_dir, specs)
    console.print(f"  ✓ Generated [green]{agents_path.name}[/green] ({len(specs)} managed projects mapped)")

    # 2. Sync personal.code-workspace
    code_ws_path = sync_estate_code_workspace(estate_dir, specs)
    console.print(f"  ✓ Synchronized [green]{code_ws_path.name}[/green]")

    # 3. Sync common .gitignore entries across managed repositories
    if gitignore:
        managed_names = [s.name for s in specs]
        results = sync_estate_gitignores(estate_dir, repos=managed_names, dry_run=False)
        updated = [r.repo_name for r in results if r.result.status == "aligned"]
        if updated:
            console.print(
                f"  ✓ Synchronized [green].gitignore[/green] across {len(results)} repos "
                f"({len(updated)} updated: {', '.join(updated)})"
            )
        else:
            console.print(f"  ✓ Verified [green].gitignore[/green] across {len(results)} repos (all compliant)")

    console.print("\n[bold green]Estate orchestration successfully synchronized.[/bold green]")


def main() -> None:
    app()


if __name__ == "__main__":
    main()
