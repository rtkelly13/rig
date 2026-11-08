from __future__ import annotations

from typing import TYPE_CHECKING

from desktop_manager.desktop_port import DesktopPort, Win32DesktopPort
from desktop_manager.models import LaunchEntry, RestoreRunLogger, WorkspacePlan
from desktop_manager.restore_models import PlannedDesktopExecution, RestoreExecutionPlan
from desktop_manager.restore_planner import resolve_target_desktop_index
from desktop_manager.restore_timing import SLOW_DESKTOP_SECONDS, StepTimer
from rig import tracing

if TYPE_CHECKING:
    from desktop_manager.restore_fallbacks import RestoreFailureFallbacks
from desktop_manager.handlers.restore_steps import (
    RESTORE_SCOPE,
    execute_desktop_with_retries,
    refresh_desktop_execution_plan,
)


def restore_workspace_with_options(
    plan: WorkspacePlan,
    *,
    dry_run: bool = False,
    close_existing: bool = False,
    diff_existing: bool = False,
    logger: RestoreRunLogger | None = None,
    target_desktop: str | None = None,
    port: DesktopPort | None = None,
) -> list[str]:
    with tracing.span(
        "workspace.restore",
        scope=RESTORE_SCOPE,
        **{
            "restore.dry_run": dry_run,
            "restore.close_existing": close_existing,
            "restore.diff_existing": diff_existing,
            "restore.target_desktop": target_desktop or "all",
            "restore.desktop_count": len(plan.desktops),
            "restore.retired_desktops": list(plan.retired_desktop_names),
        },
    ) as restore_span:
        actions = _restore_workspace(
            plan,
            dry_run=dry_run,
            close_existing=close_existing,
            diff_existing=diff_existing,
            logger=logger,
            target_desktop=target_desktop,
            port=port or Win32DesktopPort(),
        )
        restore_span.set(**{"restore.action_count": len(actions)})
        return actions


