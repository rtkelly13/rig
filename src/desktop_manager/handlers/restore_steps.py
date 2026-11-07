"""Executing one desktop's restore steps: reuse or launch each entry, retry the desktop, verify it."""

from __future__ import annotations

import time
from collections.abc import Callable
from dataclasses import dataclass
from typing import TYPE_CHECKING

from desktop_manager.commands.workspace_commands import WorkspaceCommands
from desktop_manager.models import DesktopPlan, LaunchEntry, RestoreRunLogger, WorkspacePlan
from desktop_manager.queries.workspace_queries import WorkspaceQueries
from desktop_manager.restore_fallbacks import is_missing_path_error
from desktop_manager.restore_models import PlannedDesktopExecution, PlannedEntryExecution
from desktop_manager.restore_timing import new_terminal_restore_token, should_force_terminal_relaunch
from desktop_manager.win32.errors import WIN32_OPTIONAL_ERRORS
from rig import tracing

if TYPE_CHECKING:
    from desktop_manager.restore_fallbacks import RestoreFailureFallbacks


_MAX_DESKTOP_RESTORE_ATTEMPTS = 3

RESTORE_SCOPE = "desktop_manager.restore"


def refresh_desktop_execution_plan(
    plan: WorkspacePlan,
    desktop_step: PlannedDesktopExecution,
    *,
    diff_existing: bool,
    logger: RestoreRunLogger | None,
    queries: WorkspaceQueries,
    preserved_hwnds: set[int],
) -> None:
    from desktop_manager.window_pid_file import pid_verified
    from desktop_manager.window_reuse import claim_reusable_window, competing_sibling_titles, describe_launch

    substitutions = plan.substitutions()
    desktop_step.entry_steps = []
    desktop_step.orphan_windows = []

    def _satisfied_step(entry: LaunchEntry) -> PlannedEntryExecution:
        return PlannedEntryExecution(
            desktop=desktop_step.desktop,
            desktop_index=desktop_step.desktop_index,
            entry=entry,
            description=describe_launch(entry, substitutions),
            action_kind="satisfied",
        )

    if not diff_existing or desktop_step.close_existing:
        desktop_step.entry_steps = [
            _satisfied_step(entry)
            if entry.name in desktop_step.satisfied_entry_names
            else PlannedEntryExecution(
                desktop=desktop_step.desktop,
                desktop_index=desktop_step.desktop_index,
                entry=entry,
                description=describe_launch(entry, substitutions),
                action_kind="launch",
            )
            for entry in desktop_step.desktop.launch
            if not entry.disabled
        ]
        return

    # Exclude the invoking window(s) — the foreground/console window of the
    # process driving the restore, which is the live Claude Code session that
    # launched it.  Without this, that session's WindowsTerminal window matches
    # a terminal entry on process name + location and gets claimed as a
    # workspace-terminal slot: it is then moved/repositioned (and closed in the
    # close path), terminating the running session.  Keeping it out of the
    # reusable inventory retains it in place.
    reusable_windows = queries.inventory_reusable_windows(excluded_hwnds=preserved_hwnds)
    # A window an entry here pins by recorded pid is that entry's own instance even on
    # another desktop, so it stays a candidate and is moved here instead of relaunched.
    pinned_entries = [entry for entry in desktop_step.desktop.launch if entry.window_pid_file and not entry.disabled]
    desktop_windows = [
        window
        for window in reusable_windows
        if window.desktop_number == desktop_step.desktop_index
        or window.desktop_number is None
        or any(pid_verified(entry, window.pid, substitutions) for entry in pinned_entries)
    ]
    if logger is not None:
        logger.log(
            f"Desktop {desktop_step.desktop_index} reusable inventory refresh collected "
            f"{len(desktop_windows)} candidate window(s)"
        )

    remaining_windows = list(desktop_windows)
    for entry in desktop_step.desktop.launch:
        if entry.disabled:
            continue
        description = describe_launch(entry, substitutions)
        # Claim the window even for a satisfied entry, so a later entry on this
        # desktop cannot grab the window the completion check matched it against.
        claim = claim_reusable_window(
            entry,
            remaining_windows,
            substitutions,
            target_desktop_number=desktop_step.desktop_index,
            competing_titles=competing_sibling_titles(desktop_step.desktop.launch, entry),
        )
        reusable_window, remaining_windows = claim.window, claim.remaining
        if entry.name in desktop_step.satisfied_entry_names:
            desktop_step.entry_steps.append(_satisfied_step(entry))
            continue
        desktop_step.entry_steps.append(
            PlannedEntryExecution(
                desktop=desktop_step.desktop,
                desktop_index=desktop_step.desktop_index,
                entry=entry,
                description=description,
                action_kind="reuse" if reusable_window is not None else "launch",
                reusable_window=reusable_window,
            )
        )

    desktop_step.orphan_windows = sorted(
        remaining_windows,
        key=lambda window: ((window.process_name or "").lower(), window.title.lower(), window.hwnd),
    )
    if logger is not None and desktop_step.orphan_windows:
        logger.log(
            f"Desktop {desktop_step.desktop_index} orphan open windows: "
            + ", ".join(window.title for window in desktop_step.orphan_windows)
        )


