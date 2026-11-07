"""CLI interface for managing AI agent skill symlinks across OpenCode, Claude, Codex, and Antigravity."""

from __future__ import annotations

import typer
from rich.console import Console
from rich.panel import Panel
from rich.table import Table

from rig.skills.engine import (
    activate_skills,
    audit_skill_symlinks,
    deactivate_skills,
    discover_all_skills,
    load_skill_activation_state,
    prune_broken_skill_symlinks,
    sync_skill_symlinks,
)
from rig.skills.models import (
    CLI_SYSTEM_DISPLAY_NAMES,
    DEFAULT_PRIMARY_SYSTEMS,
    CliSystem,
)

skills_cli = typer.Typer(
    name="skills",
    help="Manage global AI agent skill symlinks across OpenCode, Claude, Codex, and Antigravity.",
    no_args_is_help=True,
    add_completion=False,
)
console = Console()


def _resolve_systems(system_args: list[str] | None) -> list[CliSystem]:
    """Parse string system names into typed CliSystem enums."""
    if not system_args:
        return list(DEFAULT_PRIMARY_SYSTEMS)

    resolved: list[CliSystem] = []
    for arg in system_args:
        clean = arg.strip().lower()
        if clean == "all":
            return list(CliSystem)
        try:
            resolved.append(CliSystem(clean))
        except ValueError:
            valid_names = ", ".join(s.value for s in CliSystem)
            console.print(f"[red]Error:[/red] Unknown system '{arg}'. Supported systems: {valid_names}")
            raise typer.Exit(code=1) from None
    return resolved


@skills_cli.command(name="status")
def status_command(
    system: list[str] = typer.Option(
        None,
        "--system",
        "-s",
        help="Filter audit to specific CLI systems (opencode, claude, codex, antigravity, all)",
    ),
) -> None:
    """Audit the current symlink state across AI agent CLI systems."""
    target_systems = _resolve_systems(system)
    report = audit_skill_symlinks(systems=target_systems)

    num_skills = len(report.discovered_skills)
    allowlist_flag = " [bold green][allowlist active][/bold green]" if report.allowlist_active else ""
    title = (
        f"[bold cyan]AI Agent Skills Symlink Verification[/bold cyan] "
        f"[dim]({num_skills} active skills{allowlist_flag})[/dim]"
    )
    console.print(Panel.fit(title, border_style="cyan"))

    activations = load_skill_activation_state()
    if activations:
        console.print(
            f"[dim]Temporary overlay: {len(activations)} skill(s) active; "
            "run [cyan]workspace skills deactivate --all[/cyan] to restore the curated catalog.[/dim]\n"
        )

    table = Table(show_header=True, header_style="bold")
    table.add_column("CLI System", style="cyan", no_wrap=True)
    table.add_column("Directory Path", style="dim")
    table.add_column("Status", justify="center")
    table.add_column("Active", justify="right")
    table.add_column("Issues", style="yellow")

    for sys in target_systems:
        status = report.systems.get(sys)
        if not status:
            continue

        if not status.dir_exists:
            health_badge = "[red]Missing Dir[/red]"
            issues = "Target directory does not exist"
        elif status.is_healthy:
            health_badge = "[green]✓ Healthy[/green]"
            issues = "[dim]None[/dim]"
        else:
            health_badge = "[yellow]! Drifted[/yellow]"
            parts: list[str] = []
            if status.missing_skills:
                parts.append(f"{len(status.missing_skills)} missing")
            if status.broken_links:
                parts.append(f"{len(status.broken_links)} broken")
            if status.stale_links:
                parts.append(f"{len(status.stale_links)} stale")
            if status.drifted_links:
                parts.append(f"{len(status.drifted_links)} drifted")
            if status.collisions:
                parts.append(f"{len(status.collisions)} non-symlink collision")
            issues = ", ".join(parts)

        table.add_row(
            status.display_name,
            str(status.target_dir),
            health_badge,
            f"{len(status.valid_links)}/{len(report.discovered_skills)}",
            issues,
        )

    console.print(table)
    console.print()

    if report.is_clean:
        console.print("[bold green]✓ All AI agent systems are completely in sync![/bold green]")
    else:
        console.print("[yellow]Run 'workspace skills sync' to provision or fix missing symlinks.[/yellow]")


