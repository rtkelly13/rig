"""Installed applications on Windows, read from the Uninstall registry keys.

The macOS scan walks `.app` bundles; Windows has no equivalent directory to
walk. What it has is the list Settings > Apps shows, which lives in three
Uninstall hives: machine-wide 64-bit, machine-wide 32-bit (WOW6432Node), and
per-user. Each entry may carry `EstimatedSize` (KB, written by the installer
and occasionally stale), `InstallDate` (YYYYMMDD) and `InstallLocation`.

When an installer skipped `EstimatedSize` the install directory is measured
instead, so the size column is never silently zero for an app that has a
known location.
"""

from __future__ import annotations

import datetime as dt
import pathlib
import sys
from dataclasses import dataclass

from desktop_manager.win32 import dlls
from rig.estate.apps import AppInfo
from rig.estate.disksize import du

_UNINSTALL = r"Software\Microsoft\Windows\CurrentVersion\Uninstall"
_UNINSTALL_WOW = r"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"


def _parse_install_date(raw: str) -> dt.datetime | None:
    try:
        return dt.datetime.strptime(raw.strip(), "%Y%m%d").replace(tzinfo=dt.UTC)
    except ValueError:
        return None


@dataclass(slots=True, frozen=True)
class UninstallEntry:
    """The values of one Uninstall key this scan reads; each is whatever the installer wrote, or None."""

    display_name: object = None
    display_version: object = None
    estimated_size: object = None
    install_date: object = None
    install_location: object = None
    system_component: object = None
    parent_key_name: object = None


@dataclass(slots=True, frozen=True)
class _AppKey:
    """Identity for de-duplicating an app registered in more than one hive."""

    name: str
    version: str


def entry_to_app(entry: UninstallEntry, *, measure: bool = True) -> AppInfo | None:
    """Build an AppInfo from one Uninstall key's values, or None for a non-app entry."""
    name = entry.display_name
    if not isinstance(name, str) or not name.strip():
        return None
    # Updates and runtime components are children of an app, not apps of their own.
    if entry.system_component == 1 or entry.parent_key_name:
        return None

    location_raw = entry.install_location
    location = (
        pathlib.Path(location_raw.strip().strip('"'))
        if isinstance(location_raw, str) and location_raw.strip()
        else None
    )

    estimated = entry.estimated_size
    size = int(estimated) * 1024 if isinstance(estimated, int) and estimated > 0 else 0
    if size == 0 and measure and location is not None and location.is_dir():
        size = du(location)

    raw_date = entry.install_date
    installed = _parse_install_date(raw_date) if isinstance(raw_date, str) else None
    touched: dt.datetime | None = None
    if location is not None:
        try:
            touched = dt.datetime.fromtimestamp(location.stat().st_mtime, tz=dt.UTC)
        except OSError:
            touched = None
    modified = max((d for d in (installed, touched) if d is not None), default=dt.datetime.now(dt.UTC))

    version = entry.display_version
    return AppInfo(
        name=name.strip(),
        path=location or pathlib.Path(),
        bytes_used=size,
        version=version.strip() if isinstance(version, str) and version.strip() else "unknown",
        modified=modified,
        accessed=modified,
    )


def _read_hive(root: int, subkey: str) -> list[UninstallEntry]:
    import winreg

    def value(key: winreg.HKEYType, name: str) -> object:
        try:
            return dlls.registry_value(winreg.QueryValueEx, key, name)
        except OSError:
            return None

    entries: list[UninstallEntry] = []
    try:
        parent = winreg.OpenKey(root, subkey)
    except OSError:
        return entries
    with parent:
        index = 0
        while True:
            try:
                child_name = winreg.EnumKey(parent, index)
            except OSError:
                break
            index += 1
            try:
                with winreg.OpenKey(parent, child_name) as child:
                    entries.append(
                        UninstallEntry(
                            display_name=value(child, "DisplayName"),
                            display_version=value(child, "DisplayVersion"),
                            estimated_size=value(child, "EstimatedSize"),
                            install_date=value(child, "InstallDate"),
                            install_location=value(child, "InstallLocation"),
                            system_component=value(child, "SystemComponent"),
                            parent_key_name=value(child, "ParentKeyName"),
                        )
                    )
            except OSError:
                continue
    return entries


def scan_applications(*, min_size: int = 50 * 1024 * 1024) -> list[AppInfo]:
    """Every installed app at or above *min_size*, largest first."""
    if sys.platform != "win32":
        return []
    import winreg

    hives = (
        (winreg.HKEY_LOCAL_MACHINE, _UNINSTALL),
        (winreg.HKEY_LOCAL_MACHINE, _UNINSTALL_WOW),
        (winreg.HKEY_CURRENT_USER, _UNINSTALL),
    )
    apps: list[AppInfo] = []
    seen: set[_AppKey] = set()
    for root, subkey in hives:
        for entry in _read_hive(root, subkey):
            app = entry_to_app(entry)
            if app is None or app.bytes_used < min_size:
                continue
            # The same product often registers in both the 32- and 64-bit hives.
            key = _AppKey(name=app.name.casefold(), version=app.version)
            if key in seen:
                continue
            seen.add(key)
            apps.append(app)
    apps.sort(key=lambda a: -a.bytes_used)
    return apps
