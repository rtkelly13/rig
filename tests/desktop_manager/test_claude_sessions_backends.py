"""Terminal backends for `claude-sessions recover`: selection, the print fallback, and the Windows delegation.

Nothing here touches a real terminal. The Windows modules are replaced in `sys.modules`,
either by fakes or by None, which makes any import of them fail loudly.
"""

from __future__ import annotations

import sys
import types
from collections.abc import Sequence
from pathlib import Path
from typing import Any

import pytest
from typer.testing import CliRunner

from desktop_manager.claude_sessions import backends, cli
from desktop_manager.claude_sessions.backends import (
    PrintBackend,
    ResumeOptions,
    WindowsTerminalBackend,
    select_backend,
)
from desktop_manager.claude_sessions.discovery import Discovery, SessionRecord
from desktop_manager.claude_sessions.outcomes import Outcome
from desktop_manager.claude_sessions.plan import Slot, assign_slots
from desktop_manager.claude_sessions.terminal import TerminalTab, TerminalWindow

pytestmark = pytest.mark.ci

WT_UIA = "desktop_manager.claude_sessions.wt_uia"
RECOVER = "desktop_manager.claude_sessions.recover"


def _session(session_id: str, cwd: str) -> SessionRecord:
    return SessionRecord(
        session_id=session_id,
        transcript=Path(f"{session_id}.jsonl"),
        start_cwd=cwd,
        last_cwd=cwd,
        title=f"title {session_id[:4]}",
        first_prompt=None,
        prompt_count=1,
        last_activity=None,
        size_bytes=0,
        exit_kind="crashed",
        interactive=True,
    )


