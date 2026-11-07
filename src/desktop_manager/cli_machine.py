"""Machine wiring commands: export the plan, install the startup task and display guard, name desktops."""

from __future__ import annotations

import getpass
import os
import subprocess
import time
from pathlib import Path

import typer

from desktop_manager.cli_common import config_option, console, console_safe_text, virtual_desktop_event_callback
from rig.platforms import Platform, requires


@requires(Platform.WINDOWS)
def wire_machine(
    workspace: Path = typer.Option(
        ...,
        "--workspace",
        help="Root directory for the primary workspace (resolves {workspace} placeholders).",
    ),
    personal_workspace: Path = typer.Option(
        None,
        "--personal-workspace",
        help="Root directory for the personal workspace (resolves {personal-workspace} placeholders).",
    ),
    config: Path = typer.Option(
        None,
        "--config",
        help="Output path for the saved plan. Defaults to the platform default.",
    ),
) -> None:
    """Generate and save the machine workspace plan from the bespoke template."""
    from desktop_manager.windows_workspace import (
        build_bespoke_machine_plan,
        save_plan,
    )

    config_path = config or config_option()
    plan = build_bespoke_machine_plan(workspace, personal_workspace=personal_workspace)
    save_plan(plan, config_path)

    console.print(f"[green]Saved workspace plan:[/green] {config_path}")
    console.print(f"[dim]Workspace:[/dim] {workspace}")
    if personal_workspace:
        console.print(f"[dim]Personal workspace:[/dim] {personal_workspace}")


@requires(Platform.WINDOWS)
def export_plan(
    workspace: Path = typer.Option(
        None,
        "--workspace",
        help="Workspace root used to convert desktop 1 terminals into launch commands.",
    ),
    desktop_count: int | None = typer.Option(
        None,
        "--desktop-count",
        help="Override the detected virtual desktop count when exporting.",
    ),
    config: Path = typer.Option(
        None,
        "--config",
        help="Output path for the saved plan. Defaults to the platform default.",
    ),
    bundle: bool = typer.Option(
        False,
        "--bundle",
        help="Export a bundle root plus one file per desktop instead of a single JSON file.",
    ),
    desktop_dir: str = typer.Option(
        "desktops",
        "--desktop-dir",
        help="Relative directory used for per-desktop files when --bundle is enabled.",
    ),
) -> None:
    """Capture the current desktop layout and export a draft restore plan."""
    from desktop_manager.windows_workspace import (
        capture_workspace_plan,
        clear_virtual_desktop_event_callback,
        save_plan,
        save_plan_bundle,
        set_virtual_desktop_event_callback,
    )

    config_path = config or config_option()
    set_virtual_desktop_event_callback(virtual_desktop_event_callback(None))
    try:
        plan = capture_workspace_plan(desktop_count=desktop_count, workspace=workspace)
        if bundle:
            save_plan_bundle(plan, config_path, desktop_dir_name=desktop_dir)
        else:
            save_plan(plan, config_path)
    finally:
        clear_virtual_desktop_event_callback()

    console.print(f"[green]Exported workspace plan:[/green] {config_path}")
    console.print(f"[dim]Mode:[/dim] {'bundle' if bundle else 'single file'}")
    if workspace:
        console.print(f"[dim]Workspace:[/dim] {workspace}")
    if desktop_count is not None:
        console.print(f"[dim]Desktop count:[/dim] {desktop_count}")


@requires(Platform.WINDOWS)
def guard(
    config: Path = typer.Option(
        None,
        "--config",
        help="Workspace plan path. Only used to find the logs directory shared with restore.",
    ),
    poll_seconds: float = typer.Option(1.0, "--poll-seconds", min=0.1, help="Seconds between topology checks."),
    settle_seconds: float = typer.Option(
        6.0, "--settle-seconds", min=0.0, help="How long a topology must hold before windows are put back."
    ),
    snapshot_seconds: float = typer.Option(
        5.0, "--snapshot-seconds", min=1.0, help="Seconds between placement snapshots while displays are stable."
    ),
    dry_run: bool = typer.Option(False, "--dry-run", help="Log what would be moved without moving anything."),
) -> None:
    """Watch for monitors dropping out or the machine waking, and put windows back.

    Runs until stopped. It logs to display-guard.log in the workspace logs directory,
    because the scheduled task runs it under pythonw.exe with no console. Install it
    with ``jobs install``.
    """
    import logging
    from logging.handlers import RotatingFileHandler

    from desktop_manager import runtime
    from desktop_manager.display_guard import (
        GUARD_LOCK_FILE_NAME,
        GUARD_LOG_FILE_NAME,
        RESTORE_LOCK_FILE_NAME,
        DisplayGuard,
        DisplayGuardSettings,
        lock_dir,
        run_guard,
        try_hold_lock,
    )
    from desktop_manager.display_guard_windows import (
        DisplayEventListener,
        Win32DisplayGuardPorts,
        make_process_per_monitor_dpi_aware,
    )
    from desktop_manager.plan import workspace_logs_dir

    config_path = config or config_option()
    # From a runtime copy, log beside Workspace Restore's logs, which outlive the copy.
    log_dir = runtime.workspace_logs_dir() if runtime.running_copy(__file__) else workspace_logs_dir(config_path)
    log_dir.mkdir(parents=True, exist_ok=True)
    locks = lock_dir()
    locks.mkdir(parents=True, exist_ok=True)
    guard_logger = logging.getLogger("desktop_manager.display_guard")
    guard_logger.setLevel(logging.INFO)
    guard_logger.propagate = False
    handler = RotatingFileHandler(log_dir / GUARD_LOG_FILE_NAME, maxBytes=1_000_000, backupCount=3, encoding="utf-8")
    handler.setFormatter(logging.Formatter("%(asctime)s %(message)s"))
    guard_logger.addHandler(handler)

    def log(message: str) -> None:
        guard_logger.info(message)
        console.print(console_safe_text(message))

    with try_hold_lock(locks / GUARD_LOCK_FILE_NAME) as acquired:
        if not acquired:
            log("Another display guard is already running; exiting.")
            return
        make_process_per_monitor_dpi_aware()
        events = DisplayEventListener(log)
        events.start()
        ports = Win32DisplayGuardPorts(
            restore_lock_path=locks / RESTORE_LOCK_FILE_NAME, log=log, clock=time.time, events=events
        )
        settings = DisplayGuardSettings(
            settle_seconds=settle_seconds,
            snapshot_interval_seconds=snapshot_seconds,
            dry_run=dry_run,
        )
        log(f"Display guard started (pid {os.getpid()}{', dry run' if dry_run else ''}).")
        try:
            run_guard(DisplayGuard(ports=ports, settings=settings), poll_seconds=poll_seconds, sleep=time.sleep)
        except KeyboardInterrupt:
            log("Display guard stopped.")
        except Exception:
            guard_logger.exception("Display guard crashed")
            raise