def _execute_desktop_step(
    plan: WorkspacePlan,
    desktop_step: PlannedDesktopExecution,
    *,
    preserved_hwnds: set[int],
    logger: RestoreRunLogger | None,
    queries: WorkspaceQueries,
    commands: WorkspaceCommands,
    fallback_state: RestoreFailureFallbacks,
) -> list[LaunchEntry]:
    pending_second_pass_entries: list[LaunchEntry] = []
    substitutions = plan.substitutions()

    if logger is not None:
        logger.log(f"Processing desktop {desktop_step.desktop_index}: {desktop_step.desktop.name}")

    if desktop_step.close_existing:
        closed_count = commands.close_current_desktop_windows(preserved_hwnds=preserved_hwnds)
        if logger is not None:
            logger.log(f"Requested close for {closed_count} existing window(s) on {desktop_step.desktop.name}")
        time.sleep(0.5)

    for entry_step in desktop_step.entry_steps:
        if entry_step.action_kind == "satisfied":
            if logger is not None:
                logger.log(f"Skipping {entry_step.entry.name}: already verified in its planned slot")
            continue
        confirmed = commands.ensure_current_desktop_number(entry_step.desktop_index, settle_seconds=0.1)
        if logger is not None:
            anchor_status = "confirmed" if confirmed else "best-effort only"
            logger.log(
                f"Desktop {entry_step.desktop_index} pre-entry check for {entry_step.entry.name}: {anchor_status}"
            )
        with tracing.span(
            f"restore.entry.{entry_step.action_kind}",
            scope=RESTORE_SCOPE,
            **{
                "desktop.index": entry_step.desktop_index,
                "entry.name": entry_step.entry.name,
                "entry.type": entry_step.entry.type,
                "entry.process": entry_step.entry.window_process_name or "",
            },
        ) as entry_span:
            if entry_step.action_kind == "reuse" and entry_step.reusable_window is not None:
                entry_span.set(**{"window.hwnd": entry_step.reusable_window.hwnd})
                entry_pending = _execute_reuse_step(
                    entry_step,
                    logger=logger,
                    commands=commands,
                    plan=plan,
                )
            else:
                entry_pending = _execute_launch_step(
                    entry_step,
                    substitutions=substitutions,
                    logger=logger,
                    commands=commands,
                    queries=queries,
                    plan=plan,
                    fallback_state=fallback_state,
                    already_launched=entry_step.entry.name in desktop_step.launched_entry_names,
                    reserved_hwnds=_sibling_owned_hwnds(desktop_step, entry_step.entry, logger=logger),
                )
                desktop_step.launched_entry_names.add(entry_step.entry.name)
            entry_span.set(**{"entry.deferred_second_pass": bool(entry_pending)})
        pending_second_pass_entries.extend(entry_pending)

    if logger is not None:
        if pending_second_pass_entries:
            logger.log(
                f"Desktop {desktop_step.desktop_index} first pass incomplete; "
                f"{len(pending_second_pass_entries)} entry(s) queued for second pass"
            )
        else:
            logger.log(f"Desktop {desktop_step.desktop_index} first pass fully successful")

    return pending_second_pass_entries


