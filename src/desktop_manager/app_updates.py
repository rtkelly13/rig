"""Detection and settling of third-party app self-updates that race the restore.

Warp updates itself with a silent Inno Setup installer staged in ``%TEMP%``. The
installer does not run on download: it waits on Warp's single-instance mutex
(``Local\\WarpStable_SingleInstance``) for a running Warp to exit. At logon
nothing has launched Warp yet, so the update fires the moment the restore
launches its first Warp entry -- mid-run, with three desktops already placed.

Observed 2026-08-05 (``warp_update.log``):

* 09:23:36  restore launches the ``Personal Warp`` entry
* 09:23:47  10s window-wait expires -> ``FAILED placement Personal Warp``
* 09:23:48  installer starts, waits for the old Warp (pid 15916) to exit
* 09:24:05  binaries swapped, Warp relaunches under pid 39788
* 09:24:09  Warp restores *every* prior session window onto the active desktop

The three Work-desktop Warp windows placed minutes earlier belonged to the killed
process, so they vanished too. Net effect: every Warp session piled onto the last
desktop and four planned slots came up empty.

Settling the update *before* any desktop is processed turns that into an ordinary
restore: Warp is already on its new build, its restored windows are in the
reusable inventory, and the plan places them normally.
"""

from __future__ import annotations

import os
import re
import subprocess
import time
from collections.abc import Callable
from dataclasses import dataclass
from pathlib import Path
from typing import TYPE_CHECKING

from pydantic import StrictStr

from rig.typed_json import try_decode_json

if TYPE_CHECKING:
    from desktop_manager.models import RestoreRunLogger, WorkspacePlan

WARP_PROCESS_NAME = "warp.exe"

# Inno Setup stages the download as %TEMP%\.tmp<version>-WarpSetup.exe and runs it
# with /SILENT /update=1. Stale installers from applied updates are never cleaned
# up (three were present on 2026-08-05), so presence alone means nothing -- the
# version encoded in the filename has to be compared against the installed build.
_WARP_INSTALLER_GLOBS = ("*WarpSetup.exe", ".*WarpSetup.exe")
_WARP_INSTALLER_VERSION_PATTERN = re.compile(r"^\.?tmp(?P<version>.+?)-WarpSetup\.exe$", re.IGNORECASE)
_WARP_UPDATER_PROCESS_HINT = "warpsetup"

_UPDATE_LOG_ACTIVE_WINDOW_SECONDS = 90.0
_UPDATER_APPEAR_TIMEOUT_SECONDS = 45.0
_UPDATER_FINISH_TIMEOUT_SECONDS = 300.0
_WARP_RETURN_TIMEOUT_SECONDS = 90.0
_POLL_INTERVAL_SECONDS = 1.0

Logged = Callable[[str], None]

# Staged versions this process has already launched Warp for. A trigger that produced no
# updater is a trigger that will not work on a retry either -- without this, a restore
# that re-checks per desktop would open one window per check.
_TRIGGERED_VERSIONS: set[str] = set()


@dataclass(frozen=True, slots=True)
class WarpUpdateState:
    """What the local filesystem and process table say about a Warp update."""

    installed_version: str | None
    staged_version: str | None
    staged_installer: Path | None
    updater_running: bool
    update_log_active: bool
    warp_running: bool = False

    @property
    def pending(self) -> bool:
        """A newer build is staged on disk and has not been applied yet."""
        if self.staged_version is None:
            return False
        return self.staged_version != self.installed_version

    @property
    def in_progress(self) -> bool:
        return self.updater_running or self.update_log_active

    @property
    def triggerable(self) -> bool:
        """Whether launching Warp could actually apply the staged build.

        The installer waits on ``Local\\WarpStable_SingleInstance``, so it only runs
        once no Warp holds that mutex. With Warp already up, launching ``warp.exe``
        just hands a new window to the running instance: the update cannot start, and
        the only lasting effect is a stray window. Checking this is what separates the
        logon case the settling was written for from a mid-session restore.
        """
        return self.pending and not self.warp_running

    def describe(self) -> str:
        return (
            f"installed={self.installed_version or '?'} staged={self.staged_version or 'none'} "
            f"updater_running={self.updater_running} log_active={self.update_log_active} "
            f"warp_running={self.warp_running} pending={self.pending}"
        )


@dataclass(frozen=True, slots=True)
class StagedInstaller:
    """A Warp installer waiting in TEMP, with the version its filename carries."""

    version: str
    path: Path


@dataclass(frozen=True, slots=True)
class _WarpVersionFile:
    warp_version: StrictStr | None = None


def warp_install_dir() -> Path:
    return Path(os.environ.get("LOCALAPPDATA", "")) / "Programs" / "Warp"


def warp_version_file() -> Path:
    return warp_install_dir() / "resources" / "bundled" / "metadata" / "version.json"