@requires(Platform.WINDOWS)
def name_desktops(
    config: Path = typer.Option(
        None,
        "--config",
        help="Path to the workspace JSON plan.",
    ),
    dry_run: bool = typer.Option(False, "--dry-run", help="Show the names that would be applied without setting them."),
) -> None:
    """Ensure the declared virtual desktops exist and have the right names (IaC).

    Windows periodically drops user-assigned desktop names; restore re-applies
    them on every run, but this reconciles virtual desktops on demand to match
    the declared plan specification (Infrastructure as Code).
    """
    from desktop_manager.win32.reconcile import reconcile_virtual_desktops
    from desktop_manager.windows_workspace import load_plan

    config_path = config or config_option()
    if not config_path.exists():
        console.print(f"[red]Config not found:[/red] {config_path}")
        raise typer.Exit(code=1)

    plan = load_plan(config_path)
    target_names = [desktop.name for desktop in plan.desktops]

    console.print("[bold]Virtual Desktop Reconciliation[/bold] (IAC)")
    console.print(f"[dim]Config:[/dim] {config_path} ({len(target_names)} planned desktop(s))")

    result = reconcile_virtual_desktops(
        target_names,
        retired_names=plan.retired_desktop_names,
        switch_delay_seconds=plan.switch_delay_ms / 1000,
        dry_run=dry_run,
    )

    if dry_run:
        p = result.plan
        if not p.has_drift:
            console.print("[green]No drift detected:[/green] all virtual desktops match declared state.")
        for name in p.to_retire:
            console.print(f"  [yellow]- remove[/yellow] switched-off desktop: {name}")
        for create_action in p.to_create:
            console.print(f'  [green]+ create[/green] Desktop {create_action.index} ("{create_action.name}")')
        for rename_action in p.to_rename:
            curr_str = f'"{rename_action.current_name}"' if rename_action.current_name else "(unnamed)"
            console.print(
                f'  [cyan]~ rename[/cyan] Desktop {rename_action.index} ({curr_str} -> "{rename_action.target_name}")'
            )
        for item in p.unchanged:
            console.print(f'  [dim]= unchanged[/dim] Desktop {item.index} ("{item.name}")')
        for unmanaged in p.unmanaged:
            console.print(f'  [dim]! unmanaged surplus[/dim] Desktop {unmanaged.index} ("{unmanaged.name}")')

        console.print(
            f"[dim]Plan:[/dim] {len(p.to_retire)} to remove, {len(p.to_create)} to create, "
            f"{len(p.to_rename)} to rename, {len(p.unchanged)} unchanged."
        )
        console.print("[yellow]Dry run:[/yellow] no changes applied.")
        return

    for name in result.retired:
        console.print(f"  [yellow]Removed[/yellow] switched-off desktop {name}")
    for create_action in result.created:
        console.print(f'  [green]Created[/green] Desktop {create_action.index} ("{create_action.name}")')
    for rename_action in result.renamed:
        if rename_action.success:
            console.print(f"  [green]Desktop {rename_action.index}[/green] -> {rename_action.target_name}")
        else:
            console.print(f"  [red]Desktop {rename_action.index}[/red] -> {rename_action.target_name} (failed)")
    for item in result.unchanged:
        console.print(f"  [dim]Desktop {item.index} -> {item.name} (already matched)[/dim]")

    if not result.converged:
        console.print(f"[red]Failed to rename {len(result.failed_renames)} desktop(s).[/red]")
        raise typer.Exit(code=1)

    console.print(f"[green]All {len(target_names)} virtual desktop(s) converged to declared state.[/green]")


def main_checkout_root(repo_root: Path) -> Path | None:
    """Return the primary worktree of the repo containing ``repo_root``, or None outside git.

    Every worktree shares one common git dir, and that dir lives inside the
    primary worktree, so its parent is the main checkout wherever this runs from.
    """
    result = subprocess.run(
        ["git", "-C", str(repo_root), "rev-parse", "--path-format=absolute", "--git-common-dir"],
        capture_output=True,
        text=True,
    )
    if result.returncode != 0 or not result.stdout.strip():
        return None
    return Path(result.stdout.strip()).parent


def same_path(left: Path, right: Path) -> bool:
    return os.path.normcase(os.path.realpath(left)) == os.path.normcase(os.path.realpath(right))


def current_user_id() -> str:
    """Return ``DOMAIN\\user`` (or bare username) for the current interactive user."""
    username = os.environ.get("USERNAME") or getpass.getuser()
    domain = os.environ.get("USERDOMAIN")
    return f"{domain}\\{username}" if domain else username