@skills_cli.command(name="sync")
def sync_command(
    system: list[str] = typer.Option(
        None,
        "--system",
        "-s",
        help="Specific CLI systems to sync (opencode, claude, codex, antigravity, all)",
    ),
    prune: bool | None = typer.Option(
        None,
        "--prune/--no-prune",
        help="Remove links outside the selected catalog (defaults off for --only, on otherwise)",
    ),
    dry_run: bool = typer.Option(
        False,
        "--dry-run",
        help="Preview links without modifying disk",
    ),
    include_hidden: bool = typer.Option(
        False,
        "--all",
        help="Temporarily include every vendored skill; does not change the permanent allowlist",
    ),
    only: list[str] = typer.Option(
        None,
        "--only",
        help="Temporarily provision named skills; repeat the option for multiple skills",
    ),
) -> None:
    """Synchronize skill symlinks across supported agent CLIs."""
    if include_hidden and only:
        console.print("[red]Use either --all or --only, not both.[/red]")
        raise typer.Exit(code=2)

    target_systems = _resolve_systems(system)
    if include_hidden or only:
        try:
            results = activate_skills(
                only or [],
                systems=target_systems,
                all_skills=include_hidden,
                dry_run=dry_run,
            )
        except ValueError as exc:
            console.print(f"[red]{exc}[/red]")
            raise typer.Exit(code=2) from exc
        selection = "all discovered" if include_hidden else ", ".join(only)
        console.print(f"[bold cyan]Activating {selection} as a reversible skill overlay:[/bold cyan]")
        for target, count in results.items():
            console.print(f"  [green]{CLI_SYSTEM_DISPLAY_NAMES.get(target, target.value)}[/green]: {count} link(s)")
        if not dry_run:
            console.print("[dim]Run 'workspace skills deactivate --all' to restore the curated catalog.[/dim]")
        return

    selected_allowlist = only or None
    use_estate_allowlist = not include_hidden and not only
    skills = discover_all_skills(allowlist=selected_allowlist, use_estate_allowlist=use_estate_allowlist)
    effective_prune = (not bool(only)) if prune is None else prune

    if dry_run:
        console.print("[bold yellow]Running in dry-run mode (no changes will be applied)[/bold yellow]\n")

    console.print(
        f"[bold cyan]Reconciling {len(skills)} selected skills across {len(target_systems)} CLI system(s):[/bold cyan]"
    )
    for s in target_systems:
        name = CLI_SYSTEM_DISPLAY_NAMES.get(s, s.value)
        console.print(f"  • [green]{name}[/green]")
    console.print()

    results = sync_skill_symlinks(
        systems=target_systems,
        prune=effective_prune,
        dry_run=dry_run,
        allowlist=selected_allowlist,
        use_estate_allowlist=use_estate_allowlist,
    )

    for s, count in results.items():
        name = CLI_SYSTEM_DISPLAY_NAMES.get(s, s.value)
        action_verb = "Would link" if dry_run else "Successfully linked"
        console.print(f"[bold green]✓[/bold green] {name}: {action_verb} {count} skill(s)")

    console.print()
    if not dry_run:
        console.print("[bold green]✓ Symlink synchronization complete across all requested systems![/bold green]")


@skills_cli.command(name="activate")
def activate_command(
    system: list[str] = typer.Option(None, "--system", "-s", help="Target CLI systems."),
    only: list[str] = typer.Option(None, "--only", help="Skill names to expose; repeat for multiple skills."),
    all_skills: bool = typer.Option(False, "--all", help="Expose every discovered skill."),
    dry_run: bool = typer.Option(False, "--dry-run", help="Preview links without changing disk."),
) -> None:
    """Activate a temporary skill overlay that can later be deactivated."""
    if all_skills == bool(only):
        console.print("[red]Choose exactly one of --only or --all.[/red]")
        raise typer.Exit(code=2)
    try:
        results = activate_skills(only or [], systems=_resolve_systems(system), all_skills=all_skills, dry_run=dry_run)
    except ValueError as exc:
        console.print(f"[red]{exc}[/red]")
        raise typer.Exit(code=2) from exc
    for target, count in results.items():
        console.print(f"{CLI_SYSTEM_DISPLAY_NAMES.get(target, target.value)}: {count} link(s)")


