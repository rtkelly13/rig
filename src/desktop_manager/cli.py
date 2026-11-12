"""CLI entry point for the desktop-manager workspace tool.

Provides subcommands for workspace restore, validation, status, and startup installation.
"""

from __future__ import annotations

import contextlib
import sys
import time
from collections.abc import Generator
from pathlib import Path

import typer
from typer.core import TyperGroup, TyperOption

from desktop_manager.chrome.cli import chrome_tabs_app
from desktop_manager.chrome.traces_cli import traces_app
from desktop_manager.claude_sessions.cli import claude_sessions_app
from desktop_manager.cli_align import align
from desktop_manager.cli_apps import detect_app, launch_file, restart_app, test_item, update_orca
from desktop_manager.cli_common import (
    config_option,
    console,
    console_safe_text,
    virtual_desktop_event_callback,
)
from desktop_manager.cli_machine import export_plan, guard, name_desktops, wire_machine
from desktop_manager.jobs.cli import jobs_app
from desktop_manager.models import DesktopPlan, RestoreRunLogger, WorkspacePlan
from desktop_manager.runtime_cli import runtime_app
from desktop_manager.web_cli import web_app
from desktop_manager.work.cli import work_app
from rig import tracing
from rig.platforms import Platform, requires

app = typer.Typer(name="desktop-manager", no_args_is_help=True, add_completion=False)
workspace_app = typer.Typer(name="workspace", no_args_is_help=True, help="Windows workspace management commands.")

app.add_typer(workspace_app)
app.add_typer(claude_sessions_app)
app.add_typer(chrome_tabs_app)
app.add_typer(runtime_app)
app.add_typer(work_app)
app.add_typer(jobs_app)
app.add_typer(web_app)


@app.command(name="help")
def help_cmd() -> None:
    """Show a compact overview of every command, option, and subcommand."""

    from rich.text import Text

    # --- header ---
    console.print()
    console.print(
        Text("desktop-manager", style="bold cyan"),
        Text(" — shared-utilities virtual monorepo CLI", style="dim"),
    )
    console.print()

    # --- render a command group ---
    def _render_group(group_app: typer.Typer, group_name: str) -> None:
        group = typer.main.get_command(group_app)
        commands = group.commands if isinstance(group, TyperGroup) else {group.name or group_name: group}
        for name, command in commands.items():
            doc = (command.help or "").strip().split("\n")[0]

            console.print(f"  [green]{group_name} {name}[/green]")
            console.print(f"    {doc}")

            for param in command.params:
                if isinstance(param, TyperOption):
                    flags = ", ".join([*param.opts, *param.secondary_opts])
                    console.print(f"      [cyan]{flags}[/cyan]  [dim]{param.help or ''}[/dim]")

            console.print()

    # --- workspace commands ---
    console.rule("[bold]Workspace commands[/bold]", style="dim")
    console.print()
    _render_group(workspace_app, "workspace")

    # --- claude session commands ---
    console.rule("[bold]Claude session commands[/bold]", style="dim")
    console.print()
    _render_group(claude_sessions_app, "claude-sessions")

    # --- chrome tab commands ---
    console.rule("[bold]Chrome tab commands[/bold]", style="dim")
    console.print()
    _render_group(chrome_tabs_app, "chrome-tabs")
    _render_group(traces_app, "chrome-tabs traces")

    # --- work item commands ---
    console.rule("[bold]Work item commands[/bold]", style="dim")
    console.print()
    _render_group(work_app, "work")

    # --- machine jobs ---
    console.rule("[bold]Machine jobs[/bold]", style="dim")
    console.print()
    _render_group(jobs_app, "jobs")

    # --- usage hint ---
    console.print("[dim]Run[/dim] desktop-manager <group> <command> --help [dim]for full details.[/dim]")
    console.print()


def _filter_plan_to_desktop(plan: WorkspacePlan, *, desktop: str) -> WorkspacePlan:
    filtered_desktops = [item for item in plan.desktops if item.name == desktop]
    return WorkspacePlan(
        version=plan.version,
        workspace=plan.workspace,
        personal_workspace=plan.personal_workspace,
        switch_delay_ms=plan.switch_delay_ms,
        desktops=filtered_desktops,
        notes=plan.notes,
        aliases=plan.aliases,
        features=plan.features,
    )