def _execute_reuse_step(
    entry_step: PlannedEntryExecution,
    *,
    logger: RestoreRunLogger | None,
    commands: WorkspaceCommands,
    plan: WorkspacePlan,
) -> list[LaunchEntry]:
    from desktop_manager.window_reuse import is_window_target_confirmed

    reusable_window = entry_step.reusable_window
    if reusable_window is None:
        return []
    pending_second_pass_entries: list[LaunchEntry] = []
    if logger is not None:
        logger.log(
            f"Reusing window {reusable_window.hwnd} for {entry_step.entry.name} "
            f"pid={reusable_window.pid or '?'} title={reusable_window.title!r} "
            f"process={reusable_window.process_name!r} "
            f"desktop={reusable_window.desktop_number}"
        )
    try:
        move = commands.move_existing_window_to_entry(
            reusable_window,
            entry_step.desktop_index,
            entry_step.entry,
        )
        if logger is not None and move.moved_to_desktop:
            logger.log(
                f"Moved reused window {reusable_window.hwnd} for {entry_step.entry.name} "
                f"from desktop {reusable_window.desktop_number or '?'} to {entry_step.desktop_index}"
            )
        if logger is not None and move.moved_to_placement and entry_step.entry.placement is not None:
            logger.log(
                f"Repositioned reused window {reusable_window.hwnd} for {entry_step.entry.name} "
                f"to {entry_step.entry.placement.to_dict()}"
            )
        if entry_step.entry.placement is not None and entry_step.entry.type != "terminal":
            if is_window_target_confirmed(reusable_window.hwnd, entry_step.desktop_index, entry_step.entry):
                if logger is not None:
                    logger.log(f"First-pass placement verified for {entry_step.entry.name}")
            else:
                pending_second_pass_entries.append(entry_step.entry)
                if logger is not None:
                    logger.log(f"Deferred second-pass placement for {entry_step.entry.name}")
    except Exception as err:
        if logger is not None:
            logger.exception(f"FAILED reuse {entry_step.entry.name}: {err}; falling back to relaunch")
        # Best-effort fallback to launch through the normal launch path.
        launch_step = PlannedEntryExecution(
            desktop=entry_step.desktop,
            desktop_index=entry_step.desktop_index,
            entry=entry_step.entry,
            description=entry_step.description,
            action_kind="launch",
        )
        pending_second_pass_entries.extend([])
        return _execute_launch_step(
            launch_step,
            substitutions=plan.substitutions(),
            logger=logger,
            commands=commands,
            queries=WorkspaceQueries(),
            plan=plan,
            fallback_state=None,
            preserve_launch_signature=True,
        )
    return pending_second_pass_entries


