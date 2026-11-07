"""Restoring a whole workspace plan through the restore handler."""

from __future__ import annotations

from desktop_manager.models import RestoreRunLogger, WorkspacePlan


def restore_workspace_with_options(
    plan: WorkspacePlan,
    *,
    dry_run: bool = False,
    close_existing: bool = False,
    diff_existing: bool = False,
    logger: RestoreRunLogger | None = None,
    target_desktop: str | None = None,
) -> list[str]:
    from desktop_manager.desktop_port import RestoreCompatibilityPort
    from desktop_manager.handlers.restore_handler import restore_workspace_with_options as run_restore_handler

    return run_restore_handler(
        plan,
        dry_run=dry_run,
        close_existing=close_existing,
        diff_existing=diff_existing,
        logger=logger,
        target_desktop=target_desktop,
        port=RestoreCompatibilityPort(),
    )