def _claude_session_restore_on() -> bool:
    """Whether `claude-sessions startup` will run after this logon restore to fill deferred terminals.

    With the flag off nothing fills them, so terminals keep their own command.
    """
    from rig.flags import is_enabled

    if is_enabled("claude-session-restore"):
        return True
    console.print("[dim]claude-session-restore is off: terminals run their own command.[/dim]")
    return False


def _load_plan_with_features(
    config_path: Path,
    *,
    enable: list[str],
    disable: list[str],
    require_existing_paths: bool = False,
) -> WorkspacePlan:
    """Load a plan with feature toggles applied, reporting bad flag names cleanly."""
    from desktop_manager.windows_workspace import UnknownFeatureError, load_plan

    try:
        return load_plan(
            config_path,
            require_existing_paths=require_existing_paths,
            enable_features=enable,
            disable_features=disable,
        )
    except UnknownFeatureError as error:
        console.print(f"[red]Feature error:[/red] {error}")
        raise typer.Exit(code=1) from error


def _print_active_features(plan: WorkspacePlan) -> None:
    """Show resolved feature state so a paused variant is never a silent surprise."""
    if not plan.features:
        return
    rendered = "  ".join(
        f"[green]{name}=on[/green]" if enabled else f"[dim]{name}=off[/dim]"
        for name, enabled in sorted(plan.features.items())
    )
    console.print(f"[bold]Features:[/bold] {rendered}")


def _plan_for_scope(
    plan: WorkspacePlan,
    *,
    desktop: str | None,
    screen: int | None,
) -> WorkspacePlan:
    scoped_plan = _filter_plan_to_desktop(plan, desktop=desktop) if desktop is not None else plan
    if screen is not None:
        scoped_plan = _filter_plan_to_screen(scoped_plan, desktop=None, screen=screen)
    return scoped_plan


