"""Inspection and usage evaluation of installed applications.

Categorizes application bundles by size, version, and last-modified/accessed date,
highlighting dormant applications that are candidates for removal.
"""

from __future__ import annotations

import datetime as dt
import pathlib
import subprocess
from dataclasses import dataclass

from rig.estate.disksize import du


@dataclass(frozen=True)
class AppInfo:
    """Metadata and disk usage for one installed application."""

    name: str
    path: pathlib.Path
    bytes_used: int
    version: str
    modified: dt.datetime
    accessed: dt.datetime

    @property
    def is_dormant(self) -> bool:
        """True if the application has not been modified or accessed in >180 days."""
        now = dt.datetime.now(dt.UTC)
        age = now - max(self.modified, self.accessed)
        return age.days > 180


def _get_app_version(app_path: pathlib.Path) -> str:
    plist = app_path / "Contents" / "Info.plist"
    if not plist.is_file():
        return "unknown"
    try:
        proc = subprocess.run(
            ["defaults", "read", str(plist), "CFBundleShortVersionString"],
            capture_output=True,
            text=True,
            check=False,
            timeout=5,
        )
        if proc.returncode == 0 and proc.stdout.strip():
            return proc.stdout.strip()
    except (OSError, subprocess.SubprocessError):
        pass
    return "unknown"


def scan_applications(
    app_dirs: list[pathlib.Path] | None = None,
    *,
    min_size: int = 50 * 1024 * 1024,
) -> list[AppInfo]:
    """Scan standard application directories and return entries sorted largest first."""
    if app_dirs is None:
        app_dirs = [
            pathlib.Path("/Applications"),
            pathlib.Path("~/Applications").expanduser(),
        ]

    apps: list[AppInfo] = []
    seen: set[pathlib.Path] = set()

    for app_dir in app_dirs:
        if not app_dir.is_dir():
            continue
        try:
            candidates = [p for p in app_dir.iterdir() if p.suffix == ".app" and p.is_dir()]
        except OSError:
            continue

        for app_path in candidates:
            # Dedupe on the real path (symlinks / repeated roots), never the bundle name:
            # distinct installs sharing a name are separate disk usage.
            try:
                real = app_path.resolve()
            except OSError:
                real = app_path
            if real in seen:
                continue
            seen.add(real)

            size = du(app_path)
            if size < min_size:
                continue

            try:
                st = app_path.stat()
                mtime = dt.datetime.fromtimestamp(st.st_mtime, tz=dt.UTC)
                atime = dt.datetime.fromtimestamp(st.st_atime, tz=dt.UTC)
            except OSError:
                mtime = dt.datetime.now(dt.UTC)
                atime = mtime

            version = _get_app_version(app_path)
            apps.append(
                AppInfo(
                    name=app_path.name,
                    path=app_path,
                    bytes_used=size,
                    version=version,
                    modified=mtime,
                    accessed=atime,
                )
            )

    apps.sort(key=lambda a: -a.bytes_used)
    return apps