def _execute_launch_step(
    entry_step: PlannedEntryExecution,
    *,
    substitutions: dict[str, str],
    logger: RestoreRunLogger | None,
    commands: WorkspaceCommands,
    queries: WorkspaceQueries,
    plan: WorkspacePlan,
    fallback_state: RestoreFailureFallbacks | None,
    preserve_launch_signature: bool = False,
    already_launched: bool = False,
    reserved_hwnds: set[int] | None = None,
) -> list[LaunchEntry]:
    from desktop_manager.restore_timing import entry_window_wait_timeout_seconds
    from desktop_manager.window_reuse import is_entry_target_confirmed

    pending_second_pass_entries: list[LaunchEntry] = []
    entry = entry_step.entry

    if logger is not None:
        logger.log(entry_step.description)

    # A retry means the previous attempt's window never showed up in time. If the
    # instance we started is still alive, wait for *it* instead of launching
    # again: three attempts against a slow-starting Fork spawned three processes
    # on 2026-08-05, none of which landed in the planned slot.
    adopt_existing = already_launched and _entry_process_is_running(entry)
    if adopt_existing and logger is not None:
        logger.log(
            f"Not relaunching {entry.name}: the instance launched earlier this run is still "
            "starting; waiting for its window instead"
        )

    # Only exclude pre-existing windows when we are about to create a new one.
    # When adopting, the window we want is by definition one of the existing ones,
    # so all that stays excluded is what demonstrably belongs to a sibling.
    existing_process_handles: set[int] = set(reserved_hwnds or ())
    launch_token: str | None = new_terminal_restore_token(entry) if should_force_terminal_relaunch(entry) else None
    if entry.window_process_name and not adopt_existing:
        existing_process_handles |= queries.collect_windows_matching_process(entry.window_process_name)
        if logger is not None and existing_process_handles:
            logger.log(f"Snapshot existing process windows for {entry.name}: {sorted(existing_process_handles)}")

    if not adopt_existing:
        try:
            if preserve_launch_signature and launch_token is None:
                launched_pid = commands.launch_entry(entry, substitutions)
            else:
                launched_pid = commands.launch_entry(entry, substitutions, restore_token=launch_token)
            if logger is not None and launched_pid is not None:
                logger.log(f"Launched {entry.name} pid={launched_pid}")
            if logger is not None and launch_token is not None:
                logger.log(f"Launched {entry.name} with restore token {launch_token}")
        except Exception as err:
            if logger is not None:
                logger.exception(f"FAILED {entry.name}: {err}")
            if fallback_state is not None and is_missing_path_error(err):
                fallback_state.record_missing_path(entry_step.desktop_index, entry, str(err))
            return pending_second_pass_entries

    try:
        ready_hwnd = _wait_for_launched_entry_ready(
            entry,
            desktop_index=entry_step.desktop_index,
            logger=logger,
            excluded_hwnds=existing_process_handles,
            queries=queries,
        )
    except Exception as err:
        if logger is not None:
            logger.exception(f"FAILED readiness {entry.name}: {err}")
        return pending_second_pass_entries

    if entry.placement is None:
        return pending_second_pass_entries

    try:
        placement_started_at = time.monotonic()
        moved_to_desktop = commands.apply_window_placement(
            entry,
            hwnd=ready_hwnd,
            target_desktop_number=entry_step.desktop_index,
            excluded_hwnds=existing_process_handles,
            diagnostic_callback=_diagnostic_callback(logger),
            timeout_seconds=entry_window_wait_timeout_seconds(entry),
        ).moved_to_target_desktop
        if logger is not None and moved_to_desktop:
            logger.log(
                f"Moved launched window for {entry.name} onto desktop {entry_step.desktop_index} before placement"
            )
        if logger is not None:
            logger.log(
                f"Applied placement {entry.placement.to_dict()} to {entry.name} "
                f"after {time.monotonic() - placement_started_at:.2f}s"
            )
        _ensure_desktop_anchor(
            entry_step.desktop_index,
            entry_name=entry.name,
            reason="post-entry status",
            logger=logger,
            queries=queries,
            commands=commands,
            settle_seconds=min(plan.switch_delay_ms / 1000, 0.2),
        )
        _log_post_entry_desktop_state(entry, entry_step.desktop, logger=logger, plan=plan, queries=queries)
        if entry.type != "terminal":
            if is_entry_target_confirmed(entry, entry_step.desktop_index, timeout_seconds=0.35):
                if logger is not None:
                    logger.log(f"First-pass placement verified for {entry.name}")
            else:
                pending_second_pass_entries.append(entry)
                if logger is not None:
                    logger.log(f"Deferred second-pass placement for {entry.name}")
    except Exception as err:
        if logger is not None:
            logger.exception(f"FAILED placement {entry.name}: {err}")

    return pending_second_pass_entries


