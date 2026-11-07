"""Project discovery, linking and cloning commands."""

from __future__ import annotations

from pathlib import Path

import typer

from desktop_manager.cli_common import console

projects_app = typer.Typer(name="projects", no_args_is_help=True, help="Project discovery, linking, and management.")


def workspace_projects() -> None:
    """Show configured virtual-monorepo projects and their local status."""
    from rich.table import Table

    from desktop_manager.projects import managed_project_statuses, managed_projects_config_path

    statuses = managed_project_statuses()
    console.print(f"[dim]Config:[/dim] {managed_projects_config_path()}")
    console.print()

    tbl = Table(show_header=True, padding=(0, 2, 0, 2), box=None)
    tbl.add_column("Project", style="bold green", no_wrap=True)
    tbl.add_column("Cloned", no_wrap=True)
    tbl.add_column("Linked", no_wrap=True)
    tbl.add_column("Repo", style="dim")
    for status in statuses:
        tbl.add_row(
            status.project.name,
            "yes" if status.is_cloned else "no",
            "yes" if status.is_linked else "no",
            status.project.repo,
        )
    console.print(tbl)


def workspace_sync_projects(
    name: list[str] | None = typer.Option(None, "--name", help="Only sync the named managed project(s)."),
    no_clone: bool = typer.Option(False, "--no-clone", help="Do not clone missing local repos."),
    no_link: bool = typer.Option(False, "--no-link", help="Do not create missing projects/ links."),
) -> None:
    """Sync configured virtual-monorepo projects from the committed registry."""
    from desktop_manager.projects import managed_projects_config_path, sync_managed_projects

    console.print(f"[dim]Config:[/dim] {managed_projects_config_path()}")
    actions = sync_managed_projects(names=name, clone_missing=not no_clone, link_missing=not no_link)
    if not actions:
        console.print("[green]Managed projects already match the config.[/green]")
        return
    for action in actions:
        console.print(action)


def workspace_clone_project(
    name: str = typer.Option(..., "--name", help="Name of the managed project to clone from config."),
    no_link: bool = typer.Option(False, "--no-link", help="Clone without creating the projects/ link."),
) -> None:
    """Clone one configured managed project from the virtual-monorepo registry."""
    from desktop_manager.projects import sync_managed_projects

    actions = sync_managed_projects(names=[name], clone_missing=True, link_missing=not no_link)
    for action in actions:
        console.print(action)


def workspace_vscode() -> None:
    """Regenerate the multi-root VS Code workspace from the linked projects."""
    from desktop_manager.projects import collect_linked_projects, generate_vscode_workspace

    target = generate_vscode_workspace()
    projects = collect_linked_projects()
    console.print(f"[green]Wrote[/green] {target}")
    console.print(f"[dim]Folders:[/dim] root + {len(projects)} linked project(s)")
    for proj in projects:
        console.print(f"  [green]+[/green] {proj.name}")


@projects_app.command(name="list")
def projects_list() -> None:
    """List all projects currently linked in the projects/ directory."""
    from rich.table import Table

    from desktop_manager.projects import collect_linked_projects, get_projects_dir

    projects = collect_linked_projects()
    if not projects:
        console.print(f"[dim]No projects linked yet.[/dim]  ({get_projects_dir()})")
        console.print("[dim]Run[/dim] desktop-manager projects discover [dim]to find repos to link.[/dim]")
        return

    tbl = Table(show_header=True, padding=(0, 2, 0, 2), box=None)
    tbl.add_column("Repo", style="bold green", no_wrap=True)
    tbl.add_column("Path", style="dim")
    tbl.add_column("Summary", style="white")
    for proj in projects:
        tbl.add_row(proj.name, str(proj.path), proj.summary or "--")
    console.print(tbl)


