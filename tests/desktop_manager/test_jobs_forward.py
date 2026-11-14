"""Legacy jobs spellings delegate every argument to the C# owner."""

from __future__ import annotations

import subprocess
from pathlib import Path

import pytest
from typer.testing import CliRunner

from desktop_manager.cli import app
from rig import csharp_cli

pytestmark = pytest.mark.ci


@pytest.mark.parametrize("arguments", [[], ["--help"], ["--unknown", "a b"]])
@pytest.mark.parametrize("command", ["install", "uninstall", "status", "last", "log", "run"])
def test_jobs_passes_the_whole_line_and_exit_code(command: str, arguments: list[str]) -> None:
    calls: list[list[str]] = []

    def send(argv: list[str], _environment: dict[str, str]) -> int:
        calls.append(argv)
        return 3

    token = csharp_cli.RUNNER.set(send)
    try:
        result = CliRunner().invoke(app, ["jobs", command, *arguments])
    finally:
        csharp_cli.RUNNER.reset(token)

    assert result.exit_code == 3, result.output
    assert len(calls) == 1
    assert calls[0][calls[0].index("--") + 1 :] == ["jobs", command, *arguments]


@pytest.mark.parametrize("arguments", [[], ["logon"], ["tick", "--dry-run"], ["bad-trigger"]])
def test_the_legacy_scheduled_entry_uses_the_native_windowless_coordinator(arguments: list[str]) -> None:
    from desktop_manager.jobs import scheduled

    calls: list[list[str]] = []

    def send(argv: list[str]) -> int:
        calls.append(argv)
        return 3

    assert scheduled.main(arguments, runner=send) == 3
    assert len(calls) == 1
    assert calls[0][0].endswith("rigw.exe")
    assert calls[0][1:] == ["jobs", "run", "--scheduled", *(["--trigger", *arguments] if arguments else [])]


def test_the_legacy_scheduled_entry_returns_127_when_the_publish_is_missing() -> None:
    from desktop_manager.jobs import scheduled

    def missing(_argv: list[str]) -> int:
        raise FileNotFoundError

    assert scheduled.main([], runner=missing) == 127


@pytest.mark.parametrize("installed", [False, True])
def test_scheduled_entry_resolves_the_launcher_beside_its_own_copy(
    monkeypatch, tmp_path: Path, installed: bool
) -> None:
    from desktop_manager.jobs import scheduled

    root = tmp_path / "runtime" / "current" if installed else tmp_path / "checkout"
    monkeypatch.setattr(scheduled, "__file__", str(root / "src" / "desktop_manager" / "jobs" / "scheduled.py"))
    monkeypatch.setattr(scheduled.runtime, "running_copy", lambda _file: installed)
    monkeypatch.setattr(scheduled.sys, "argv", ["scheduled.py", "logon"])
    calls: list[list[str]] = []

    def send(argv: list[str]) -> int:
        calls.append(argv)
        return 0

    assert scheduled.main(runner=send) == 0
    publish = root / "rig" if installed else root / "temp" / "jobs-runtime"
    assert calls == [[str(publish / "rigw.exe"), "jobs", "run", "--scheduled", "--trigger", "logon"]]


@pytest.mark.parametrize("platform", ["win32", "linux"])
def test_scheduled_child_has_no_console_or_stdin(monkeypatch, platform: str) -> None:
    from desktop_manager.jobs import scheduled

    monkeypatch.setattr(scheduled.sys, "platform", platform)
    monkeypatch.setattr(subprocess, "CREATE_NO_WINDOW", 0x08000000, raising=False)

    def run(command, **kwargs):
        assert command == ["rigw.exe", "jobs", "run"]
        assert kwargs == {
            "capture_output": True,
            "stdin": subprocess.DEVNULL,
            "creationflags": 0x08000000 if platform == "win32" else 0,
        }
        return subprocess.CompletedProcess(command, 7)

    monkeypatch.setattr(scheduled.tracing, "run", run)
    assert scheduled._native(["rigw.exe", "jobs", "run"]) == 7