@workspace_app.command()
@requires(Platform.WINDOWS)
def restore(
    config: Path = typer.Option(
        None,
        "--config",
        help="Path to the workspace JSON plan. Defaults to the platform default.",
    ),
    desktop: str | None = typer.Option(
        None,
        "--desktop",
        help="Only restore a single desktop by name (e.g. Personal, Work, Admin).",
    ),
    screen: int | None = typer.Option(
        None,
        "--screen",
        min=1,
        help="Only restore entries placed on the specified screen within the targeted desktop(s).",
    ),
    enable_feature: list[str] = typer.Option(
        [],
        "--enable",
        metavar="FEATURE",
        help="Turn a plan feature on for this run, overriding its default. Repeatable.",
    ),
    disable_feature: list[str] = typer.Option(
        [],
        "--disable",
        metavar="FEATURE",
        help="Turn a plan feature off for this run, overriding its default. Repeatable.",
    ),
    close: bool = typer.Option(False, "--close", help="Close existing windows before restoring."),
    strict: bool = typer.Option(False, "--strict", help="Fail if the plan does not fully cover targeted screens."),
    relaunch: bool = typer.Option(False, "--relaunch", help="Disable diff-based reuse; relaunch everything."),
    dry_run: bool = typer.Option(False, "--dry-run", help="Show what would happen without launching anything."),
    defer_logon_commands: bool = typer.Option(
        False,
        "--defer-logon-commands",
        help='Open terminals marked `command_at_logon: "deferred"` as bare shells, for '
        "`claude-sessions startup` to fill. Only for the logon run, which must call startup next. "
        "Ignored while the `claude-session-restore` rig flag is off.",
    ),
    wait_for_success: bool = typer.Option(
        False,
        "--wait-for-success",
        help="After restore, check every 10 seconds whether the UI state still matches the plan.",
    ),
    wait_poll_seconds: float = typer.Option(
        10.0,
        "--wait-poll-seconds",
        min=0.0,
        help="Seconds between wait-for-success checks.",
    ),
    wait_max_checks: int = typer.Option(
        18,
        "--wait-max-checks",
        min=1,
        help="Maximum wait-for-success checks before giving up if success is never reached.",
    ),
    wait_checks_after_success: int = typer.Option(
        3,
        "--wait-checks-after-success",
        min=1,
        help="Additional checks to run after the first successful state before continuing.",
    ),
) -> None:
    """Restore the workspace from a saved plan.

    Opens nothing, writes a note and exits 4 when fewer screens are live than the plan
    places windows on; the jobs coordinator retries on its ticks until they are back.
    """
    from desktop_manager.windows_workspace import (
        clear_virtual_desktop_event_callback,
        create_restore_run_logger,
        render_workspace_diff,
        restore_workspace_with_options,
        set_virtual_desktop_event_callback,
    )

    config_path = config or config_option()
    if not config_path.exists():
        console.print(f"[red]Config not found:[/red] {config_path}")
        raise typer.Exit(code=1)

    if not dry_run and desktop is None and screen is None:
        from desktop_manager.jobs.registry import RESTORE_DEFERRED
        from desktop_manager.jobs.session_windows import boot_time
        from desktop_manager.placement import monitor_shortfall
        from desktop_manager.plan import load_plan_context
        from desktop_manager.restore_fallbacks import open_monitor_shortfall_note

        shortfall = monitor_shortfall(
            load_plan_context(config_path, enable_features=enable_feature, disable_features=disable_feature)
        )
        if shortfall is not None:
            note_path = open_monitor_shortfall_note(shortfall.required, shortfall.live, quiet_since=boot_time())
            console.print(
                f"[yellow]Workspace restore skipped:[/yellow] plan needs {shortfall.required} screens, "
                f"only {shortfall.live} detected. Note: {note_path}."
            )
            raise typer.Exit(code=RESTORE_DEFERRED)

    plan = _load_plan_with_features(config_path, enable=enable_feature, disable=disable_feature)
    if defer_logon_commands and _claude_session_restore_on():
        from desktop_manager.plan import defer_logon_commands as defer_commands

        plan = defer_commands(plan)
    _print_active_features(plan)
    # The restore handler resolves `--desktop` to an absolute virtual-desktop index
    # itself, so it must see every desktop. Dropping the others here renumbered the
    # target to 1 and restored onto the wrong desktop -- and, because the reuse pass
    # only considers windows already on the targeted index, it relaunched everything
    # instead of adopting the windows sitting on the real one.
    restore_plan = _plan_for_scope(plan, desktop=None, screen=screen)
    scoped_plan = _plan_for_scope(plan, desktop=desktop, screen=screen)
    logger = None if dry_run else create_restore_run_logger(config_path)

    if strict:
        from desktop_manager.windows_workspace import validate_plan_structure

        expected_screen_count = 0 if screen is not None else None
        structure_issues = validate_plan_structure(
            scoped_plan, expected_screen_count=expected_screen_count, strict=True
        )
        if structure_issues:
            for issue in structure_issues:
                console.print(f"[red]Structure issue:[/red] {issue}")
            raise typer.Exit(code=1)

    # The display guard pauses while this lock is held, so it neither snapshots a
    # half-restored layout nor drags windows back while the restore moves them.
    restore_lock = contextlib.ExitStack()
    if not dry_run:
        restore_lock.enter_context(_hold_restore_lock())
    set_virtual_desktop_event_callback(virtual_desktop_event_callback(logger))
    try:
        diff_existing = not relaunch
        actions = restore_workspace_with_options(
            restore_plan,
            dry_run=dry_run,
            close_existing=close,
            diff_existing=diff_existing,
            logger=logger,
            target_desktop=desktop,
        )
        for action in actions:
            console.print(action)

        if not dry_run:
            time.sleep(3.0)
            actual_plan = _current_workspace_status_for_scope(plan, desktop=desktop, screen=screen)
            console.print("\n[bold]Final Status[/bold]")
            console.print(console_safe_text(render_workspace_diff(scoped_plan, actual_plan)))

        if not dry_run and wait_for_success:
            is_stable = _wait_for_restore_success(
                plan,
                desktop=desktop,
                screen=screen,
                logger=logger,
                poll_seconds=wait_poll_seconds,
                max_checks=wait_max_checks,
                checks_after_success=wait_checks_after_success,
            )
            if not is_stable:
                raise typer.Exit(code=1)

        if logger is not None:
            console.print(f"\n[dim]Log:[/dim] {logger.run_log_path}")
    finally:
        clear_virtual_desktop_event_callback()
        restore_lock.close()


