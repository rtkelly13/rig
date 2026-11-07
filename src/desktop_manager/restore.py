from __future__ import annotations

import time
from dataclasses import dataclass
from pathlib import Path

from desktop_manager import launching
from desktop_manager.models import (
    DesktopPlan,
    LaunchEntry,
    RestoreRunLogger,
    WindowSnapshot,
    WorkspacePlan,
)
from desktop_manager.plan import (
    build_launch_entries_for_desktop,
)
from desktop_manager.restore_readiness import wait_for_launched_entry_ready
from desktop_manager.restore_timing import (
    logger_callback,
    new_terminal_restore_token,
    should_force_terminal_relaunch,
)
from desktop_manager.window_reuse import (
    describe_launch,
)


@dataclass(frozen=True, slots=True)
class EntryLocation:
    """A launch entry together with the desktop (and its 1-based index) it lives on."""

    desktop_index: int
    desktop: DesktopPlan
    entry: LaunchEntry


def capture_workspace_plan(desktop_count: int | None = None, workspace: Path | None = None) -> WorkspacePlan:
    from desktop_manager.win32 import ensure_windows
    from desktop_manager.win32.desktops import get_virtual_desktop_count

    ensure_windows()
    resolved_count = desktop_count if desktop_count is not None else get_virtual_desktop_count()
    if resolved_count < 1:
        raise ValueError("Desktop count must be at least 1.")

    desktops = _captured_desktop_plans_from_inventory(resolved_count, workspace)
    notes = [
        (
            "Captured launch commands are a draft. Review browser paths, URLs, and profile-specific "
            "arguments before using restore."
        ),
        "Desktop 1 terminal windows are converted into workspace terminal launchers when a workspace is supplied.",
    ]
    return WorkspacePlan(
        workspace=str(workspace) if workspace is not None else None,
        desktops=desktops,
        notes=notes,
    )


def current_workspace_status(desktop_count: int | None = None) -> WorkspacePlan:
    from desktop_manager.win32 import ensure_windows
    from desktop_manager.win32.desktops import get_virtual_desktop_count

    ensure_windows()
    resolved_count = desktop_count if desktop_count is not None else get_virtual_desktop_count()
    if resolved_count < 1:
        raise ValueError("Desktop count must be at least 1.")

    desktops = _desktop_statuses_from_inventory(range(1, resolved_count + 1))
    return WorkspacePlan(desktops=desktops)


def current_workspace_status_for_desktop_indexes(desktop_indexes: list[int]) -> WorkspacePlan:
    from desktop_manager.win32 import ensure_windows

    ensure_windows()
    if not desktop_indexes:
        return WorkspacePlan(desktops=[])

    unique_indexes = sorted(set(desktop_indexes))
    if any(desktop_number < 1 for desktop_number in unique_indexes):
        raise ValueError("Desktop indexes must be at least 1.")

    captured_by_index = {
        desktop_number: desktop
        for desktop_number, desktop in zip(
            unique_indexes, _desktop_statuses_from_inventory(unique_indexes), strict=True
        )
    }
    return WorkspacePlan(desktops=[captured_by_index[index] for index in desktop_indexes])


def _desktop_statuses_from_inventory(desktop_indexes: list[int] | range) -> list[DesktopPlan]:
    windows_by_desktop = _inventory_windows_by_desktop()
    return [
        DesktopPlan(
            name=f"Desktop {desktop_number}",
            captured_windows=windows_by_desktop.get(desktop_number, []),
        )
        for desktop_number in desktop_indexes
    ]


def _captured_desktop_plans_from_inventory(desktop_count: int, workspace: Path | None) -> list[DesktopPlan]:
    windows_by_desktop = _inventory_windows_by_desktop()
    desktops: list[DesktopPlan] = []
    for desktop_number in range(1, desktop_count + 1):
        windows = windows_by_desktop.get(desktop_number, [])
        desktops.append(
            DesktopPlan(
                name=f"Desktop {desktop_number}",
                launch=build_launch_entries_for_desktop(windows, desktop_number, workspace),
                captured_windows=windows,
            )
        )
    return desktops


def _inventory_windows_by_desktop() -> dict[int, list[WindowSnapshot]]:
    from desktop_manager.win32.inventory import inventory_reusable_windows

    windows_by_desktop: dict[int, list[WindowSnapshot]] = {}
    for window in inventory_reusable_windows():
        if window.desktop_number is None or window.desktop_number < 1:
            continue
        windows_by_desktop.setdefault(window.desktop_number, []).append(
            WindowSnapshot(
                title=window.title,
                pid=window.pid,
                process_name=window.process_name,
                exe_path=window.exe_path,
                class_name=window.class_name,
                screen_number=window.screen_number,
                inferred_placement=window.inferred_placement,
                bounds=window.bounds,
            )
        )

    for windows in windows_by_desktop.values():
        windows.sort(key=lambda snapshot: ((snapshot.process_name or "").lower(), snapshot.title.lower()))
    return windows_by_desktop


