"""Apply staged Orca updates on rig's terms, so both Orca profiles come back afterwards.

Two Orca profiles run on the Work desktop: ``Work Orca`` on the default profile and
``Personal Orca`` on ``--user-data-dir=%APPDATA%\\orca-personal``. Orca's in-app
"restart to update" runs the electron-builder NSIS installer, which kills *every*
``Orca.exe`` but relaunches only the profile that asked for the update. Observed
2026-11-05: 1.4.250 was applied from the Work profile at 10:43, Work relaunched at
10:44, and Personal stayed closed until it was launched by hand.

electron-updater stages the download in ``%LOCALAPPDATA%\\orca-updater\\pending``
(``update-info.json`` plus ``orca-windows-setup.exe``) and never clears it after an
install, so presence alone means nothing: the installer's file version has to be
compared against the installed build.

rig takes the update over in two places:

* At restore, before any window is inventoried: with no Orca running, the staged
  installer runs silently and the restore then launches both profiles as usual.
* ``desktop-manager workspace update-orca``: closes every Orca window gracefully,
  waits out any installer Orca started itself on quit, installs if still behind,
  and relaunches every enabled Orca entry in its planned placement.

So Orca's own update prompts can simply be dismissed.
"""

from __future__ import annotations

import base64
import binascii
import hashlib
import os
import time
from collections.abc import Callable
from dataclasses import dataclass, field
from pathlib import Path
from typing import TYPE_CHECKING, Annotated

from pydantic import Field, StrictBool, StrictStr

from rig import proc
from rig.typed_json import try_decode_json

if TYPE_CHECKING:
    from desktop_manager.models import RestoreRunLogger, WorkspacePlan

ORCA_PROCESS_NAME = "orca.exe"
ORCA_DISPLAY_NAME = "Orca"
_INSTALLER_PROCESS_HINT = "orca-windows-setup"

_INSTALL_TIMEOUT_SECONDS = 300.0
_INSTALLER_FINISH_TIMEOUT_SECONDS = 300.0
# Orca persists its sessions while quitting, so the close wait is generous before
# anything is force-killed (see the tab-loss-at-shutdown issue, PR #581).
_CLOSE_TIMEOUT_SECONDS = 60.0
# electron-updater's autoInstallOnAppQuit may start the installer as Orca exits.
_SELF_INSTALL_APPEAR_SECONDS = 15.0
_EXIT_TIMEOUT_SECONDS = 10.0
_POLL_INTERVAL_SECONDS = 1.0

Logged = Callable[[str], None]

# Staged versions this process has already run the installer for, so a failed install
# is not retried on every desktop of the same restore.
_ATTEMPTED_VERSIONS: set[str] = set()


@dataclass(frozen=True, slots=True)
class _UpdateInfo:
    file_name: Annotated[StrictStr, Field(alias="fileName")]
    sha512: StrictStr | None = None
    admin_required: Annotated[StrictBool, Field(alias="isAdminRightsRequired")] = False


@dataclass(frozen=True, slots=True)
class StagedOrcaUpdate:
    """The installer electron-updater left in its pending folder."""

    version: str | None
    installer: Path
    sha512: str | None
    admin_required: bool = False


@dataclass(frozen=True, slots=True)
class OrcaUpdateState:
    """What the disk and process table say about an Orca update."""

    installed_version: str | None
    staged: StagedOrcaUpdate | None
    orca_running: bool = False
    installer_running: bool = False

    @property
    def staged_version(self) -> str | None:
        return self.staged.version if self.staged is not None else None

    @property
    def pending(self) -> bool:
        """A strictly newer build is staged. With Orca not installed there is nothing to update."""
        installed = version_key(self.installed_version)
        staged = version_key(self.staged_version)
        if installed is None or staged is None:
            return False
        return staged > installed

    def describe(self) -> str:
        return (
            f"installed={self.installed_version or '?'} staged={self.staged_version or 'none'} "
            f"orca_running={self.orca_running} installer_running={self.installer_running} pending={self.pending}"
        )