@contextlib.contextmanager
def _hold_restore_lock() -> Generator[None]:
    from desktop_manager.display_guard import RESTORE_LOCK_FILE_NAME, hold_lock, lock_dir

    lock_path = lock_dir() / RESTORE_LOCK_FILE_NAME
    with hold_lock(lock_path, timeout_seconds=2.0, sleep=time.sleep) as acquired:
        # Two restores fighting over the same windows leave neither layout, and the
        # guard would resume and snapshot mid-restore once the first one finished.
        if not acquired:
            console.print(f"[red]Another workspace restore is already running ({lock_path}).[/red]")
            raise typer.Exit(code=1)
        yield


def _wait_for_restore_success(
    plan: WorkspacePlan,
    *,
    desktop: str | None,
    screen: int | None,
    logger: RestoreRunLogger | None,
    poll_seconds: float = 10.0,
    max_checks: int = 18,
    checks_after_success: int = 3,
) -> bool:
    from desktop_manager.windows_workspace import WorkspaceDiff, diff_workspace

    target_plan = _plan_for_scope(plan, desktop=desktop, screen=screen)

    console.print("\n[bold]Wait For Success[/bold]")
    saw_success = False
    success_checks_remaining: int | None = None
    previous_diff: WorkspaceDiff | None = None
    for check_index in range(1, max_checks + 1):
        actual_plan = _current_workspace_status_for_scope(plan, desktop=desktop, screen=screen)
        diff = diff_workspace(target_plan, actual_plan)
        is_success = diff.is_clean
        status_label = "success" if is_success else "not yet stable"
        diff_changed = diff != previous_diff
        console.print(f"Check {check_index}: {status_label}")
        if diff_changed:
            console_diff = diff.changed_since(previous_diff).render()
            console.print(console_safe_text(console_diff))
        else:
            console.print("No Change")
        if logger is not None:
            logger.log(
                f"Wait-for-success check {check_index}: {status_label} changed={'yes' if diff_changed else 'no'}"
            )
            if diff_changed:
                for line in diff.render().splitlines():
                    logger.log(f"WAIT {line}")
        if is_success:
            saw_success = True
            if success_checks_remaining is None:
                success_checks_remaining = checks_after_success
            else:
                success_checks_remaining -= 1
            if success_checks_remaining <= 0:
                return True
        elif saw_success:
            console.print(f"[red]Regression detected after initial success on check {check_index}.[/red]")
            if logger is not None:
                logger.log(f"Wait-for-success regression detected on check {check_index}")
            return False
        previous_diff = diff
        if check_index < max_checks:
            time.sleep(poll_seconds)
    return saw_success


def _current_workspace_status_for_scope(
    plan: WorkspacePlan,
    *,
    desktop: str | None,
    screen: int | None,
) -> WorkspacePlan:
    from desktop_manager.windows_workspace import current_workspace_status, current_workspace_status_for_desktop_indexes

    if desktop is not None:
        target_indexes = [index for index, item in enumerate(plan.desktops) if item.name == desktop]
        actual_plan = current_workspace_status_for_desktop_indexes([index + 1 for index in target_indexes])
    else:
        actual_plan = current_workspace_status(desktop_count=len(plan.desktops))
    if screen is not None:
        actual_plan = _filter_plan_to_screen(actual_plan, desktop=None, screen=screen)
    return actual_plan