@projects_app.command(name="discover")
def projects_discover() -> None:
    """Scan the peer directory for git repos not yet linked."""
    from desktop_manager.projects import discover_unlinked_projects, get_peer_dir

    peer = get_peer_dir()
    console.print(f"[dim]Scanning:[/dim] {peer}")
    console.print()

    unlinked = discover_unlinked_projects()
    if not unlinked:
        console.print("[green]All peer repos are already linked (or none found).[/green]")
        return

    console.print(f"Found [bold]{len(unlinked)}[/bold] unlinked repo(s):\n")
    for proj in unlinked:
        summary_part = f"  [dim]{proj.summary}[/dim]" if proj.summary else ""
        console.print(f"  [yellow]{proj.name}[/yellow]{summary_part}")
        console.print(f"    [dim]{proj.path}[/dim]")

    console.print()
    console.print("[dim]Run[/dim] desktop-manager projects link --name <name> [dim]to link a repo.[/dim]")
    console.print("[dim]Run[/dim] desktop-manager projects link-all [dim]to link everything.[/dim]")


@projects_app.command(name="link")
def projects_link(
    name: str = typer.Option(..., "--name", help="Name of the peer repo directory to link."),
    path: Path | None = typer.Option(
        None,
        "--path",
        help="Explicit path to a git repo. Overrides peer directory lookup.",
    ),
) -> None:
    """Link a single peer repo into the projects/ directory."""
    from desktop_manager.projects import get_peer_dir, link_project

    target = path.resolve() if path is not None else get_peer_dir() / name

    if not target.exists():
        console.print(f"[red]Not found:[/red] {target}")
        raise typer.Exit(code=1)

    try:
        link_path = link_project(target, name=name)
        console.print(f"[green]Linked[/green] {name} -> {target}")
        console.print(f"  [dim]{link_path}[/dim]")
    except (FileExistsError, ValueError) as exc:
        console.print(f"[red]Error:[/red] {exc}")
        raise typer.Exit(code=1) from None


@projects_app.command(name="link-all")
def projects_link_all() -> None:
    """Discover and link all unlinked peer repos at once."""
    from desktop_manager.projects import discover_unlinked_projects, link_project

    unlinked = discover_unlinked_projects()
    if not unlinked:
        console.print("[green]All peer repos are already linked (or none found).[/green]")
        return

    linked_count = 0
    skipped_count = 0
    for proj in unlinked:
        try:
            link_project(proj.path, name=proj.name)
            console.print(f"  [green]Linked[/green] {proj.name} -> {proj.path}")
            linked_count += 1
        except (FileExistsError, ValueError) as exc:
            console.print(f"  [red]Skipped[/red] {proj.name}: {exc}")
            skipped_count += 1

    console.print(f"\n[bold]{linked_count}[/bold] project(s) linked.")
    if skipped_count:
        console.print(f"[red]{skipped_count} project(s) skipped.[/red]")
        raise typer.Exit(code=1)


@projects_app.command(name="clone")
def projects_clone(
    repo: str = typer.Option(..., "--repo", help="Git URL for the repository to clone."),
    name: str | None = typer.Option(None, "--name", help="Directory name to use under the peer repo directory."),
    branch: str | None = typer.Option(None, "--branch", help="Optional branch to clone."),
    no_link: bool = typer.Option(False, "--no-link", help="Clone the repo without linking it into projects/."),
) -> None:
    """Clone a repo into the peer directory and link it into projects/."""
    from subprocess import CalledProcessError

    from desktop_manager.projects import clone_project

    try:
        cloned = clone_project(repo, name=name, branch=branch, link=not no_link)
    except (FileExistsError, CalledProcessError, ValueError) as exc:
        console.print(f"[red]Error:[/red] {exc}")
        raise typer.Exit(code=1) from None

    console.print(f"[green]Cloned[/green] {repo} -> {cloned.path}")
    if cloned.link_path is not None:
        console.print(f"[green]Linked[/green] {cloned.link_path.name}")
        console.print(f"  [dim]{cloned.link_path}[/dim]")


@projects_app.command(name="unlink")
def projects_unlink(
    name: str = typer.Option(..., "--name", help="Name of the linked project to remove."),
) -> None:
    """Remove a project symlink from the projects/ directory."""
    from desktop_manager.projects import unlink_project

    try:
        link_path = unlink_project(name)
        console.print(f"[green]Unlinked[/green] {name}")
        console.print(f"  [dim]Removed {link_path}[/dim]")
    except (FileNotFoundError, ValueError) as exc:
        console.print(f"[red]Error:[/red] {exc}")
        raise typer.Exit(code=1) from None