def capture_current_desktop_plan(
    desktop_number: int,
    workspace: Path | None,
    *,
    desktop_name: str | None = None,
) -> DesktopPlan:
    from desktop_manager.win32.inventory import list_current_desktop_windows

    windows = list_current_desktop_windows()
    return DesktopPlan(
        name=desktop_name or f"Desktop {desktop_number}",
        launch=build_launch_entries_for_desktop(windows, desktop_number, workspace),
        captured_windows=windows,
    )


def current_desktop_status(*, desktop_name: str | None = None) -> DesktopPlan:
    from desktop_manager.win32.inventory import list_current_desktop_windows

    return DesktopPlan(name=desktop_name or "Current Desktop", captured_windows=list_current_desktop_windows())


def find_launch_entry(plan: WorkspacePlan, desktop_name: str, entry_name: str) -> EntryLocation:
    for desktop_index, desktop in enumerate(plan.desktops, start=1):
        if desktop.name != desktop_name:
            continue
        for entry in desktop.launch:
            if entry.name == entry_name:
                return EntryLocation(desktop_index, desktop, entry)
        raise ValueError(f"Entry not found on desktop `{desktop_name}`: {entry_name}")
    raise ValueError(f"Desktop not found: {desktop_name}")


def launch_single_entry(
    plan: WorkspacePlan,
    desktop_name: str,
    entry_name: str,
    *,
    dry_run: bool = False,
    logger: RestoreRunLogger | None = None,
) -> list[str]:
    from desktop_manager.win32.desktops import ensure_current_desktop_number
    from desktop_manager.win32.entry_windows import apply_window_placement

    location = find_launch_entry(plan, desktop_name, entry_name)
    desktop_index, desktop, entry = location.desktop_index, location.desktop, location.entry
    actions = [f"Desktop {desktop_index}: {desktop.name}", f"  - {describe_launch(entry, plan.substitutions())}"]

    if logger is not None:
        logger.log(f"Single-entry launch dry_run={dry_run} desktop={desktop.name} entry={entry.name}")

    if dry_run:
        return actions

    ensure_current_desktop_number(desktop_index, settle_seconds=plan.switch_delay_ms / 1000)
    launch_token: str | None = new_terminal_restore_token(entry) if should_force_terminal_relaunch(entry) else None
    launching.launch_entry(entry, plan.substitutions(), restore_token=launch_token)
    if logger is not None:
        logger.log(f"Launched single entry: {entry.name}")
        if launch_token is not None:
            logger.log(f"Launched {entry.name} with restore token {launch_token}")
    ready_hwnd = wait_for_launched_entry_ready(entry, desktop_index=desktop_index, logger=logger)
    if entry.placement:
        placement_started_at = time.monotonic()
        moved_to_desktop = apply_window_placement(
            entry,
            hwnd=ready_hwnd,
            target_desktop_number=desktop_index,
            diagnostic_callback=logger_callback(logger),
        ).moved_to_target_desktop
        if logger is not None and moved_to_desktop:
            logger.log(f"Moved launched window for {entry.name} onto desktop {desktop_index} before placement")
        if logger is not None:
            logger.log(
                f"Applied placement {entry.placement.to_dict()} to {entry.name} "
                f"after {time.monotonic() - placement_started_at:.2f}s"
            )
        _log_post_entry_desktop_state(entry, desktop, logger=logger, plan=plan)
    return actions


def find_launch_entries_by_name(
    plan: WorkspacePlan, name: str, *, target_desktop: str | None = None
) -> list[EntryLocation]:
    """Return every entry whose name matches (case-insensitive), with its desktop."""
    target = name.casefold()
    matches: list[EntryLocation] = []
    for desktop_index, desktop in enumerate(plan.desktops, start=1):
        if target_desktop is not None and desktop.name != target_desktop:
            continue
        for entry in desktop.launch:
            if entry.name.casefold() == target:
                matches.append(EntryLocation(desktop_index, desktop, entry))
    return matches


