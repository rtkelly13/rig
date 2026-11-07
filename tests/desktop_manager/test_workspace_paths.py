import os
import time
from pathlib import Path

import pytest

from desktop_manager.windows_workspace import resolve_versioned_path


def test_resolve_versioned_path_keeps_existing_path(tmp_path: Path):
    exe = tmp_path / "tool.exe"
    exe.write_text("", encoding="utf-8")

    assert resolve_versioned_path(str(exe)) == str(exe)


def test_resolve_versioned_path_returns_original_when_no_search_root_exists(tmp_path: Path):
    missing = tmp_path / "missing" / "deeper" / "tool.exe"

    assert resolve_versioned_path(str(missing)) == str(missing)


def test_resolve_versioned_path_picks_newest_matching_invariant_exe(tmp_path: Path):
    older_dir = tmp_path / "app-1"
    newer_dir = tmp_path / "app-2"
    older_dir.mkdir()
    newer_dir.mkdir()
    older = older_dir / "slack.exe"
    newer = newer_dir / "slack.exe"
    older.write_text("old", encoding="utf-8")
    newer.write_text("new", encoding="utf-8")

    now = time.time()
    os.utime(older, (now - 10, now - 10))
    os.utime(newer, (now, now))

    requested = tmp_path / "app-9" / "slack.exe"

    assert resolve_versioned_path(str(requested)) == str(newer)


def test_resolve_versioned_path_uses_where_for_bare_executable_name(monkeypatch):
    expected = (
        "C:\\Users\\someone\\AppData\\Local\\Microsoft\\WinGet\\Packages\\"
        "Anthropic.ClaudeCode_Microsoft.Winget.Source_8wekyb3d8bbwe\\claude.exe"
    )

    class CompletedProcess:
        returncode = 0
        stderr = ""
        stdout = expected + "\n"

    def fake_run(command: list[str], **kwargs: object) -> CompletedProcess:
        assert command == ["where.exe", "claude.exe"]
        return CompletedProcess()

    monkeypatch.setattr("sys.platform", "win32")
    monkeypatch.setattr("desktop_manager.executable_paths.subprocess.run", fake_run)
    monkeypatch.setattr(Path, "exists", lambda self: str(self) == expected)

    assert resolve_versioned_path("claude.exe") == expected


@pytest.mark.windows
def test_resolve_versioned_path_keeps_bare_executable_name_when_where_fails(monkeypatch):
    class CompletedProcess:
        returncode = 1
        stderr = ""
        stdout = ""

    monkeypatch.setattr("sys.platform", "win32")
    monkeypatch.setattr("desktop_manager.executable_paths.subprocess.run", lambda *_args, **_kwargs: CompletedProcess())

    assert resolve_versioned_path("claude.exe") == "claude.exe"


def test_resolve_versioned_path_keeps_bare_executable_name_when_where_unavailable(monkeypatch):
    monkeypatch.setattr("desktop_manager.executable_paths.resolve_executable_on_path", lambda _path: None)

    assert resolve_versioned_path("claude.exe") == "claude.exe"
