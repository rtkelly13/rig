"""Resolving a plan entry's executable: version wildcards, PATH lookups and AppX installs."""

from __future__ import annotations

import json
import os
import subprocess
import sys
from pathlib import Path

from rig import tracing
from rig.typed_json import JsonValue, try_decode_json

_APPX_MIN_PATH_PARTS = 2


def resolve_versioned_path(path: str) -> str:
    """Resolve an executable path that may contain a version-stamped directory.

    Also supports bare executable names like ``claude.exe`` by querying
    ``where.exe`` on Windows so plan entries can rely on PATH/App Execution
    Aliases instead of a constantly changing install directory.

    Detects patterns like ``WindowsApps/MSTeams_<version>_x64__<hash>/ms-teams.exe``
    or ``slack/app-<version>/slack.exe`` where the version segment changes on updates.
    When the exact path does not exist, these strategies are tried:

    1. For bare executable names, query ``where.exe``.
    2. For paths under ``C:\\Program Files\\WindowsApps`` (permission-protected),
       uses ``Get-AppxPackage`` to resolve the current install location.
    3. For all other paths, the parent directory is globbed for siblings that match
       the executable name, and the most recently modified candidate is returned.

    Returns the original path unchanged if it already exists or no candidate is found.
    """
    expanded = Path(os.path.expandvars(path))
    if expanded.exists():
        return str(expanded)

    if _is_bare_executable_name(path):
        resolved_on_path = resolve_executable_on_path(path)
        if resolved_on_path is not None:
            return resolved_on_path

    # Strategy 1: WindowsApps MSIX packages -- use Get-AppxPackage
    windows_apps_prefix = Path(r"C:\Program Files\WindowsApps")
    try:
        if expanded.is_relative_to(windows_apps_prefix):
            resolved = _resolve_appx_path(expanded)
            if resolved is not None:
                return resolved
    except (ValueError, TypeError):
        pass

    # Strategy 2: glob for versioned directories in user-accessible locations
    exe_name = expanded.name
    search_root = expanded.parent
    depth = 0
    while not search_root.exists() and search_root != search_root.parent:
        search_root = search_root.parent
        depth += 1

    if not search_root.exists() or depth == 0:
        return path  # Nothing useful to glob -- return original for a clear error later

    # Build a glob pattern: one '*' per missing directory level, then the exe name
    glob_pattern = "/".join(["*"] * depth + [exe_name])
    try:
        candidates = sorted(search_root.glob(glob_pattern), key=lambda p: p.stat().st_mtime, reverse=True)
    except PermissionError:
        return path
    if candidates:
        return str(candidates[0])

    return path  # No match -- return original so the caller gets a clear FileNotFoundError


def _is_bare_executable_name(path: str) -> bool:
    candidate = Path(os.path.expandvars(path)).expanduser()
    return len(candidate.parts) == 1 and candidate.drive == ""


def resolve_executable_on_path(path: str) -> str | None:
    if sys.platform != "win32":
        return None

    try:
        result = tracing.run(
            ["where.exe", path],
            capture_output=True,
            text=True,
            timeout=5,
            check=False,
        )
    except (subprocess.TimeoutExpired, FileNotFoundError, OSError):
        return None

    if result.returncode != 0:
        return None

    for line in result.stdout.splitlines():
        candidate = line.strip()
        if candidate and Path(candidate).exists():
            return candidate

    return None


def _appx_cache_path() -> Path:
    appdata = os.environ.get("APPDATA")
    base = Path(appdata) if appdata else Path.home() / ".config"
    return base / "workspace-manager" / "appx-resolve-cache.json"


def _read_appx_cache(package_prefix: str) -> str | None:
    """Return a previously resolved, still-existing install path for ``package_prefix``."""
    try:
        text = _appx_cache_path().read_text(encoding="utf-8")
    except OSError:
        return None
    cache = try_decode_json(dict[str, JsonValue], text)
    cached = cache.get(package_prefix) if cache is not None else None
    if isinstance(cached, str) and Path(cached).exists():
        return cached
    return None


def _write_appx_cache(package_prefix: str, resolved: str) -> None:
    """Persist a resolved install path so cold-logon restores skip the slow query."""
    cache_path = _appx_cache_path()
    try:
        existing = try_decode_json(dict[str, JsonValue], cache_path.read_text(encoding="utf-8")) or {}
    except OSError:
        existing = {}
    if existing.get(package_prefix) == resolved:
        return
    existing[package_prefix] = resolved
    try:
        cache_path.parent.mkdir(parents=True, exist_ok=True)
        cache_path.write_text(json.dumps(existing, indent=2), encoding="utf-8")
    except OSError:
        pass


def _resolve_appx_path(expanded: Path) -> str | None:
    """Resolve a WindowsApps path by package-name prefix, independent of version.

    The version segment of a WindowsApps directory (e.g. the ``8.12.28.25`` in
    ``Agilebits.1Password_8.12.28.25_x64__amwd9z03whsfe``) changes on every
    auto-update, so the pinned path in a plan goes stale continually. Rather than
    tracking the version, this extracts the stable package-name prefix (e.g.
    ``Agilebits.1Password``) and resolves the *current* install location by prefix.

    ``WindowsApps`` is ACL-protected against directory listing, so a filesystem
    glob for the prefix returns nothing; ``Get-AppxPackage`` is the only reliable
    prefix lookup. That is a PowerShell subprocess and is slow at cold logon, so a
    successful result is cached to disk and reused on subsequent runs -- the query
    only re-runs once the cached path stops existing (i.e. after the next update).
    """
    windows_apps_prefix = Path(r"C:\Program Files\WindowsApps")
    try:
        relative = expanded.relative_to(windows_apps_prefix)
    except ValueError:
        return None

    parts = relative.parts
    if len(parts) < _APPX_MIN_PATH_PARTS:
        return None

    # The versioned directory name is the first part, e.g. "MSTeams_26032.208.4399.5_x64__8wekyb3d8bbwe"
    versioned_dir = parts[0]
    exe_subpath = Path(*parts[1:])

    # Extract the package name prefix (everything before the first underscore)
    package_prefix = versioned_dir.split("_")[0] if "_" in versioned_dir else versioned_dir

    cached = _read_appx_cache(package_prefix)
    if cached is not None:
        return cached

    try:
        result = tracing.run(
            [
                "powershell",
                "-NoProfile",
                "-NonInteractive",
                "-Command",
                f"(Get-AppxPackage *{package_prefix}*).InstallLocation",
            ],
            capture_output=True,
            text=True,
            # Cold-logon restore: `powershell` startup + Get-AppxPackage enumeration
            # regularly exceeds 10s, which timed out and fell back to the stale literal
            # path (dumping a "Restore Error" note). 30s tolerates the cold-start cost so
            # version drift degrades to a slower resolve rather than a hard failure.
            timeout=30,
        )
        install_location = result.stdout.strip()
        if install_location:
            resolved = Path(install_location) / exe_subpath
            if resolved.exists():
                _write_appx_cache(package_prefix, str(resolved))
                return str(resolved)
    except (subprocess.TimeoutExpired, FileNotFoundError, OSError):
        pass

    return None