def restart_app_entries(
    plan: WorkspacePlan,
    name: str,
    *,
    target_desktop: str | None = None,
    graceful: bool = False,
    dry_run: bool = False,
    logger: RestoreRunLogger | None = None,
    close_wait_seconds: float = 2.0,
    exit_timeout_seconds: float = 5.0,
) -> list[str]:
    """Forcefully close every window of an app type, then re-open it in its planned placement.

    The app is identified by launch-entry name (e.g. ``Fork``). Every matching entry is
    re-launched and placed via the normal launch path so it lands in the right shape.
    """
    from desktop_manager.win32.geometry import request_close_window
    from desktop_manager.win32.inventory import (
        collect_windows_matching_process,
        process_ids_for_process_name,
        terminate_process_id,
        wait_for_process_exit,
    )

    matches = find_launch_entries_by_name(plan, name, target_desktop=target_desktop)
    if not matches:
        scope = f" on desktop `{target_desktop}`" if target_desktop is not None else ""
        raise ValueError(f"No launch entries named `{name}`{scope} found in the plan.")

    method = "graceful, then force-kill" if graceful else "force-kill"
    entry_labels = ", ".join(f"{match.desktop.name}/{match.entry.name}" for match in matches)
    actions: list[str] = [f"Restart app `{name}` ({len(matches)} entry(s): {entry_labels})"]
    if logger is not None:
        logger.log(f"Restart app {name} graceful={graceful} dry_run={dry_run} matches={entry_labels}")

    process_names: list[str] = []
    seen_processes: set[str] = set()
    for match in matches:
        process_name = match.entry.window_process_name
        if process_name and process_name.casefold() not in seen_processes:
            seen_processes.add(process_name.casefold())
            process_names.append(process_name)

    if not process_names:
        actions.append("  - WARNING: no window_process_name configured for these entries; cannot identify windows")

    if dry_run:
        for process_name in process_names:
            pids = process_ids_for_process_name(process_name.casefold(), current_desktop_only=False)
            actions.append(f"  - would close all `{process_name}` windows ({method}); {len(pids)} process(es) running")
        for match in matches:
            desktop, entry = match.desktop, match.entry
            if entry.disabled:
                actions.append(f"  - would skip disabled entry `{entry.name}` on {desktop.name}")
            else:
                actions.append(f"  - would relaunch `{entry.name}` on {desktop.name}")
        return actions

    pids_to_kill: set[int] = set()
    for process_name in process_names:
        pids_to_kill |= process_ids_for_process_name(process_name.casefold(), current_desktop_only=False)

    if graceful and process_names:
        handles: list[int] = []
        for process_name in process_names:
            handles += collect_windows_matching_process(process_name.casefold(), current_desktop_only=False)
        for hwnd in handles:
            request_close_window(hwnd)
        actions.append(f"  - requested polite close for {len(handles)} window(s)")
        if logger is not None:
            logger.log(f"Requested WM_CLOSE for {len(handles)} window(s) of {name}")
        time.sleep(close_wait_seconds)
        survivors: set[int] = set()
        for process_name in process_names:
            survivors |= process_ids_for_process_name(process_name.casefold(), current_desktop_only=False)
        pids_to_kill = survivors

    killed = 0
    for pid in pids_to_kill:
        if terminate_process_id(pid):
            killed += 1
    for pid in pids_to_kill:
        wait_for_process_exit(pid, timeout_seconds=exit_timeout_seconds)
    actions.append(f"  - closed {killed} process(es) ({method})")
    if logger is not None:
        logger.log(f"Force-killed {killed} process(es) for {name}; pids={sorted(pids_to_kill)}")
    if killed:
        time.sleep(0.5)

    for match in matches:
        desktop, entry = match.desktop, match.entry
        if entry.disabled:
            actions.append(f"  - skip disabled entry `{entry.name}` on {desktop.name}")
            continue
        relaunch_actions = launch_single_entry(plan, desktop.name, entry.name, dry_run=False, logger=logger)
        actions.extend(f"  {line}" for line in relaunch_actions)

    return actions


def _log_post_entry_desktop_state(
    entry: LaunchEntry,
    desktop: DesktopPlan,
    *,
    logger: RestoreRunLogger | None,
    plan: WorkspacePlan | None,
) -> None:
    if logger is None:
        return

    actual_desktop = current_desktop_status(desktop_name=desktop.name)
    logger.log(f"Post-entry status for {entry.name}: {len(actual_desktop.captured_windows)} visible window(s)")
    if plan is None:
        return

    from desktop_manager.visualization import render_workspace_diff

    scoped_plan = WorkspacePlan(
        version=plan.version,
        workspace=plan.workspace,
        personal_workspace=plan.personal_workspace,
        switch_delay_ms=plan.switch_delay_ms,
        desktops=[desktop],
        notes=plan.notes,
        aliases=plan.aliases,
    )
    diff = render_workspace_diff(scoped_plan, WorkspacePlan(desktops=[actual_desktop]))
    for line in diff.splitlines()[1:]:
        logger.log(f"STATE {line}")
