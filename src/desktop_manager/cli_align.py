"""``workspace align``: put open plan windows back in place without launching or closing anything."""

from __future__ import annotations

import contextlib
import logging
import time
from collections.abc import Generator
from pathlib import Path

import typer

from desktop_manager.cli_common import config_option, console, console_safe_text
from rig.platforms import Platform, requires

ALIGN_LOG_FILE_NAME = "align.log"
# Some window moves failed (logged in align.log); everything else was applied.
EXIT_MOVES_FAILED = 3


@contextlib.contextmanager
def _align_logger(config_path: Path) -> Generator[logging.Logger]:
    from logging.handlers import RotatingFileHandler

    from desktop_manager import runtime
    from desktop_manager.plan import workspace_logs_dir

    # From a runtime copy, log beside Workspace Restore's logs, which outlive the copy.
    log_dir = runtime.workspace_logs_dir() if runtime.running_copy(__file__) else workspace_logs_dir(config_path)
    log_dir.mkdir(parents=True, exist_ok=True)
    logger = logging.getLogger("desktop_manager.align")
    logger.setLevel(logging.INFO)
    logger.propagate = False
    handler = RotatingFileHandler(log_dir / ALIGN_LOG_FILE_NAME, maxBytes=1_000_000, backupCount=3, encoding="utf-8")
    handler.setFormatter(logging.Formatter("%(asctime)s %(message)s"))
    logger.addHandler(handler)
    try:
        yield logger
    finally:
        logger.removeHandler(handler)
        handler.close()


@requires(Platform.WINDOWS)
def align(
    config: Path = typer.Option(
        None,
        "--config",
        help="Path to the workspace JSON plan. Defaults to the platform default.",
    ),
    desktop: str | None = typer.Option(
        None,
        "--desktop",
        help="Only align the windows of one plan desktop by name (e.g. Personal, Work, Admin).",
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
    dry_run: bool = typer.Option(False, "--dry-run", help="Report what would move without moving anything."),
    verbose: bool = typer.Option(False, "--verbose", "-v", help="List every plan entry's outcome."),
    reconcile_desktops: bool = typer.Option(
        True,
        "--reconcile-desktops/--no-reconcile-desktops",
        help="Ensure correct virtual desktops exist and have the right names like IaC.",
    ),
) -> None:
    """Move open plan windows back to their planned desktop and slot. Never launches or closes windows.

    Prints one summary line. Exit 0 when it ran (missing, minimized, maximized and
    foreground windows are reported, not errors) or when a restore holds the lock;
    1 on a bad config, feature or desktop name; 3 when some window moves failed.
    """
    from desktop_manager.align import AlignOutcome, run_alignment, summary_line
    from desktop_manager.display_guard import RESTORE_LOCK_FILE_NAME, hold_lock, lock_dir
    from desktop_manager.placement import monitor_shortfall
    from desktop_manager.plan import load_plan, load_plan_context
    from desktop_manager.win32.align_ports import Win32AlignPorts
    from desktop_manager.windows_workspace import UnknownFeatureError

    config_path = config or config_option()
    if not config_path.exists():
        console.print(f"[red]Config not found:[/red] {config_path}")
        raise typer.Exit(code=1)

    with _align_logger(config_path) as logger:

        def report(message: str, *, echo: bool) -> None:
            logger.info(message)
            if echo:
                console.print(console_safe_text(message), highlight=False)

        try:
            shortfall = monitor_shortfall(
                load_plan_context(config_path, enable_features=enable_feature, disable_features=disable_feature)
            )
            plan = load_plan(config_path, enable_features=enable_feature, disable_features=disable_feature)
        except (UnknownFeatureError, ValueError) as error:
            report(f"align: error: {error}", echo=True)
            raise typer.Exit(code=1) from error
        prefix = "align (dry run)" if dry_run else "align"
        if shortfall is not None:
            report(f"{prefix}: skipped (plan needs {shortfall.required} screens, {shortfall.live} detected)", echo=True)
            return

        # The same lock restore holds: the display guard pauses while align moves
        # windows, and align never moves windows a running restore is placing.
        # A short retry rides out the guard's own instant probe of the lock.
        with hold_lock(lock_dir() / RESTORE_LOCK_FILE_NAME, timeout_seconds=0.5, sleep=time.sleep) as acquired:
            if not acquired:
                report(f"{prefix}: skipped (restore in progress)", echo=True)
                return
            try:
                result = run_alignment(
                    plan,
                    Win32AlignPorts(),
                    target_desktop=desktop,
                    dry_run=dry_run,
                    reconcile_desktops=reconcile_desktops,
                    log=logger.info,
                )
            except ValueError as error:
                report(f"align: error: {error}", echo=True)
                raise typer.Exit(code=1) from error

        if result.desktop_reconciliation is not None:
            rec = result.desktop_reconciliation
            for r in rec.renamed:
                report(f"  desktop {r.index}: renamed '{r.current_name}' -> '{r.target_name}'", echo=verbose)
            for c in rec.created:
                report(f"  desktop {c.index}: created '{c.name}'", echo=verbose)
            for ret in rec.retired:
                report(f"  desktop retired: '{ret}'", echo=verbose)

        for decision in result.decisions:
            if decision.outcome is AlignOutcome.IN_PLACE:
                if verbose:
                    console.print(console_safe_text(f"  {decision.describe()}"), highlight=False)
                continue
            report(f"  {decision.describe()}", echo=verbose)
        report(summary_line(result, dry_run=dry_run), echo=True)

    if result.count(AlignOutcome.FAILED):
        raise typer.Exit(code=EXIT_MOVES_FAILED)