@workspace_app.command()
@requires(Platform.WINDOWS)
def audit(
    config: Path = typer.Option(
        None,
        "--config",
        help="Path to the workspace JSON plan. Defaults to the platform default.",
    ),
    desktop: str | None = typer.Option(
        None,
        "--desktop",
        help="Only audit a single desktop by name (e.g. Personal, Work, Admin).",
    ),
    screen: int | None = typer.Option(
        None,
        "--screen",
        min=1,
        help="Only audit entries placed on the specified screen within the targeted desktop(s).",
    ),
    enable_feature: list[str] = typer.Option(
        [],
        "--enable",
        metavar="FEATURE",
        help="Audit against the plan with this feature on, overriding its default. Repeatable.",
    ),
    disable_feature: list[str] = typer.Option(
        [],
        "--disable",
        metavar="FEATURE",
        help="Audit against the plan with this feature off, overriding its default. Repeatable.",
    ),
) -> None:
    """Compare the saved workspace plan to the live desktop state without changing anything."""
    from desktop_manager.windows_workspace import diff_workspace

    config_path = config or config_option()
    if not config_path.exists():
        console.print(f"[red]Config not found:[/red] {config_path}")
        raise typer.Exit(code=1)

    plan = _load_plan_with_features(config_path, enable=enable_feature, disable=disable_feature)
    scoped_plan = _plan_for_scope(plan, desktop=desktop, screen=screen)
    actual_plan = _current_workspace_status_for_scope(plan, desktop=desktop, screen=screen)
    diff = diff_workspace(scoped_plan, actual_plan)

    console.print("[bold]Workspace Audit[/bold]")
    console.print(console_safe_text(diff.render()))
    if diff.is_clean:
        console.print("[green]Audit passed.[/green]")
        return
    console.print("[yellow]Audit found drift.[/yellow]")
    raise typer.Exit(code=1)


def _filter_plan_to_screen(plan: WorkspacePlan, *, desktop: str | None, screen: int) -> WorkspacePlan:
    filtered_desktops: list[DesktopPlan] = []
    for item in plan.desktops:
        if desktop is not None and item.name != desktop:
            continue
        filtered_entries = [
            entry for entry in item.launch if entry.placement is not None and entry.placement.screen == screen
        ]
        filtered_windows = [window for window in item.captured_windows if window.screen_number == screen]
        filtered_desktops.append(
            DesktopPlan(name=item.name, launch=filtered_entries, captured_windows=filtered_windows)
        )
    return WorkspacePlan(
        version=plan.version,
        workspace=plan.workspace,
        personal_workspace=plan.personal_workspace,
        switch_delay_ms=plan.switch_delay_ms,
        desktops=filtered_desktops,
        notes=plan.notes,
        aliases=plan.aliases,
        features=plan.features,
    )


@workspace_app.command(name="validate-plan")
def validate_plan_cmd(
    config: Path = typer.Option(
        None,
        "--config",
        help="Path to the workspace JSON plan.",
    ),
    strict: bool = typer.Option(False, "--strict", help="Treat overlapping screen regions as errors."),
    enable_feature: list[str] = typer.Option(
        [],
        "--enable",
        metavar="FEATURE",
        help="Validate the plan with this feature on, overriding its default. Repeatable.",
    ),
    disable_feature: list[str] = typer.Option(
        [],
        "--disable",
        metavar="FEATURE",
        help="Validate the plan with this feature off, overriding its default. Repeatable.",
    ),
) -> None:
    """Validate the workspace plan JSON."""
    from desktop_manager.windows_workspace import (
        validate_plan,
        validate_plan_structure,
    )

    config_path = config or config_option()
    if not config_path.exists():
        console.print(f"[red]Config not found:[/red] {config_path}")
        raise typer.Exit(code=1)

    plan = _load_plan_with_features(config_path, enable=enable_feature, disable=disable_feature)
    errors = validate_plan(plan, require_existing_paths=True)
    structure_issues = validate_plan_structure(plan, strict=strict)

    if errors:
        for error in errors:
            console.print(f"[red]Error:[/red] {error}")
    if structure_issues:
        for issue in structure_issues:
            console.print(f"[yellow]Structure:[/yellow] {issue}")

    if errors or structure_issues:
        raise typer.Exit(code=1)

    console.print("[green]Plan is valid.[/green]")