def execute_desktop_with_retries(
    plan: WorkspacePlan,
    desktop_step: PlannedDesktopExecution,
    *,
    preserved_hwnds: set[int],
    logger: RestoreRunLogger | None,
    queries: WorkspaceQueries,
    commands: WorkspaceCommands,
    fallback_state: RestoreFailureFallbacks,
) -> list[LaunchEntry]:
    pending_second_pass_entries: list[LaunchEntry] = []
    desktop_complete = False

    for attempt_index in range(1, _MAX_DESKTOP_RESTORE_ATTEMPTS + 1):
        if attempt_index > 1:
            desktop_step.close_existing = False
            refresh_desktop_execution_plan(
                plan,
                desktop_step,
                diff_existing=True,
                logger=logger,
                queries=queries,
                preserved_hwnds=preserved_hwnds,
            )
            if logger is not None:
                logger.log(
                    f"Retrying desktop {desktop_step.desktop_index}: "
                    f"attempt {attempt_index} of {_MAX_DESKTOP_RESTORE_ATTEMPTS}"
                )
                if desktop_step.satisfied_entry_names:
                    logger.log(f"Leaving already-placed entries alone: {sorted(desktop_step.satisfied_entry_names)}")

        with tracing.span(
            "restore.desktop_attempt",
            scope=RESTORE_SCOPE,
            **{"desktop.index": desktop_step.desktop_index, "restore.attempt": attempt_index},
        ) as attempt_span:
            pending_second_pass_entries = _execute_desktop_step(
                plan,
                desktop_step,
                preserved_hwnds=preserved_hwnds,
                logger=logger,
                queries=queries,
                commands=commands,
                fallback_state=fallback_state,
            )
            with tracing.span(
                "restore.verify_desktop", scope=RESTORE_SCOPE, **{"desktop.index": desktop_step.desktop_index}
            ):
                completion = _verify_desktop_completion(
                    plan,
                    desktop_step,
                    logger=logger,
                    queries=queries,
                    commands=commands,
                )
                desktop_complete = completion.is_complete
                completion_diff = completion.diff
                satisfied_entries = completion.satisfied_entry_names
            attempt_span.set(**{"desktop.complete": desktop_complete})
        # Carry forward for the next attempt's plan refresh: whatever is already in
        # its planned slot must not be relaunched or re-matched.
        desktop_step.satisfied_entry_names = satisfied_entries
        if desktop_complete:
            if logger is not None:
                logger.log(f"Desktop {desktop_step.desktop_index} completion check passed on attempt {attempt_index}")
            _raise_planned_windows_on_desktop(desktop_step, commands=commands, logger=logger)
            return []

        if logger is not None:
            logger.log(f"Desktop {desktop_step.desktop_index} completion check failed on attempt {attempt_index}")
            for line in completion_diff.splitlines()[1:]:
                logger.log(f"VERIFY {line}")

    if logger is not None:
        logger.log(
            f"Desktop {desktop_step.desktop_index} incomplete after "
            f"{_MAX_DESKTOP_RESTORE_ATTEMPTS} attempt(s); continuing"
        )
    _raise_planned_windows_on_desktop(desktop_step, commands=commands, logger=logger)
    return pending_second_pass_entries


def _raise_planned_windows_on_desktop(
    desktop_step: PlannedDesktopExecution,
    *,
    commands: WorkspaceCommands,
    logger: RestoreRunLogger | None,
) -> None:
    """Re-raise the desktop's planned windows so they sit above any orphan windows.

    Placement happens early; auto-started apps and auth popups can grab the
    foreground afterwards and cover the tiled layout. This runs as the final action
    on the desktop, lifting every window whose process belongs to a planned entry.
    """
    process_names = {
        entry.window_process_name
        for entry in desktop_step.desktop.launch
        if not entry.disabled and entry.window_process_name
    }
    if not process_names:
        return
    raised = commands.raise_windows_for_processes(process_names)
    if logger is not None:
        logger.log(f"Desktop {desktop_step.desktop_index} raised {raised} planned window(s) above orphan windows")