def version_key(version: str | None) -> tuple[int, ...] | None:
    """``1.4.250`` and ``1.4.250.0`` compare equal; anything non-numeric gives None."""
    if not version:
        return None
    parts: list[int] = []
    for part in version.strip().lstrip("vV").split("."):
        if not part.isdigit():
            return None
        parts.append(int(part))
    while len(parts) > 1 and parts[-1] == 0:
        parts.pop()
    return tuple(parts)


def orca_install_dir() -> Path:
    return Path(os.environ.get("LOCALAPPDATA", "")) / "Programs" / "orca"


def orca_pending_dir() -> Path:
    return Path(os.environ.get("LOCALAPPDATA", "")) / "orca-updater" / "pending"


def installed_orca_version() -> str | None:
    """The Uninstall entry's ``DisplayVersion``, falling back to ``Orca.exe``'s file version."""
    from desktop_manager.win32.file_version import file_version, uninstall_display_version

    return uninstall_display_version(ORCA_DISPLAY_NAME) or file_version(orca_install_dir() / "Orca.exe")


def staged_orca_update() -> StagedOrcaUpdate | None:
    """The pending installer, versioned by its own file version (``update-info.json`` carries none)."""
    from desktop_manager.win32.file_version import file_version

    pending = orca_pending_dir()
    try:
        text = (pending / "update-info.json").read_text(encoding="utf-8")
    except (OSError, ValueError):
        return None
    info = try_decode_json(_UpdateInfo, text)
    if info is None:
        return None
    installer = pending / Path(info.file_name).name
    if not installer.is_file():
        return None
    return StagedOrcaUpdate(
        version=file_version(installer),
        installer=installer,
        sha512=info.sha512,
        admin_required=info.admin_required,
    )


def installer_matches_sha512(installer: Path, expected: str | None) -> bool:
    """Check the installer against the base64 SHA-512 electron-updater recorded for it."""
    if not expected:
        return False
    try:
        wanted = base64.b64decode(expected, validate=True)
    except (binascii.Error, ValueError):
        return False
    digest = hashlib.sha512()
    try:
        with installer.open("rb") as stream:
            for chunk in iter(lambda: stream.read(1024 * 1024), b""):
                digest.update(chunk)
    except OSError:
        return False
    return digest.digest() == wanted


def orca_running() -> bool:
    from desktop_manager.win32.inventory import running_process_names

    return ORCA_PROCESS_NAME in running_process_names()


def orca_installer_running() -> bool:
    from desktop_manager.win32.inventory import running_process_names

    return any(_INSTALLER_PROCESS_HINT in name for name in running_process_names())


def orca_update_state() -> OrcaUpdateState:
    return OrcaUpdateState(
        installed_version=installed_orca_version(),
        staged=staged_orca_update(),
        orca_running=orca_running(),
        installer_running=orca_installer_running(),
    )


def wait_for_orca_installer(
    *,
    timeout_seconds: float = _INSTALLER_FINISH_TIMEOUT_SECONDS,
    log: Logged | None = None,
) -> bool:
    """Block until no Orca installer process remains. True if it finished in time."""
    deadline = time.monotonic() + timeout_seconds
    while time.monotonic() < deadline:
        if not orca_installer_running():
            return True
        time.sleep(_POLL_INTERVAL_SECONDS)
    _emit(log, f"Orca installer still running after {timeout_seconds:.0f}s; continuing anyway")
    return False


def run_orca_installer(staged: StagedOrcaUpdate, *, log: Logged | None = None) -> bool:
    """Verify and silently run the staged installer. It does not relaunch Orca; the caller does.

    ``--updated /S`` is what electron-updater itself passes, minus ``--force-run``.
    """
    if staged.admin_required:
        _emit(log, "Orca installer needs admin rights; not running it unattended")
        return False
    if not installer_matches_sha512(staged.installer, staged.sha512):
        _emit(log, f"Orca installer {staged.installer} does not match its recorded SHA-512; not running it")
        return False
    _emit(log, f"Running Orca installer {staged.installer} ({staged.version or '?'}) silently")
    ran = proc.run(
        [str(staged.installer), "--updated", "/S"],
        timeout=_INSTALL_TIMEOUT_SECONDS,
        scope="desktop_manager.orca_update",
    )
    if not ran.ok:
        _emit(log, f"Orca installer exited {ran.code}: {(ran.err or ran.out).strip()}")
        return False
    return True