def warp_update_log_path() -> Path:
    return Path(os.environ.get("LOCALAPPDATA", "")) / "warp" / "Warp" / "data" / "logs" / "warp_update.log"


def installed_warp_version() -> str | None:
    """The build Warp currently has on disk, e.g. ``v0.2026.07.29.09.05.stable_02``."""
    version_file = warp_version_file()
    try:
        # Warp writes this file with a UTF-8 BOM.
        text = version_file.read_text(encoding="utf-8-sig")
    except (OSError, ValueError):
        return None
    meta = try_decode_json(_WarpVersionFile, text)
    return meta.warp_version if meta is not None and meta.warp_version else None


def staged_warp_installers() -> list[StagedInstaller]:
    """Every staged Warp installer, newest file first."""
    temp_dir = Path(os.environ.get("TEMP") or os.environ.get("TMP") or "")
    if not temp_dir.is_dir():
        return []
    found: dict[Path, str] = {}
    for pattern in _WARP_INSTALLER_GLOBS:
        try:
            candidates = list(temp_dir.glob(pattern))
        except OSError:
            continue
        for candidate in candidates:
            match = _WARP_INSTALLER_VERSION_PATTERN.match(candidate.name)
            if match is not None:
                found[candidate] = match.group("version")

    def sort_key(path: Path) -> float:
        try:
            return path.stat().st_mtime
        except OSError:
            return 0.0

    return [StagedInstaller(found[path], path) for path in sorted(found, key=sort_key, reverse=True)]


def warp_updater_running() -> bool:
    """True while the Inno Setup updater process is alive."""
    from desktop_manager.win32.inventory import running_process_names

    return any(_WARP_UPDATER_PROCESS_HINT in name for name in running_process_names())


def warp_update_log_active(*, within_seconds: float = _UPDATE_LOG_ACTIVE_WINDOW_SECONDS) -> bool:
    """True when the update log was written within the last ``within_seconds``.

    Catches the tail of an update whose installer process has already exited but
    whose relaunched Warp has not settled yet.
    """
    try:
        modified_at = warp_update_log_path().stat().st_mtime
    except OSError:
        return False
    return (time.time() - modified_at) <= within_seconds


def warp_running() -> bool:
    """True while any Warp holds the single-instance mutex the installer waits on."""
    from desktop_manager.win32.inventory import running_process_names

    return any(name.casefold() == WARP_PROCESS_NAME for name in running_process_names())


def warp_update_state() -> WarpUpdateState:
    staged = staged_warp_installers()
    newest = staged[0] if staged else None
    return WarpUpdateState(
        installed_version=installed_warp_version(),
        staged_version=newest.version if newest else None,
        staged_installer=newest.path if newest else None,
        updater_running=warp_updater_running(),
        update_log_active=warp_update_log_active(),
        warp_running=warp_running(),
    )


def wait_for_warp_updater_to_finish(
    *,
    timeout_seconds: float = _UPDATER_FINISH_TIMEOUT_SECONDS,
    log: Logged | None = None,
) -> bool:
    """Block until no Warp updater process remains. True if it finished in time."""
    deadline = time.monotonic() + timeout_seconds
    while time.monotonic() < deadline:
        if not warp_updater_running():
            if log is not None:
                log("Warp updater has exited")
            return True
        time.sleep(_POLL_INTERVAL_SECONDS)
    if log is not None:
        log(f"Warp updater still running after {timeout_seconds:.0f}s; continuing anyway")
    return False


def _wait_for_warp_window(*, timeout_seconds: float, log: Logged | None = None) -> bool:
    from desktop_manager.win32.inventory import collect_windows_matching_process

    deadline = time.monotonic() + timeout_seconds
    while time.monotonic() < deadline:
        if collect_windows_matching_process(WARP_PROCESS_NAME, current_desktop_only=False):
            return True
        time.sleep(_POLL_INTERVAL_SECONDS)
    if log is not None:
        log(f"Warp had no window {timeout_seconds:.0f}s after the update; continuing anyway")
    return False