@dataclass(frozen=True, slots=True)
class DesktopCompletion:
    """How a desktop compares with its plan after a restore attempt.

    ``satisfied_entry_names`` names the entries already sitting in their planned
    slot so the next attempt can leave them alone instead of relaunching them.
    """

    is_complete: bool
    diff: str
    satisfied_entry_names: set[str]


def _verify_desktop_completion(
    plan: WorkspacePlan,
    desktop_step: PlannedDesktopExecution,
    *,
    logger: RestoreRunLogger | None,
    queries: WorkspaceQueries,
    commands: WorkspaceCommands,
) -> DesktopCompletion:
    """Check the desktop against its plan."""
    from desktop_manager.visualization import entries_matched_on_planned_screen, render_workspace_diff

    _ensure_desktop_anchor(
        desktop_step.desktop_index,
        entry_name=desktop_step.desktop.name,
        reason="desktop completion check",
        logger=logger,
        queries=queries,
        commands=commands,
        settle_seconds=min(plan.switch_delay_ms / 1000, 0.2),
    )
    actual_desktop = queries.current_desktop_status(desktop_name=desktop_step.desktop.name)
    scoped_plan = WorkspacePlan(
        version=plan.version,
        workspace=plan.workspace,
        personal_workspace=plan.personal_workspace,
        switch_delay_ms=plan.switch_delay_ms,
        desktops=[desktop_step.desktop],
        notes=plan.notes,
        aliases=plan.aliases,
    )
    diff = render_workspace_diff(scoped_plan, WorkspacePlan(desktops=[actual_desktop]))
    satisfied: set[str]
    try:
        satisfied = entries_matched_on_planned_screen(desktop_step.desktop, actual_desktop, plan.substitutions())
    except Exception as err:  # a matching quirk must not abort the restore
        if logger is not None:
            logger.exception(f"Could not determine satisfied entries on desktop {desktop_step.desktop_index}: {err}")
        satisfied = set()
    # Only planned entries that are absent (`missing:`) or in the wrong place
    # (`misplaced:`) are fixable by another attempt. `extra:`/`borrowed:` denote
    # orphan or foreign windows (Docker Desktop, stray browser tabs, an
    # already-open Slack) that a retry can never resolve, so they must not force
    # the desktop to burn all its attempts.
    is_complete = all(token not in diff for token in ("missing:", "misplaced:"))
    return DesktopCompletion(is_complete, diff, satisfied)


def _ensure_desktop_anchor(
    desktop_index: int,
    *,
    entry_name: str,
    reason: str,
    logger: RestoreRunLogger | None,
    queries: WorkspaceQueries,
    commands: WorkspaceCommands,
    settle_seconds: float,
) -> None:
    current_desktop = queries.get_current_desktop_number()
    if current_desktop == desktop_index:
        return

    confirmed = commands.ensure_current_desktop_number(desktop_index, settle_seconds=settle_seconds)
    if logger is None or current_desktop is None:
        return

    anchor_status = "confirmed" if confirmed else "best-effort only"
    logger.log(
        f"Desktop drift detected before {reason} for {entry_name}: "
        f"on desktop {current_desktop}, expected {desktop_index}; re-anchored ({anchor_status})"
    )


