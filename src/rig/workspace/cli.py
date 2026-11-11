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
    audit_estate,
    clean_estate_item,
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


@app.command(name="audit")
def audit_command(
    root: str | None = typer.Option(None, "--root", "-r", help="Path to personal estate root directory"),
    config: str | None = typer.Option(None, "--config", "-c", help="Path to managed-projects.json registry file"),
    json_output: bool = typer.Option(False, "--json", help="Output audit report as JSON"),
    strict: bool = typer.Option(False, "--strict", help="Fail with non-zero exit code if unmanaged items exist"),
) -> None:
    """Audit the personal folder against registered managed projects."""
    estate_dir = estate_root_path(root)
    cfg_path = pathlib.Path(config).resolve() if config else None
    specs = load_managed_specs(cfg_path) if cfg_path else None
    audit = audit_estate(estate_dir, specs=specs)

    if json_output:
        payload = {
            "estate_root": str(audit.estate_root),
            "is_clean": audit.is_clean,
            "total_items": audit.total_items,
            "managed_repos": [
                {"name": item.name, "path": str(item.path), "remote": item.remote_url} for item in audit.managed_repos
            ],
            "linked_worktrees": [{"name": item.name, "path": str(item.path)} for item in audit.linked_worktrees],
            "unmanaged_items": [
                {"name": item.name, "path": str(item.path), "kind": item.kind.value, "detail": item.detail}
                for item in audit.unmanaged_items
            ],
            "missing_managed_repos": [
                {"name": spec.name, "repo": spec.repo, "description": spec.description}
                for spec in audit.missing_managed_repos
            ],
            "blocked_managed_repos": [
                {"name": item.name, "path": str(item.path), "kind": item.kind.value, "detail": item.detail}
                for item in audit.blocked_managed_repos
            ],
            "known_system_items": [{"name": item.name, "path": str(item.path)} for item in audit.known_system_items],
            "retained_archives": [
                {"name": item.name, "path": str(item.path), "remote": item.remote_url, "detail": item.detail}
                for item in audit.retained_archives
            ],
        }
        typer.echo(json.dumps(payload, indent=2))
        if strict and not audit.is_clean:
            raise typer.Exit(code=1)
        return

    title = f"Personal Virtual Monorepo Audit: {estate_dir}"
    console.print(Panel.fit(f"[bold cyan]{title}[/bold cyan]", border_style="cyan"))

    # Managed Repos
    console.print(f"\n[bold green]✓ Managed Repositories ({len(audit.managed_repos)})[/bold green]")
    for item in audit.managed_repos:
        remote_str = f" [dim]({item.remote_url})[/dim]" if item.remote_url else ""
        console.print(f"  • [bold]{item.name}[/bold]{remote_str}")

    # Retained Archives
    if audit.retained_archives:
        console.print(f"\n[bold magenta]📦 Retained Archives ({len(audit.retained_archives)})[/bold magenta]")
        for item in audit.retained_archives:
            remote_str = f" [dim]({item.remote_url})[/dim]" if item.remote_url else ""
            console.print(f"  • [magenta]{item.name}[/magenta]{remote_str} [dim]({item.detail})[/dim]")

    # Linked Worktrees
    if audit.linked_worktrees:
        console.print(f"\n[bold blue]⌥ Linked Git Worktrees ({len(audit.linked_worktrees)})[/bold blue]")
        for item in audit.linked_worktrees:
            console.print(f"  • [cyan]{item.name}[/cyan] [dim]({item.detail})[/dim]")

    # Known System Files
    if audit.known_system_items:
        console.print(f"\n[bold]⚙ System Orchestration Items ({len(audit.known_system_items)})[/bold]")
        for item in audit.known_system_items:
            console.print(f"  • [dim]{item.name}[/dim]")

    # Missing Managed Repos
    if audit.missing_managed_repos:
        console.print(f"\n[bold red]✗ Missing Declared Clones ({len(audit.missing_managed_repos)})[/bold red]")
        for spec in audit.missing_managed_repos:
            console.print(f"  • [red]{spec.name}[/red]: {spec.repo} [dim]({spec.description})[/dim]")

    # Declared org-folder locations occupied by something else
    if audit.blocked_managed_repos:
        console.print(f"\n[bold red]✗ Blocked Declared Locations ({len(audit.blocked_managed_repos)})[/bold red]")
        for item in audit.blocked_managed_repos:
            console.print(f"  • [red]{item.name}[/red] at {item.path} [dim]({item.kind.value}: {item.detail})[/dim]")

    # Unmanaged Items
    if audit.unmanaged_items:
        console.print(f"\n[bold yellow]! Unmanaged Items In Estate ({len(audit.unmanaged_items)})[/bold yellow]")
        for item in audit.unmanaged_items:
            kind_style = "red" if item.kind.value == "unmanaged_dir" else "yellow"
            console.print(f"  • [{kind_style}]{item.name}[/{kind_style}] [dim]({item.kind.value}: {item.detail})[/dim]")
        console.print(
            "\n[dim]Run 'workspace clean --dry-run' to inspect or register them in config/managed-projects.json[/dim]"
        )
    else:
        console.print("\n[bold green]SUCCESS: No unmanaged files or directories in estate root.[/bold green]")

    if strict and not audit.is_clean:
        raise typer.Exit(code=1)


