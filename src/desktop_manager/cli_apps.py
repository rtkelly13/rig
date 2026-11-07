"""Commands that act on one plan entry: test, restart, launch a file with it, detect its app."""

from __future__ import annotations

from pathlib import Path

import typer

from desktop_manager.cli_common import (
    PlannedProcess,
    config_option,
    console,
    console_safe_text,
    virtual_desktop_event_callback,
)
from rig.platforms import Platform, requires


@requires(Platform.WINDOWS)
def test_item(
    name: str = typer.Option(..., "--name", help="Name of the launch entry to test."),
    desktop: str | None = typer.Option(None, "--desktop", help="Name of the desktop containing the entry."),
    config: Path = typer.Option(
        None,
        "--config",
        help="Path to the workspace JSON plan.",
    ),
    dry_run: bool = typer.Option(False, "--dry-run", help="Show what would happen without launching."),
) -> None:
    """Launch and place a single entry for testing."""
    from desktop_manager.windows_workspace import (
        clear_virtual_desktop_event_callback,
        create_restore_run_logger,
        launch_single_entry,
        load_plan,
        set_virtual_desktop_event_callback,
    )

    config_path = config or config_option()
    if not config_path.exists():
        console.print(f"[red]Config not found:[/red] {config_path}")
        raise typer.Exit(code=1)

    plan = load_plan(config_path)

    resolved_desktop = desktop
    if resolved_desktop is None:
        from desktop_manager.win32.desktops import get_current_desktop_number

        current = get_current_desktop_number()
        if current is None or current < 1 or current > len(plan.desktops):
            console.print("[red]Could not determine the current desktop.[/red]")
            raise typer.Exit(code=1)
        resolved_desktop = plan.desktops[current - 1].name
        console.print(f"Using current desktop: [bold]{resolved_desktop}[/bold]")

    logger = None if dry_run else create_restore_run_logger(config_path)
    set_virtual_desktop_event_callback(virtual_desktop_event_callback(logger))
    try:
        try:
            actions = launch_single_entry(plan, resolved_desktop, name, dry_run=dry_run, logger=logger)
        except ValueError as exc:
            console.print(f"[red]Error:[/red] {exc}")
            raise typer.Exit(code=1) from None
        for action in actions:
            console.print(action)
    finally:
        clear_virtual_desktop_event_callback()


@requires(Platform.WINDOWS)
def restart_app(
    name: str = typer.Option(..., "--name", help="Name of the launch entry/app to restart (e.g. Fork)."),
    desktop: str | None = typer.Option(
        None,
        "--desktop",
        help="Only restart matching entries on this desktop. Defaults to every desktop.",
    ),
    config: Path = typer.Option(
        None,
        "--config",
        help="Path to the workspace JSON plan.",
    ),
    graceful: bool = typer.Option(
        False,
        "--graceful",
        help="Request a polite close first, then force-kill any survivors (default: force-kill immediately).",
    ),
    dry_run: bool = typer.Option(False, "--dry-run", help="Show what would happen without closing or launching."),
) -> None:
    """Force-close every window of an app type and re-open it in its planned placement."""
    from desktop_manager.windows_workspace import (
        clear_virtual_desktop_event_callback,
        create_restore_run_logger,
        load_plan,
        restart_app_entries,
        set_virtual_desktop_event_callback,
    )

    config_path = config or config_option()
    if not config_path.exists():
        console.print(f"[red]Config not found:[/red] {config_path}")
        raise typer.Exit(code=1)

    plan = load_plan(config_path)
    logger = None if dry_run else create_restore_run_logger(config_path)
    set_virtual_desktop_event_callback(virtual_desktop_event_callback(logger))
    try:
        actions = restart_app_entries(
            plan,
            name,
            target_desktop=desktop,
            graceful=graceful,
            dry_run=dry_run,
            logger=logger,
        )
    except ValueError as exc:
        console.print(f"[red]Error:[/red] {exc}")
        raise typer.Exit(code=1) from None
    finally:
        clear_virtual_desktop_event_callback()

    for action in actions:
        console.print(console_safe_text(action))
    if logger is not None:
        console.print(f"\n[dim]Log:[/dim] {logger.run_log_path}")