def _sibling_owned_hwnds(
    desktop_step: PlannedDesktopExecution,
    entry: LaunchEntry,
    *,
    logger: RestoreRunLogger | None,
) -> set[int]:
    """Windows that plainly belong to a sibling entry and must not be adopted.

    When a retry adopts an in-flight instance it cannot exclude the pre-existing
    windows of its own process -- the one it wants is among them. That opened a
    hole: on 2026-08-05 the `VS Code Workspace` entry, whose window genuinely never
    appeared, adopted the `prs-to-review.md` window belonging to its already-placed
    sibling and dragged it onto the wrong screen.

    A sibling's window is identified by that sibling's explicit ``window_title``, so
    only entries that declare one can reserve anything.
    """
    process_name = entry.window_process_name
    if not process_name:
        return set()
    sibling_titles = {
        sibling.window_title.strip().casefold()
        for sibling in desktop_step.desktop.launch
        if sibling.name != entry.name
        and not sibling.disabled
        and sibling.window_title
        and sibling.window_process_name
        and sibling.window_process_name.casefold() == process_name.casefold()
    }
    own_title = (entry.window_title or "").strip().casefold()
    sibling_titles.discard(own_title)
    if not sibling_titles:
        return set()
    try:
        from desktop_manager.win32.inventory import window_titles_for_process

        live_titles = window_titles_for_process(process_name)
    except WIN32_OPTIONAL_ERRORS:
        return set()
    reserved = {
        hwnd
        for hwnd, title in live_titles.items()
        if any(sibling_title in title.casefold() for sibling_title in sibling_titles)
    }
    if reserved and logger is not None:
        logger.log(f"Reserved sibling-owned window(s) {sorted(reserved)} against adoption by {entry.name}")
    return reserved


def _entry_process_is_running(entry: LaunchEntry) -> bool:
    """True when this entry's target process exists, windowed or not.

    Used to decide whether a retry should adopt the instance the previous attempt
    started instead of launching another one.
    """
    if not entry.window_process_name:
        return False
    try:
        from desktop_manager.win32.inventory import is_process_running

        return is_process_running(entry.window_process_name)
    except WIN32_OPTIONAL_ERRORS:
        return False


def _wait_for_launched_entry_ready(
    entry: LaunchEntry,
    *,
    desktop_index: int,
    logger: RestoreRunLogger | None,
    excluded_hwnds: set[int] | None = None,
    queries: WorkspaceQueries,
) -> int | None:
    from desktop_manager.restore_timing import entry_title_takeover_timeout_seconds

    started_at = time.monotonic()
    if should_force_terminal_relaunch(entry):
        if logger is not None:
            logger.log(
                f"Waiting for launch readiness for {entry.name} on desktop {desktop_index} "
                f"excluded_hwnds={sorted(excluded_hwnds or set())}"
            )
        hwnd = queries.wait_for_window_for_entry(
            entry,
            timeout_seconds=10.0,
            target_desktop_number=desktop_index,
            excluded_hwnds=excluded_hwnds,
            allow_process_fallback_for_terminals=True,
            diagnostic_callback=_diagnostic_callback(logger),
        ).hwnd
        if hwnd is None:
            raise RuntimeError(f"Could not detect launched window for {entry.name}")
        if logger is not None:
            logger.log(f"Detected launched window {hwnd} for {entry.name} after {time.monotonic() - started_at:.2f}s")
        # A bare shell (command deferred at logon) runs nothing that would retitle the window.
        if entry.window_title and entry.command:
            changed_title = queries.wait_for_window_title_change(
                hwnd,
                entry.window_title,
                timeout_seconds=entry_title_takeover_timeout_seconds(entry),
                diagnostic_callback=_diagnostic_callback(logger),
            )
            if changed_title is None:
                raise RuntimeError(f"Timed out waiting for UI title takeover for {entry.name}")
            if logger is not None:
                logger.log(f"Confirmed UI title takeover for {entry.name}: {changed_title!r}")
        return hwnd
    delay_seconds = max(entry.delay_ms, 0) / 1000
    if delay_seconds > 0:
        time.sleep(delay_seconds)
    return None


def _log_post_entry_desktop_state(
    entry: LaunchEntry,
    desktop: DesktopPlan,
    *,
    logger: RestoreRunLogger | None,
    plan: WorkspacePlan | None,
    queries: WorkspaceQueries,
) -> None:
    if logger is None:
        return
    actual_desktop = queries.current_desktop_status(desktop_name=desktop.name)
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


def _diagnostic_callback(logger: RestoreRunLogger | None) -> Callable[[str], None] | None:
    if logger is None:
        return None

    def _callback(message: str) -> None:
        logger.log(message)

    return _callback