def settle_warp_update(*, log: Logged | None = None, allow_trigger: bool = True) -> bool:
    """Drive any Warp update to completion before the restore places windows.

    Returns True when an update was in progress or was applied, meaning the
    caller should re-read live window state before matching entries to windows.
    """
    state = warp_update_state()
    if log is not None:
        log(f"Warp update check: {state.describe()}")

    if state.in_progress:
        if log is not None:
            log("Warp update already in progress; waiting for it to finish before restoring")
        wait_for_warp_updater_to_finish(log=log)
        _wait_for_warp_window(timeout_seconds=_WARP_RETURN_TIMEOUT_SECONDS, log=log)
        return True

    if not state.pending:
        return False

    if not allow_trigger:
        if log is not None:
            log(f"Warp update {state.staged_version} is pending but triggering is disabled")
        return False

    # Warp is already up, so it holds the single-instance mutex the installer blocks on.
    # Launching warp.exe here cannot apply anything -- it just hands a window to the
    # running instance and then the updater wait times out. Left unchecked this fires on
    # every restore that runs mid-session, each one leaking a window: 42 of them
    # accumulated across the test runs of 2026-08-09, spaced at exactly the 45s updater
    # timeout. The update stays staged and gets applied at the next logon instead.
    if not state.triggerable:
        if log is not None:
            log(
                f"Warp update {state.staged_version} is staged but Warp is already running; "
                "the installer cannot run while Warp holds its single-instance mutex, so "
                "leaving it staged for the next logon"
            )
        return False

    staged_version = state.staged_version
    if staged_version is not None and staged_version in _TRIGGERED_VERSIONS:
        if log is not None:
            log(f"Warp update {staged_version} was already triggered this run; not launching Warp again")
        return False

    # The installer only runs once something launches warp.exe. Doing that here,
    # before any desktop is placed, keeps the exit/swap/relaunch cycle out of the
    # middle of the restore -- otherwise Warp's session restore dumps every window
    # onto whichever desktop happens to be active at that moment.
    if log is not None:
        log(
            f"Warp update {state.staged_version} is staged (installed {state.installed_version}); "
            "launching Warp up front to apply it before restoring"
        )
    if staged_version is not None:
        _TRIGGERED_VERSIONS.add(staged_version)
    if not _trigger_warp_launch(log=log):
        return False

    if not _wait_for_warp_updater_to_start(log=log):
        # No updater appeared: either the staged build was already applied under a
        # different version string, or Warp declined to update this run. Either
        # way Warp is now running and the restore can reuse its window.
        if log is not None:
            log("No Warp updater appeared; treating Warp as already up to date")
        return True

    wait_for_warp_updater_to_finish(log=log)
    _wait_for_warp_window(timeout_seconds=_WARP_RETURN_TIMEOUT_SECONDS, log=log)
    if log is not None:
        log(f"Warp update settled; installed version is now {installed_warp_version() or '?'}")
    return True


def _trigger_warp_launch(*, log: Logged | None = None) -> bool:
    warp_exe = warp_install_dir() / WARP_PROCESS_NAME
    if not warp_exe.is_file():
        if log is not None:
            log(f"Cannot trigger Warp update: {warp_exe} not found")
        return False
    try:
        subprocess.Popen(
            [str(warp_exe)],
            stdin=subprocess.DEVNULL,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
        )
    except OSError as err:
        if log is not None:
            log(f"Cannot trigger Warp update: {err}")
        return False
    return True


def _wait_for_warp_updater_to_start(*, log: Logged | None = None) -> bool:
    deadline = time.monotonic() + _UPDATER_APPEAR_TIMEOUT_SECONDS
    while time.monotonic() < deadline:
        if warp_updater_running() or warp_update_log_active(within_seconds=10.0):
            if log is not None:
                log("Warp updater started")
            return True
        time.sleep(_POLL_INTERVAL_SECONDS)
    return False


def plan_launches_warp(plan: WorkspacePlan) -> bool:
    """Whether any entry the plan will launch is Warp; a switched-off feature's entries are disabled."""
    return any(
        not entry.disabled and entry_targets_warp(entry.window_process_name, entry.path)
        for desktop in plan.desktops
        for entry in desktop.launch
    )


def entry_targets_warp(process_name: str | None, path: str | None) -> bool:
    if process_name and process_name.casefold() == WARP_PROCESS_NAME:
        return True
    return bool(path and path.casefold().endswith(WARP_PROCESS_NAME))


def settle_pending_app_updates(plan: WorkspacePlan, *, logger: RestoreRunLogger | None) -> bool:
    """Apply or wait out pending self-updates for the apps the plan launches."""
    from desktop_manager.orca_update import plan_launches_orca, settle_orca_update

    def log(message: str) -> None:
        if logger is not None:
            logger.log(message)

    settled = False
    # The check alone costs seconds and an update would launch Warp, so a plan without it skips both.
    if not plan_launches_warp(plan):
        log("Warp update check skipped: the plan launches no Warp window")
    else:
        try:
            settled = settle_warp_update(log=log)
        except Exception as err:  # pragma: no cover - defensive: never block a restore
            if logger is not None:
                logger.exception(f"Warp update check failed: {err}; continuing with the restore")

    # The Orca installer kills every Orca.exe and relaunches at most one profile, so it
    # runs only while no Orca is up; the restore then launches every profile itself.
    if not plan_launches_orca(plan):
        log("Orca update check skipped: the plan launches no Orca window")
    else:
        try:
            settled = settle_orca_update(log=log) or settled
        except Exception as err:  # pragma: no cover - defensive: never block a restore
            if logger is not None:
                logger.exception(f"Orca update check failed: {err}; continuing with the restore")
    return settled