@workspace_app.command(name="features")
def features_cmd(
    config: Path = typer.Option(
        None,
        "--config",
        help="Path to the workspace JSON plan. Defaults to the platform default.",
    ),
) -> None:
    """List the plan's feature toggles, their default state, and what sits behind each."""
    from desktop_manager.plan import stored_flag_states, with_stored_flags
    from desktop_manager.windows_workspace import UnknownFeatureError, parse_plan_json

    config_path = config or config_option()
    if not config_path.exists():
        console.print(f"[red]Config not found:[/red] {config_path}")
        raise typer.Exit(code=1)

    try:
        # Read the plan without applying the flags: this command exists to show what a
        # feature owns, and a switched-off desktop is absent from the applied plan.
        parsed = parse_plan_json(
            config_path.read_text(encoding="utf-8"),
            source=str(config_path),
            base_dir=config_path.parent,
        )
    except UnknownFeatureError as error:
        console.print(f"[red]Feature error:[/red] {error}")
        raise typer.Exit(code=1) from error

    states = stored_flag_states()
    plan = with_stored_flags(parsed, states)
    overridden = {state.flag.name for state in states if state.override is not None}

    console.print("[bold]Workspace Features[/bold] [dim](defaults and overrides: `rig flags list`)[/dim]")
    for name, enabled in sorted(plan.features.items()):
        state = "[green]on[/green]" if enabled else "[dim]off (paused)[/dim]"
        source = "rig flags override" if name in overridden else "plan" if name in parsed.features else "default"
        console.print(f"\n  {name}: {state} [dim]({source})[/dim]")
        for desktop in plan.desktops:
            if desktop.feature == name:
                console.print(console_safe_text(f"    - {desktop.name}: whole desktop"))
            for entry in desktop.launch:
                if entry.feature == name:
                    console.print(console_safe_text(f"    - {desktop.name}: {entry.name}"))

    unowned = [
        f"{desktop.name}/{entry.name}"
        for desktop in plan.desktops
        for entry in desktop.launch
        if entry.feature is not None and entry.feature not in plan.features
    ]
    unowned.extend(
        f"{desktop.name} (whole desktop)"
        for desktop in plan.desktops
        if desktop.feature is not None and desktop.feature not in plan.features
    )
    if unowned:
        # A reference to an undeclared feature resolves to off, so surface it
        # rather than letting a typo quietly delete a window from every restore.
        console.print("\n[red]Referencing undeclared features (treated as off):[/red]")
        for item in unowned:
            console.print(console_safe_text(f"  - {item}"))
        raise typer.Exit(code=1)


@workspace_app.command()
@requires(Platform.WINDOWS)
def status(
    verbose: bool = typer.Option(False, "--verbose", "-v", help="Show detailed window information."),
) -> None:
    """Show current desktop and window status."""
    from desktop_manager.windows_workspace import current_workspace_status, get_virtual_desktop_count

    status_plan = current_workspace_status(desktop_count=get_virtual_desktop_count())
    for index, desktop in enumerate(status_plan.desktops):
        if index > 0:
            console.print()
        console.print(f"Desktop: [bold]{desktop.name}[/bold]")
        if not desktop.captured_windows:
            console.print("  (no visible windows)")
            continue
        for window in desktop.captured_windows:
            line = f"  - {window.title}"
            if window.screen_number is not None:
                line += f"  [dim]screen {window.screen_number}[/dim]"
            if verbose and window.process_name:
                line += f"  [dim]({window.process_name})[/dim]"
            if verbose and window.exe_path:
                line += f"  [dim]{window.exe_path}[/dim]"
            console.print(console_safe_text(line))


workspace_app.command(name="align")(align)
workspace_app.command(name="wire-machine")(wire_machine)
workspace_app.command(name="export-plan")(export_plan)
workspace_app.command(name="guard")(guard)
workspace_app.command(name="name-desktops")(name_desktops)
workspace_app.command(name="ensure-desktops")(name_desktops)
workspace_app.command(name="sync-desktops")(name_desktops)
workspace_app.command(name="test-item")(test_item)
workspace_app.command(name="restart-app")(restart_app)
workspace_app.command(name="update-orca")(update_orca)
workspace_app.command(name="launch-file")(launch_file)
workspace_app.command(name="detect-app")(detect_app)


def main() -> None:
    """Entry point for the desktop-manager CLI.

    Each run is one trace: a root span named after the subcommand, with the restore
    phases, per-desktop and per-entry work, virtual-desktop calls, and every helper
    subprocess as children. Spans land in ``temp/traces/sdk/desktop-manager/``, or,
    under the jobs coordinator from a runtime copy, in
    ``%LOCALAPPDATA%/shared-utilities/traces/desktop-manager/``.
    """
    tracing.init_tracing("desktop-manager")
    command = " ".join(arg for arg in sys.argv[1:3] if not arg.startswith("-"))
    with tracing.span(f"desktop-manager {command}".rstrip(), scope="desktop_manager.cli", **{"cli.argv": sys.argv[1:]}):
        app()


if __name__ == "__main__":
    main()