def _restore_workspace(
    plan: WorkspacePlan,
    *,
    dry_run: bool,
    close_existing: bool,
    diff_existing: bool,
    logger: RestoreRunLogger | None,
    target_desktop: str | None,
    port: DesktopPort,
) -> list[str]:
    from desktop_manager.restore_fallbacks import RestoreFailureFallbacks

    if not plan.desktops:
        return ["No desktops configured."]

    steps = StepTimer(logger, RESTORE_SCOPE)

    if logger is not None:
        desktop_label = target_desktop or "all"
        logger.log(
            f"Starting restore dry_run={dry_run} close_existing={close_existing} diff_existing={diff_existing} "
            f"desktops={desktop_label} workspace={plan.workspace}"
        )
        logger.log(f"Trace spans: {tracing.trace_file()}")

    preserved_hwnds: set[int] = set()
    if diff_existing or close_existing:
        preserved_hwnds = port.current_window_handles_to_preserve()
    if logger is not None and preserved_hwnds:
        logger.log(f"Preserving {len(preserved_hwnds)} invoking window(s) from reuse/close handling")

    try:
        target_desktop_index = resolve_target_desktop_index(plan, target_desktop)
    except ValueError as err:
        return [str(err)]

    # Settle self-updating apps before inventorying live windows. A Warp update
    # applied mid-restore kills the windows already placed and dumps Warp's
    # restored session onto whatever desktop is active, so it has to happen (and
    # be waited out) before any entry is matched to a window.
    if not dry_run:
        with steps.step("restore.settle_app_updates"):
            port.settle_app_updates(plan, logger=logger)

    # Set the desktops up before planning which live windows to reuse: retiring a
    # leftover renumbers the desktops after it, so planning first would match each
    # step against the windows of the desktop that used to sit at its number.
    starting_desktop_index: int | None = None
    if not dry_run:
        starting_desktop_index = _prepare_desktops(plan, port=port, logger=logger, steps=steps)

    planned_desktops = [
        PlannedDesktopExecution(
            desktop=desktop,
            desktop_index=desktop_index,
            close_existing=close_existing,
        )
        for desktop_index, desktop in enumerate(plan.desktops, start=1)
        if target_desktop_index is None or desktop_index == target_desktop_index
    ]

    execution_plan = RestoreExecutionPlan(
        desktops=planned_desktops,
        target_desktop=target_desktop,
        target_desktop_index=target_desktop_index,
    )

    with steps.step("restore.plan"):
        for desktop_step in execution_plan.desktops:
            refresh_desktop_execution_plan(
                plan,
                desktop_step,
                diff_existing=diff_existing,
                logger=logger if not dry_run else None,
                port=port,
                preserved_hwnds=preserved_hwnds,
            )

    actions = _retire_actions(plan) + execution_plan.render_actions()
    if dry_run:
        return actions

    # Kick off CLI self-updates in parallel up front so their combined runtime
    # overlaps the rest of the restore instead of blocking each terminal launch.
    with steps.step("restore.start_terminal_command_upgrades"):
        port.start_terminal_command_upgrades(plan)
    if logger is not None:
        logger.log("Started terminal command upgrades in the background")

    fallback_state = RestoreFailureFallbacks()
    pending_second_pass_entries: dict[int, list[LaunchEntry]] = {}

    if execution_plan.target_desktop_index is not None:
        port.ensure_current_desktop_number(
            execution_plan.target_desktop_index,
            settle_seconds=plan.switch_delay_ms / 1000,
        )
        if logger is not None:
            logger.log(f"Navigated directly to desktop {execution_plan.target_desktop_index}")
    else:
        port.switch_leftmost(len(plan.desktops) + 2)
        if logger is not None:
            logger.log("Switched to the leftmost desktop")
        port.sleep(plan.switch_delay_ms / 1000)
        port.ensure_current_desktop_number(1, settle_seconds=plan.switch_delay_ms / 1000)
        if logger is not None:
            logger.log("Confirmed desktop 1 is active before restore")

    previous_desktop_index: int | None = None
    for desktop_step in execution_plan.desktops:
        with steps.step(
            "restore.desktop",
            {"desktop.index": desktop_step.desktop_index, "desktop.name": desktop_step.desktop.name},
            label=f"desktop {desktop_step.desktop_index} ({desktop_step.desktop.name})",
            slow_after=SLOW_DESKTOP_SECONDS,
        ) as desktop_span:
            desktop_pending = _restore_one_desktop(
                plan,
                desktop_step,
                previous_desktop_index=previous_desktop_index,
                target_desktop_index=execution_plan.target_desktop_index,
                preserved_hwnds=preserved_hwnds,
                logger=logger,
                port=port,
                fallback_state=fallback_state,
            )
            desktop_span.set(**{"desktop.pending_second_pass": len(desktop_pending)})
        if desktop_pending:
            pending_second_pass_entries[desktop_step.desktop_index] = desktop_pending
        previous_desktop_index = desktop_step.desktop_index

    if fallback_state.has_missing_paths():
        with steps.step("restore.missing_path_fallbacks"):
            actions.extend(port.open_missing_path_fallbacks(fallback_state, logger=logger))

    if pending_second_pass_entries:
        with steps.step(
            "restore.second_pass",
            {"entry.count": sum(len(entries) for entries in pending_second_pass_entries.values())},
        ):
            actions.extend(
                _run_second_pass(
                    plan,
                    pending_second_pass_entries,
                    target_desktop_index=execution_plan.target_desktop_index,
                    logger=logger,
                    port=port,
                )
            )
    elif logger is not None:
        logger.log("First pass fully successful; skipped second-pass placement")

    if starting_desktop_index is not None:
        with steps.step("restore.return_to_start", {"desktop.index": starting_desktop_index}) as return_span:
            returned = port.ensure_current_desktop_number(
                starting_desktop_index,
                settle_seconds=plan.switch_delay_ms / 1000,
            )
            return_span.set(**{"desktop.confirmed": returned})
        actions.append(f"Return to starting desktop {starting_desktop_index}")
        if logger is not None:
            logger.log(
                f"Returned to starting desktop {starting_desktop_index} "
                f"({'confirmed' if returned else 'best-effort only'})"
            )

    steps.log_summary()
    if logger is not None:
        logger.log("Restore finished successfully")

    return actions


def _prepare_desktops(
    plan: WorkspacePlan,
    *,
    port: DesktopPort,
    logger: RestoreRunLogger | None,
    steps: StepTimer,
) -> int | None:
    """Retire switched-off desktops, top the count up, and return the starting desktop."""
    port.ensure_windows()
    if logger is not None:
        logger.log("Ensured Windows environment is available")

    starting_desktop_index = port.get_current_desktop_number()
    if logger is not None:
        logger.log(f"Captured starting desktop: {starting_desktop_index or '?'}")

    # Retire before topping up the count: a leftover can sit among the planned desktops,
    # and removing it there leaves a gap that ensure_desktop_count then fills.
    if plan.retired_desktop_names:
        with steps.step(
            "restore.retire_desktops", {"desktop.retire_candidates": list(plan.retired_desktop_names)}
        ) as retire_span:
            retired = port.retire_switched_off_desktops(len(plan.desktops), plan.retired_desktop_names)
            retire_span.set(**{"desktop.retired": retired})
        if logger is not None and retired:
            logger.log(f"Removed switched-off desktop(s): {', '.join(retired)}")
        # Removing desktops renumbers the ones after them, and starting on a removed one
        # moves us to the fallback, so re-read where "return to the starting desktop" aims.
        if retired:
            starting_desktop_index = port.get_current_desktop_number()

    with steps.step("restore.ensure_desktop_count", {"desktop.count": len(plan.desktops)}):
        port.ensure_desktop_count(len(plan.desktops), plan.switch_delay_ms / 1000)
    if logger is not None:
        logger.log(f"Ensured desktop count is at least {len(plan.desktops)}")

    return starting_desktop_index