@skills_cli.command(name="deactivate")
def deactivate_command(
    system: list[str] = typer.Option(None, "--system", "-s", help="Target CLI systems."),
    only: list[str] = typer.Option(None, "--only", help="Overlay names to remove; repeat for multiple skills."),
    all_skills: bool = typer.Option(False, "--all", help="Remove every recorded overlay."),
    dry_run: bool = typer.Option(False, "--dry-run", help="Preview removals without changing disk."),
) -> None:
    """Remove temporary skill links and restore the curated catalog."""
    if all_skills == bool(only):
        console.print("[red]Choose exactly one of --only or --all.[/red]")
        raise typer.Exit(code=2)
    results = deactivate_skills(only or [], systems=_resolve_systems(system), all_skills=all_skills, dry_run=dry_run)
    for target, count in results.items():
        console.print(f"{CLI_SYSTEM_DISPLAY_NAMES.get(target, target.value)}: removed {count} link(s)")


@skills_cli.command(name="prune")
def prune_command(
    system: list[str] = typer.Option(
        None,
        "--system",
        "-s",
        help="Specific CLI systems to prune (opencode, claude, codex, antigravity, all)",
    ),
    dry_run: bool = typer.Option(
        False,
        "--dry-run",
        help="Preview links without removing them",
    ),
) -> None:
    """Prune broken or dangling symlinks across AI agent CLI systems."""
    target_systems = _resolve_systems(system)
    pruned = prune_broken_skill_symlinks(systems=target_systems, dry_run=dry_run)

    total = sum(pruned.values())
    if total == 0:
        console.print("[bold green]✓ No broken symlinks found across target systems.[/bold green]")
        return

    for s, count in pruned.items():
        name = CLI_SYSTEM_DISPLAY_NAMES.get(s, s.value)
        action_verb = "Would prune" if dry_run else "Pruned"
        console.print(f"  • {name}: {action_verb} {count} broken symlink(s)")

    console.print(f"\n[bold green]✓ Done ({total} broken symlink(s) handled).[/bold green]")


@skills_cli.command(name="update")
def update_command(
    system: list[str] = typer.Option(
        None,
        "--system",
        "-s",
        help="Specific CLI systems to sync (opencode, claude, codex, antigravity, all)",
    ),
    vendor: str | None = typer.Option(
        None,
        "--vendor",
        "-v",
        help="Filter update to a specific upstream vendor",
    ),
    sync: bool = typer.Option(
        True,
        "--sync/--no-sync",
        help="Automatically sync symlinks across CLI systems after updating vendored skills",
    ),
) -> None:
    """Fetch/update vendored upstream skills (Matt Pocock skills, pstack, etc.) and update symlinks."""
    from rig.skills.vendor import load_upstream_vendors, update_all_vendors

    console.print(Panel.fit("[bold cyan]Fetching & Updating Upstream Vendored Skills[/bold cyan]", border_style="cyan"))

    vendors_to_update = None
    if vendor:
        all_vendors = load_upstream_vendors()
        matched = [v for v in all_vendors if v.name == vendor]
        if not matched:
            console.print(f"[red]Error:[/red] Vendor '{vendor}' not found in configuration.")
            raise typer.Exit(code=1)
        vendors_to_update = matched

    results = update_all_vendors(vendors=vendors_to_update)
    total_skills = sum(len(skills) for skills in results.values())
    num_repos = len(results)
    console.print(
        f"\n[bold green]✓ Successfully vendored {total_skills} skills across {num_repos} repos.[/bold green]\n"
    )

    if sync:
        target_systems = _resolve_systems(system)
        sync_results = sync_skill_symlinks(systems=target_systems, prune=True)
        for s, count in sync_results.items():
            name = CLI_SYSTEM_DISPLAY_NAMES.get(s, s.value)
            console.print(f"[bold green]✓[/bold green] {name}: Synced {count} skill(s)")
        console.print("\n[bold green]✓ Skills vendoring and global symlink sync complete![/bold green]")


@skills_cli.command(name="list")
def list_command(
    query: str | None = typer.Option(
        None,
        "--query",
        "-q",
        help="Search filter matching skill names or descriptions",
    ),
) -> None:
    """List all skills discovered in repository, personal overrides, and plugins."""
    skills = discover_all_skills()

    if query:
        q = query.lower()
        skills = [s for s in skills if q in s.name.lower() or q in s.description.lower()]

    table = Table(title=f"Skills Catalog ({len(skills)} total)", show_header=True)
    table.add_column("Skill Name", style="cyan")
    table.add_column("Source", style="magenta")
    table.add_column("Description", style="dim")

    for s in skills:
        short_desc = s.description[:60] + "..." if len(s.description) > 60 else s.description
        table.add_row(s.name, s.source_name, short_desc)

    console.print(table)