@app.command(name="status")
def status_command(
    root: str | None = typer.Option(None, "--root", "-r", help="Path to personal estate root directory"),
    config: str | None = typer.Option(None, "--config", "-c", help="Path to managed-projects.json registry file"),
) -> None:
    """Show an inventory overview of all managed projects in the estate."""
    estate_dir = estate_root_path(root)
    cfg_path = pathlib.Path(config).resolve() if config else None
    specs = load_managed_specs(cfg_path)
    audit = audit_estate(estate_dir, specs=specs)

    table = Table(title=f"Estate Inventory — {estate_dir}", title_style="bold cyan")
    table.add_column("Repository", style="bold green", no_wrap=True)
    table.add_column("Status", no_wrap=True)
    table.add_column("Description")

    present_names = {item.name for item in audit.managed_repos}
    retained_names = {item.name for item in audit.retained_archives}
    blocked_names = {item.name for item in audit.blocked_managed_repos}
    for spec in specs:
        if not spec.enabled:
            status = (
                "[magenta]Retained (Archive)[/magenta]"
                if spec.name in retained_names
                else "[dim]Archived (Absent)[/dim]"
            )
        elif spec.name in present_names:
            status = "[green]Present[/green]"
        elif spec.name in blocked_names:
            status = "[red]Blocked[/red]"
        else:
            status = "[red]Missing[/red]"
        table.add_row(spec.name, status, spec.description or "[dim]--[/dim]")

    console.print(table)
    console.print(
        f"\n[dim]{len(present_names)}/{len([s for s in specs if s.enabled])} active managed repos present · "
        f"{len(audit.retained_archives)} retained archives · "
        f"{len(audit.unmanaged_items)} unmanaged items detected[/dim]\n"
    )


@app.command(name="clean")
def clean_command(
    root: str | None = typer.Option(None, "--root", "-r", help="Path to personal estate root directory"),
    config: str | None = typer.Option(None, "--config", "-c", help="Path to managed-projects.json registry file"),
    dry_run: bool = typer.Option(True, "--dry-run/--apply", help="Preview cleanup actions without deleting"),
    force: bool = typer.Option(False, "--force", help="Allow deletion of unmanaged git repos (only if clean)"),
) -> None:
    """Enforce the invariant: remove unmanaged files and directories from the personal folder."""
    estate_dir = estate_root_path(root)
    cfg_path = pathlib.Path(config).resolve() if config else None
    specs = load_managed_specs(cfg_path) if cfg_path else None
    audit = audit_estate(estate_dir, specs=specs)

    if not audit.unmanaged_items:
        console.print("[bold green]✓ Estate is clean. Nothing to remove.[/bold green]")
        return

    mode = "[bold cyan][DRY-RUN][/bold cyan] " if dry_run else "[bold red][APPLY][/bold red] "
    console.print(f"{mode}Found {len(audit.unmanaged_items)} unmanaged item(s) in {estate_dir}:\n")

    for item in audit.unmanaged_items:
        if item.kind.value == "unmanaged_repo" and not force:
            console.print(f"  [yellow]Skipped repo {item.name} (requires --force to remove git clones)[/yellow]")
            continue

        outcome = clean_estate_item(item, dry_run=dry_run)
        colour = "green" if outcome.ok else "red"
        console.print(f"  [{colour}]{outcome.message}[/{colour}]")


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