@requires(Platform.WINDOWS)
def update_orca(
    config: Path = typer.Option(
        None,
        "--config",
        help="Path to the workspace JSON plan.",
    ),
    close_timeout: float = typer.Option(
        60.0,
        "--close-timeout",
        help="Seconds to let Orca quit gracefully (it saves sessions on quit) before force-killing it.",
    ),
    dry_run: bool = typer.Option(False, "--dry-run", help="Show what would happen without closing or installing."),
) -> None:
    """Apply a staged Orca update and relaunch every Orca profile in its planned placement.

    Orca's own "restart to update" relaunches only the profile that ran it. This closes
    every Orca window, installs the staged build silently, and relaunches each enabled
    Orca entry in the plan. Exits 0 with nothing to do when Orca is already up to date.
    """
    from desktop_manager.orca_update import update_orca as run_update
    from desktop_manager.windows_workspace import (
        clear_virtual_desktop_event_callback,
        create_restore_run_logger,
        load_plan,
        set_virtual_desktop_event_callback,
    )

    config_path = config or config_option()
    if not config_path.exists():
        console.print(f"[red]Config not found:[/red] {config_path}")
        raise typer.Exit(code=1)

    plan = load_plan(config_path)
    logger = None if dry_run else create_restore_run_logger(config_path)
    set_virtual_desktop_event_callback(virtual_desktop_event_callback(logger))
    try:
        result = run_update(plan, dry_run=dry_run, logger=logger, close_timeout_seconds=close_timeout)
    finally:
        clear_virtual_desktop_event_callback()

    for action in result.actions:
        console.print(console_safe_text(action))
    if logger is not None:
        console.print(f"\n[dim]Log:[/dim] {logger.run_log_path}")
    if not result.ok:
        raise typer.Exit(code=1)


@requires(Platform.WINDOWS)
def launch_file(
    entry_file: Path = typer.Option(..., "--entry-file", help="Path to a JSON file containing one launch entry."),
    config: Path | None = typer.Option(
        None, "--config", help="Optional workspace plan used to provide aliases and substitutions."
    ),
    dry_run: bool = typer.Option(False, "--dry-run", help="Show what would happen without launching."),
) -> None:
    """Launch one entry from a standalone JSON file using the normal launch path."""
    from desktop_manager.windows_workspace import (
        LaunchEntry,
        WorkspacePlan,
        describe_launch,
        launch_entry,
        load_plan_context,
    )
    from rig.typed_json import load_json

    raw = load_json(entry_file.read_text(encoding="utf-8"))
    entry = LaunchEntry.from_dict(raw)
    plan = load_plan_context(config) if config is not None else WorkspacePlan()
    entry.placement = None

    console.print(f"  - {describe_launch(entry, plan.substitutions())}")
    if dry_run:
        return
    launch_entry(entry, plan.substitutions())
    console.print(f"[green]Launched:[/green] {entry.name}")


@requires(Platform.WINDOWS)
def detect_app(
    name: str | None = typer.Option(None, "--name", help="Name of the launch entry to check."),
    desktop: str | None = typer.Option(None, "--desktop", help="Only check entries on this desktop."),
    config: Path = typer.Option(
        None,
        "--config",
        help="Path to the workspace JSON plan.",
    ),
) -> None:
    """Detect whether applications from the workspace plan are currently running."""
    from desktop_manager.win32.inventory import collect_windows_matching_process
    from desktop_manager.windows_workspace import load_plan

    config_path = config or config_option()
    if not config_path.exists():
        console.print(f"[red]Config not found:[/red] {config_path}")
        raise typer.Exit(code=1)

    plan = load_plan(config_path)

    entries: list[PlannedProcess] = []
    for dp in plan.desktops:
        if desktop is not None and dp.name != desktop:
            continue
        for entry in dp.launch:
            if name is not None and entry.name != name:
                continue
            entries.append(PlannedProcess(dp.name, entry.name, entry.window_process_name))

    if not entries:
        console.print("[yellow]No matching entries found in the plan.[/yellow]")
        raise typer.Exit(code=1)

    found_any = False
    for planned in entries:
        desktop_name, entry_name, process_name = planned.desktop_name, planned.entry_name, planned.process_name
        if not process_name:
            console.print(f"  [dim]{desktop_name}[/dim] / {entry_name}  [dim]— no process name configured[/dim]")
            continue
        handles = collect_windows_matching_process(process_name.casefold(), current_desktop_only=False)
        if handles:
            found_any = True
            console.print(
                f"  [green]*[/green] [dim]{desktop_name}[/dim] / {entry_name}"
                f"  [green]running[/green] ({len(handles)} window{'s' if len(handles) != 1 else ''})"
            )
        else:
            console.print(f"  [red]-[/red] [dim]{desktop_name}[/dim] / {entry_name}  [red]not running[/red]")

    if not found_any:
        raise typer.Exit(code=1)
