"""Regression guard for version-agnostic WindowsApps (appx) path resolution.

The version segment of a WindowsApps directory changes on every auto-update, so
plans resolve MSIX executables by their stable package-name prefix via
``Get-AppxPackage``. That query is a slow PowerShell subprocess, so a successful
result is cached to disk and reused until the cached path stops existing. These
tests pin that behaviour so cold-logon restores never regress back to paying the
subprocess cost (or timing out) on every run.
"""

from __future__ import annotations

import sys
from pathlib import Path

import pytest

from desktop_manager import executable_paths

# `_resolve_appx_path` is anchored to the literal ``C:\Program Files\WindowsApps``
# and resolves via ``relative_to``; on non-Windows that raises and the function
# short-circuits to None, so these behaviours are only meaningful on Windows.
pytestmark = pytest.mark.skipif(sys.platform != "win32", reason="requires Windows WindowsApps path layout")


def _fake_install(tmp_path: Path) -> tuple[Path, Path]:
    """Create a fake current install dir + exe, returning (install_location, exe)."""
    install_location = tmp_path / "install-current"
    exe = install_location / "1Password.exe"
    exe.parent.mkdir(parents=True, exist_ok=True)
    exe.write_text("stub", encoding="utf-8")
    return install_location, exe


def _stale_windows_apps_path() -> Path:
    return Path(r"C:\Program Files\WindowsApps\Agilebits.1Password_0.0.0.0_x64__amwd9z03whsfe\1Password.exe")


def test_appx_resolution_caches_and_skips_subprocess_on_second_call(tmp_path, monkeypatch):
    monkeypatch.setenv("APPDATA", str(tmp_path / "appdata"))
    install_location, exe = _fake_install(tmp_path)

    calls: list[list[str]] = []

    def fake_run(cmd, *args, **kwargs):
        calls.append(cmd)

        class _Result:
            returncode = 0
            stderr = ""
            stdout = str(install_location) + "\n"

        return _Result()

    monkeypatch.setattr(executable_paths.subprocess, "run", fake_run)

    first = executable_paths._resolve_appx_path(_stale_windows_apps_path())
    assert first == str(exe)
    assert len(calls) == 1, "first resolution should query Get-AppxPackage once"

    second = executable_paths._resolve_appx_path(_stale_windows_apps_path())
    assert second == str(exe)
    assert len(calls) == 1, "second resolution should be served from cache, no subprocess"


def test_appx_cache_reresolves_when_cached_path_disappears(tmp_path, monkeypatch):
    monkeypatch.setenv("APPDATA", str(tmp_path / "appdata"))
    install_location, exe = _fake_install(tmp_path)

    new_install = tmp_path / "install-new"
    new_exe = new_install / "1Password.exe"
    new_exe.parent.mkdir(parents=True, exist_ok=True)
    new_exe.write_text("stub", encoding="utf-8")

    locations = iter([str(install_location), str(new_install)])

    def fake_run(cmd, *args, **kwargs):
        class _Result:
            returncode = 0
            stderr = ""
            stdout = next(locations) + "\n"

        return _Result()

    monkeypatch.setattr(executable_paths.subprocess, "run", fake_run)

    assert executable_paths._resolve_appx_path(_stale_windows_apps_path()) == str(exe)

    # Simulate an auto-update: the cached install dir is gone.
    exe.unlink()
    install_location.rmdir()

    # Cache entry now points at a non-existent path -> must re-query and refresh.
    assert executable_paths._resolve_appx_path(_stale_windows_apps_path()) == str(new_exe)