def settle_orca_update(*, log: Logged | None = None) -> bool:
    """Restore-time settling: apply a staged update while no Orca is running.

    Returns True when an install ran or was waited out, so the caller re-reads live state.
    """
    state = orca_update_state()
    _emit(log, f"Orca update check: {state.describe()}")

    if state.installer_running:
        _emit(log, "Orca installer already running; waiting for it before restoring")
        wait_for_orca_installer(log=log)
        return True

    if not state.pending or state.staged is None:
        return False

    if state.orca_running:
        _emit(
            log,
            f"Orca update {state.staged_version} is staged but Orca is running; leaving it staged. "
            "Run `desktop-manager workspace update-orca` to apply it and relaunch both profiles",
        )
        return False

    version = state.staged_version or ""
    if version in _ATTEMPTED_VERSIONS:
        _emit(log, f"Orca update {version} was already attempted this run; not retrying")
        return False
    _ATTEMPTED_VERSIONS.add(version)

    if not run_orca_installer(state.staged, log=log):
        return False
    _emit(log, f"Orca update settled; installed version is now {installed_orca_version() or '?'}")
    return True


@dataclass(slots=True)
class OrcaUpdateResult:
    """What ``update_orca`` did, for the CLI to print and turn into an exit code."""

    actions: list[str] = field(default_factory=list[str])
    ok: bool = True


@dataclass(frozen=True, slots=True)
class _OrcaEntry:
    desktop: str
    entry: str


def update_orca(
    plan: WorkspacePlan,
    *,
    dry_run: bool = False,
    logger: RestoreRunLogger | None = None,
    close_timeout_seconds: float = _CLOSE_TIMEOUT_SECONDS,
) -> OrcaUpdateResult:
    """Close every Orca, install the staged update, and relaunch every enabled Orca entry."""

    def log(message: str) -> None:
        if logger is not None:
            _ = logger.log(message)

    result = OrcaUpdateResult()
    state = orca_update_state()
    log(f"Orca update check: {state.describe()}")

    # An installer already running is an update in flight: it restores at most one
    # profile, so once it finishes every profile still has to be relaunched.
    update_in_flight = state.installer_running
    if update_in_flight and not dry_run:
        result.actions.append("Orca installer already running; waiting for it to finish")
        if not wait_for_orca_installer(log=log):
            return _installer_still_running(result)
        state = orca_update_state()

    if not update_in_flight and (not state.pending or state.staged is None):
        result.actions.append(f"Orca is up to date (installed {state.installed_version or '?'}); nothing to do")
        return result

    entries = orca_entries(plan)
    labels = ", ".join(f"{item.desktop}/{item.entry}" for item in entries) or "none"
    if state.pending and state.staged is not None:
        result.actions.append(
            f"Orca update {state.installed_version or '?'} -> {state.staged_version}; entries to relaunch: {labels}"
        )
    else:
        result.actions.append(
            f"Orca installer finished (installed {state.installed_version or '?'}); entries to relaunch: {labels}"
        )
    if dry_run:
        result.actions.append(f"  - would close every Orca window (graceful, up to {close_timeout_seconds:.0f}s)")
        if state.staged is not None and state.pending:
            result.actions.append(f"  - would run {state.staged.installer} --updated /S")
        result.actions.extend(f"  - would relaunch `{item.entry}` on {item.desktop}" for item in entries)
        return result

    result.actions.append(f"  - {close_all_orca(timeout_seconds=close_timeout_seconds, log=log)}")

    # Orca may hand off to its own installer as it quits; let that finish rather than racing it.
    if _installer_appears(timeout_seconds=_SELF_INSTALL_APPEAR_SECONDS):
        result.actions.append("  - Orca started its own installer on quit; waiting for it")
        if not wait_for_orca_installer(log=log):
            return _installer_still_running(result)

    state = orca_update_state()
    if state.installer_running:
        return _installer_still_running(result)
    if state.pending and state.staged is not None:
        if run_orca_installer(state.staged, log=log):
            result.actions.append(f"  - installed {state.staged_version}")
        else:
            result.actions.append("  - installer did not complete; relaunching Orca on the current build")
            result.ok = False
    installed = installed_orca_version()
    log(f"Orca installed version is now {installed or '?'}")
    result.actions.append(f"  - installed version is now {installed or '?'}")

    for item in entries:
        result.actions.extend(f"  {line}" for line in _relaunch_entry(plan, item, logger=logger))
    return result