def _block_windows_modules(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setitem(sys.modules, WT_UIA, None)
    monkeypatch.setitem(sys.modules, RECOVER, None)


class _FakeUia:
    def __init__(self) -> None:
        self.windows = [TerminalWindow(7, "pwsh", (TerminalTab(0, "pwsh in code", True, (7, 0)),))]

    def list_windows(self) -> list[TerminalWindow]:
        return self.windows


class TestSelectBackend:
    @pytest.mark.parametrize("platform", ["linux", "darwin"])
    def test_print_everywhere_but_windows(self, monkeypatch: pytest.MonkeyPatch, platform: str):
        monkeypatch.setattr("sys.platform", platform)
        _block_windows_modules(monkeypatch)
        assert isinstance(select_backend(), PrintBackend)

    def test_windows_terminal_on_windows(self, monkeypatch: pytest.MonkeyPatch):
        monkeypatch.setattr("sys.platform", "win32")
        monkeypatch.setitem(sys.modules, WT_UIA, types.SimpleNamespace(WindowsTerminalUia=_FakeUia))
        backend = select_backend()
        assert isinstance(backend, WindowsTerminalBackend)
        assert backend.drives_terminal
        assert [w.hwnd for w in backend.list_windows()] == [7]

    def test_print_only_wins_on_windows(self, monkeypatch: pytest.MonkeyPatch):
        monkeypatch.setattr("sys.platform", "win32")
        _block_windows_modules(monkeypatch)
        backend = select_backend(print_only=True)
        assert isinstance(backend, PrintBackend)
        assert not backend.drives_terminal


class TestPrintBackend:
    def test_prints_one_resume_line_per_session_and_opens_nothing(self):
        lines: list[str] = []
        backend = PrintBackend(shell="posix", write=lines.append)
        sessions = [_session("aaaaaaaa-1111", "/work/a"), _session("bbbbbbbb-2222", "/work/it's b")]
        slots = assign_slots(sessions, backend.list_windows())

        outcomes = backend.resume(slots, options=ResumeOptions(claude_args=["--model=opus"]), on_event=print)

        assert lines == [
            "# 2 session(s). Run each command in a terminal of its own.",
            "",
            "# aaaaaaaa  title aaaa",
            "cd /work/a && claude --resume aaaaaaaa-1111 --model=opus",
            "",
            "# bbbbbbbb  title bbbb",
            """cd '/work/it'"'"'s b' && claude --resume bbbbbbbb-2222 --model=opus""",
        ]
        assert [(o.ok, o.detail) for o in outcomes] == [(True, "printed"), (True, "printed")]

    @pytest.mark.parametrize(("platform", "prefix"), [("linux", "cd /w"), ("win32", "Set-Location -LiteralPath")])
    def test_shell_follows_the_platform(self, monkeypatch: pytest.MonkeyPatch, platform: str, prefix: str):
        monkeypatch.setattr("sys.platform", platform)
        lines: list[str] = []
        backend = PrintBackend(write=lines.append)
        backend.resume([Slot(_session("abc", "/w"), None, None)], options=ResumeOptions(), on_event=print)
        assert lines[-1].startswith(prefix)


class TestWindowsTerminalBackend:
    def test_delegates_windows_and_execution_to_the_uia_layer(self, monkeypatch: pytest.MonkeyPatch, tmp_path: Path):
        calls: list[dict[str, Any]] = []

        def fake_execute(slots: Sequence[Slot], **kwargs: Any) -> list[Outcome]:
            calls.append(kwargs)
            return [Outcome(slot, ok=True, detail="running", pid=1) for slot in slots]

        monkeypatch.setitem(sys.modules, RECOVER, types.SimpleNamespace(execute=fake_execute))
        uia = _FakeUia()
        backend = WindowsTerminalBackend(uia, sessions_dir=tmp_path)  # pyright: ignore[reportArgumentType]
        slots = assign_slots([_session("abc", "/w")], backend.list_windows())
        options = ResumeOptions(claude_args=["--x"], stagger_seconds=0, verify_timeout=3, max_starting=2)

        outcomes = backend.resume(slots, options=options, on_event=print)

        assert [o.detail for o in outcomes] == ["running"]
        assert slots[0].window == 7
        (kwargs,) = calls
        assert kwargs["uia"] is uia
        assert kwargs["sessions_dir"] == tmp_path
        assert (kwargs["claude_args"], kwargs["stagger_seconds"], kwargs["verify_timeout"]) == (["--x"], 0, 3)
        assert kwargs["max_starting"] == 2


class TestRecoverCommand:
    @pytest.fixture
    def found(self, monkeypatch: pytest.MonkeyPatch) -> Discovery:
        discovery = Discovery(candidates=[_session("aaaaaaaa-1111", "/work/a")], live={}, skipped=[])
        monkeypatch.setattr(cli, "_discover", lambda since, crashed_only, include_cleared=False: discovery)  # noqa: ARG005
        return discovery

    @pytest.mark.usefixtures("found")
    def test_prints_commands_off_windows_without_loading_windows_modules(self, monkeypatch: pytest.MonkeyPatch):
        monkeypatch.setattr("sys.platform", "linux")
        _block_windows_modules(monkeypatch)

        result = CliRunner().invoke(cli.claude_sessions_app, ["recover", "--all", "--claude-arg=--model=opus"])

        assert result.exit_code == 0, result.output
        assert "cd /work/a && claude --resume aaaaaaaa-1111 --model=opus" in result.output
        assert "only available" not in result.output

    @pytest.mark.usefixtures("found")
    def test_print_flag_on_windows_prints_instead_of_typing(self, monkeypatch: pytest.MonkeyPatch):
        monkeypatch.setattr("sys.platform", "win32")
        _block_windows_modules(monkeypatch)

        result = CliRunner().invoke(cli.claude_sessions_app, ["recover", "aaaa", "--print"])

        assert result.exit_code == 0, result.output
        assert "claude --resume aaaaaaaa-1111" in result.output
        assert "Set-Location -LiteralPath '/work/a'" in result.output

    @pytest.mark.usefixtures("found")
    def test_driving_backend_reports_running_sessions(self, monkeypatch: pytest.MonkeyPatch):
        class FakeDriver:
            drives_terminal = True

            def list_windows(self) -> list[TerminalWindow]:
                return []

            def resume(self, slots: Sequence[Slot], **_: object) -> list[Outcome]:
                return [Outcome(slot, ok=True, detail="running", pid=1) for slot in slots]

        monkeypatch.setattr(backends, "select_backend", lambda print_only: FakeDriver())  # noqa: ARG005

        result = CliRunner().invoke(cli.claude_sessions_app, ["recover", "--all"])

        assert result.exit_code == 0, result.output
        assert "-> new window" in result.output
        assert "1/1 running" in result.output