def _retire_actions(plan: WorkspacePlan) -> list[str]:
    """The dry-run line for desktops a switched-off feature retires."""
    if not plan.retired_desktop_names:
        return []
    names = ", ".join(plan.retired_desktop_names)
    return [f"Remove switched-off desktop(s), if present: {names}"]


def _restore_one_desktop(
    plan: WorkspacePlan,
    desktop_step: PlannedDesktopExecution,
    *,
    previous_desktop_index: int | None,
    target_desktop_index: int | None,
    preserved_hwnds: set[int],
    logger: RestoreRunLogger | None,
    port: DesktopPort,
    fallback_state: RestoreFailureFallbacks,
) -> list[LaunchEntry]:
    _activate_desktop(
        plan,
        desktop_step,
        previous_desktop_index=previous_desktop_index,
        target_desktop_index=target_desktop_index,
        logger=logger,
        port=port,
    )
    current_desktop = port.current_desktop_status(desktop_name=desktop_step.desktop.name)
    if logger is not None:
        logger.log(
            f"Captured desktop {desktop_step.desktop_index} status before restore: "
            f"{len(current_desktop.captured_windows)} visible window(s)"
        )
    return execute_desktop_with_retries(
        plan,
        desktop_step,
        preserved_hwnds=preserved_hwnds,
        logger=logger,
        port=port,
        fallback_state=fallback_state,
    )


def _run_second_pass(
    plan: WorkspacePlan,
    pending_second_pass_entries: dict[int, list[LaunchEntry]],
    *,
    target_desktop_index: int | None,
    logger: RestoreRunLogger | None,
    port: DesktopPort,
) -> list[str]:
    if target_desktop_index is not None:
        port.ensure_current_desktop_number(
            target_desktop_index,
            settle_seconds=plan.switch_delay_ms / 1000,
        )
        if logger is not None:
            logger.log(f"Navigated to desktop {target_desktop_index} for second-pass placement")
    else:
        port.switch_leftmost(len(plan.desktops) + 2)
        if logger is not None:
            logger.log("Returned to the leftmost desktop")
    if logger is not None:
        logger.log(
            f"Second pass needed for {sum(len(entries) for entries in pending_second_pass_entries.values())} entry(s)"
        )
    port.sleep(plan.switch_delay_ms / 1000)
    return port.apply_second_pass_placements(
        plan,
        logger=logger,
        entries_by_desktop=pending_second_pass_entries,
    )


def _activate_desktop(
    plan: WorkspacePlan,
    desktop_step: PlannedDesktopExecution,
    *,
    previous_desktop_index: int | None,
    target_desktop_index: int | None,
    logger: RestoreRunLogger | None,
    port: DesktopPort,
) -> None:
    if (
        target_desktop_index is None
        and previous_desktop_index is not None
        and desktop_step.desktop_index > previous_desktop_index
    ):
        port.move_right(desktop_step.desktop_index - previous_desktop_index)
        if logger is not None:
            logger.log(f"Moved to desktop {desktop_step.desktop_index}")
        port.sleep(plan.switch_delay_ms / 1000)
    confirmed = port.ensure_current_desktop_number(
        desktop_step.desktop_index,
        settle_seconds=plan.switch_delay_ms / 1000,
    )
    if logger is not None:
        activation_status = "confirmed" if confirmed else "best-effort only"
        logger.log(f"Desktop {desktop_step.desktop_index} activation {activation_status} before restore")
    with tracing.span(
        "restore.rename_desktop",
        scope=RESTORE_SCOPE,
        **{"desktop.index": desktop_step.desktop_index, "desktop.name": desktop_step.desktop.name},
    ) as rename_span:
        renamed = port.rename_desktop(desktop_step.desktop_index, desktop_step.desktop.name)
        rename_span.set(**{"desktop.renamed": renamed})
    if logger is not None and not renamed:
        logger.log(f"Desktop {desktop_step.desktop_index} could not be renamed to '{desktop_step.desktop.name}'")