def _installer_still_running(result: OrcaUpdateResult) -> OrcaUpdateResult:
    """Neither install nor relaunch while an installer may still be replacing files or killing Orca."""
    result.actions.append(
        "  - an Orca installer is still running after the wait; not installing or relaunching. "
        "Re-run `desktop-manager workspace update-orca` once it exits"
    )
    result.ok = False
    return result


def close_all_orca(*, timeout_seconds: float = _CLOSE_TIMEOUT_SECONDS, log: Logged | None = None) -> str:
    """Ask every Orca window to close, then force-kill whatever is still alive after the timeout."""
    from desktop_manager.win32.geometry import request_close_window
    from desktop_manager.win32.inventory import (
        collect_windows_matching_process,
        process_ids_for_image_name,
        terminate_process_id,
        wait_for_process_exit,
    )

    handles = collect_windows_matching_process(ORCA_PROCESS_NAME, current_desktop_only=False)
    for hwnd in handles:
        request_close_window(hwnd)
    _emit(log, f"Requested WM_CLOSE for {len(handles)} Orca window(s)")

    deadline = time.monotonic() + timeout_seconds
    while time.monotonic() < deadline:
        if not process_ids_for_image_name(ORCA_PROCESS_NAME):
            return f"closed {len(handles)} Orca window(s) gracefully"
        time.sleep(_POLL_INTERVAL_SECONDS)

    survivors = process_ids_for_image_name(ORCA_PROCESS_NAME)
    killed = sum(1 for pid in survivors if terminate_process_id(pid))
    for pid in survivors:
        _ = wait_for_process_exit(pid, timeout_seconds=_EXIT_TIMEOUT_SECONDS)
    _emit(log, f"Orca still running after {timeout_seconds:.0f}s; force-killed {killed} process(es)")
    return f"requested close for {len(handles)} Orca window(s); force-killed {killed} survivor(s)"


def _installer_appears(*, timeout_seconds: float) -> bool:
    deadline = time.monotonic() + timeout_seconds
    while time.monotonic() < deadline:
        if orca_installer_running():
            return True
        time.sleep(_POLL_INTERVAL_SECONDS)
    return False


def _relaunch_entry(plan: WorkspacePlan, item: _OrcaEntry, *, logger: RestoreRunLogger | None) -> list[str]:
    from desktop_manager.restore import launch_single_entry

    try:
        return launch_single_entry(plan, item.desktop, item.entry, dry_run=False, logger=logger)
    except ValueError as err:
        return [f"  - could not relaunch `{item.entry}` on {item.desktop}: {err}"]


def orca_entries(plan: WorkspacePlan) -> list[_OrcaEntry]:
    """Every enabled launch entry that starts Orca, in plan order (Work before Personal)."""
    return [
        _OrcaEntry(desktop.name, entry.name)
        for desktop in plan.desktops
        for entry in desktop.launch
        if not entry.disabled and entry_targets_orca(entry.window_process_name, entry.path)
    ]


def plan_launches_orca(plan: WorkspacePlan) -> bool:
    return bool(orca_entries(plan))


def entry_targets_orca(process_name: str | None, path: str | None) -> bool:
    if process_name and process_name.casefold() == ORCA_PROCESS_NAME:
        return True
    return bool(path and path.casefold().endswith(ORCA_PROCESS_NAME))


def _emit(log: Logged | None, message: str) -> None:
    if log is not None:
        log(message)
