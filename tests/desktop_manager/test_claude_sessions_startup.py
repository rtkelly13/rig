"""`claude-sessions startup`: deferred terminals in the plan, the logon planner, fresh starts, and the command.

Everything runs against fakes: no terminal, desktop or `claude` process is touched.
"""

from __future__ import annotations

import base64
import json
from collections.abc import Sequence
from dataclasses import replace
from datetime import UTC, datetime, timedelta
from pathlib import Path

import pytest
from typer.testing import CliRunner

from desktop_manager import restore_fallbacks, terminal_init
from desktop_manager.claude_sessions import backends, cli, recover
from desktop_manager.claude_sessions.discovery import Discovery, ExitKind, SessionRecord, TurnState
from desktop_manager.claude_sessions.outcomes import FreshOutcome, Outcome
from desktop_manager.claude_sessions.plan import Slot
from desktop_manager.claude_sessions.startup import (
    DeferredTerminal,
    FreshTarget,
    group_key,
    plan_startup,
    wants_resume_at_logon,
)
from desktop_manager.claude_sessions.startup_run import (
    EXIT_DEFERRED,
)
from desktop_manager.claude_sessions.terminal import TerminalTab, TerminalWindow
from desktop_manager.launching import build_terminal_command
from desktop_manager.models import DesktopPlan, LaunchEntry, WorkspacePlan
from desktop_manager.placement import MonitorShortfall, validate_plan
from desktop_manager.plan import defer_logon_commands, deferred_logon_terminals

pytestmark = pytest.mark.ci

NOW = datetime.now(UTC).replace(microsecond=0)


def _session(
    session_id: str,
    *,
    cwd: str = "/work",
    minutes_ago: int = 0,
    exit_kind: ExitKind = "crashed",
    turn_state: TurnState = "answered",
) -> SessionRecord:
    return SessionRecord(
        session_id=session_id,
        transcript=Path(f"{session_id}.jsonl"),
        start_cwd=cwd,
        last_cwd=cwd,
        title=session_id,
        first_prompt=None,
        prompt_count=1,
        last_activity=NOW - timedelta(minutes=minutes_ago),
        size_bytes=0,
        exit_kind=exit_kind,
        interactive=True,
        turn_state=turn_state,
    )


def _window(hwnd: int, *titles: str) -> TerminalWindow:
    return TerminalWindow(
        hwnd=hwnd,
        title=titles[0],
        tabs=tuple(TerminalTab(i, title, i == 0, (hwnd, i)) for i, title in enumerate(titles)),
    )


def _terminal(number: int, cwd: str = r"C:\code\workspace") -> DeferredTerminal:
    return DeferredTerminal(f"Workspace Terminal {number}", f"Workspace Terminal {number}", cwd, "claude")


def _by_cwd(session: SessionRecord) -> str:
    return session.last_cwd or ""


def _where(slots: Sequence[Slot]) -> list[tuple[str, int | None, tuple[int, ...] | None]]:
    return [(slot.session.session_id, slot.window, slot.tab) for slot in slots]


# --- the plan model -----------------------------------------------------------------------------------


def _terminal_entry(**overrides: object) -> LaunchEntry:
    entry = LaunchEntry(
        name="Workspace Terminal 1",
        type="terminal",
        command="claude",
        cwd="{workspace}",
        shell="wt-pwsh",
        window_title="Workspace Terminal 1",
        command_at_logon="deferred",
    )
    return replace(entry, **overrides)  # pyright: ignore[reportArgumentType]


def _plan(*entries: LaunchEntry, disabled_desktop: bool = False) -> WorkspacePlan:
    return WorkspacePlan(desktops=[DesktopPlan(name="Work", launch=list(entries), disabled=disabled_desktop)])


class TestDeferredTerminals:
    def test_only_a_logon_restore_withholds_the_command(self):
        deferred, plain = _terminal_entry(), _terminal_entry(name="Other", command_at_logon=None)
        plan = _plan(deferred, plain)

        logon = defer_logon_commands(plan)

        assert [e.command for e in logon.desktops[0].launch] == [None, "claude"]
        assert [e.command for e in plan.desktops[0].launch] == ["claude", "claude"]

    def test_deferred_terminals_skip_disabled_entries_and_desktops(self):
        on, off = _terminal_entry(), _terminal_entry(name="Off", disabled=True)
        assert [e.name for e in deferred_logon_terminals(_plan(on, off))] == ["Workspace Terminal 1"]
        assert deferred_logon_terminals(_plan(on, disabled_desktop=True)) == []

    def test_validation_names_unknown_modes_and_deferral_off_windows_terminal(self):
        plan = _plan(
            _terminal_entry(name="Odd", command_at_logon="later"),
            _terminal_entry(name="Cmd", shell="cmd"),
            _terminal_entry(name="Fine"),
        )

        assert validate_plan(plan) == [
            "Work/Odd: command_at_logon must be one of deferred, run, not 'later'",
            'Work/Cmd: command_at_logon "deferred" is only for terminal entries with shell wt-pwsh',
        ]

    def test_a_deferred_entry_without_its_command_launches_a_bare_shell(self, tmp_path: Path):
        entry = replace(_terminal_entry(), command=None)

        command = build_terminal_command(entry, {"workspace": str(tmp_path)})

        assert command[-4:] == ["--startingDirectory", str(tmp_path), "pwsh.exe", "-NoLogo"]
        assert "--title" in command

    def test_a_bare_shell_still_carries_the_restore_token(self, tmp_path: Path):
        entry = replace(_terminal_entry(), command=None)

        command = build_terminal_command(entry, {"workspace": str(tmp_path)}, restore_token="t-1")

        decoded = base64.b64decode(command[-1]).decode("utf-16le")
        assert decoded == "$env:OPENCODE_RESTORE_TOKEN = 't-1'; $env:OPENCODE_RESTORE_ENTRY = 'Workspace Terminal 1'"

    def test_an_entry_that_is_not_deferred_still_needs_its_command(self, tmp_path: Path):
        entry = replace(_terminal_entry(command_at_logon=None), command=None)
        with pytest.raises(ValueError, match="must define both cwd and command"):
            build_terminal_command(entry, {"workspace": str(tmp_path)})


# --- the planner ---------------------------------------------------------------------------------------


class TestWhichSessions:
    @pytest.mark.parametrize(
        ("exit_kind", "turn_state", "default", "with_exited"),
        [
            ("crashed", "answered", True, True),
            ("exited", "mid-tool", True, True),
            ("exited", "unanswered", True, True),
            ("exited", "answered", False, True),
        ],
    )
    def test_crashed_or_mid_task_by_default_clean_exits_on_request(
        self, exit_kind: ExitKind, turn_state: TurnState, default: bool, with_exited: bool
    ):
        record = _session("a", exit_kind=exit_kind, turn_state=turn_state)
        assert wants_resume_at_logon(record, include_exited=False) is default
        assert wants_resume_at_logon(record, include_exited=True) is with_exited

    def test_group_key_is_the_git_root_or_the_folder(self):
        roots = {"/repo/src": "/Repo"}
        assert group_key(_session("a", cwd="/repo/src"), roots.get) == "/repo"
        assert group_key(_session("b", cwd="/Loose"), roots.get) == "/loose"


class TestPlanStartup:
    def test_sessions_from_one_repo_share_a_window_and_unused_planned_terminals_start_fresh(self):
        windows = [_window(1, "pwsh in workspace"), _window(2, "pwsh in workspace"), _window(3, "pwsh in workspace")]
        sessions = [_session("a", cwd="/x"), _session("b", cwd="/y"), _session("c", cwd="/x")]

        plan = plan_startup(sessions, windows, [_terminal(n) for n in (1, 2, 3)], group_of=_by_cwd)

        assert _where(plan.resume) == [("a", 1, (1, 0)), ("c", 1, None), ("b", 2, (2, 0))]
        assert [(f.window, f.tab, f.terminal.name) for f in plan.fresh] == [(3, (3, 0), "Workspace Terminal 3")]
        assert plan.left_over == ()
        assert plan.unmatched == ()

    def test_a_group_takes_idle_tabs_in_its_window_before_opening_new_ones(self):
        windows = [_window(1, "pwsh", "✳ busy", "Command Prompt")]

        plan = plan_startup([_session("a"), _session("b"), _session("c")], windows, [], group_of=_by_cwd)

        assert _where(plan.resume) == [("a", 1, (1, 0)), ("b", 1, (1, 2)), ("c", 1, None)]

    def test_a_full_group_window_overflows_to_another_window(self):
        windows = [_window(1, "pwsh"), _window(2, "✳ busy")]
        sessions = [_session(s) for s in "abc"]

        plan = plan_startup(sessions, windows, [], group_of=_by_cwd, max_tabs_per_window=2)

        assert _where(plan.resume) == [("a", 1, (1, 0)), ("b", 1, None), ("c", 2, None)]

    def test_with_no_terminal_window_open_sessions_are_left_over_saying_so(self):
        plan = plan_startup([_session("a")], [], [], group_of=_by_cwd)

        assert [(item.session.session_id, item.reason) for item in plan.left_over] == [
            ("a", "no Windows Terminal window is open")
        ]

    def test_no_new_windows_at_logon_sessions_with_no_room_are_left_over(self):
        plan = plan_startup(
            [_session("a"), _session("b")], [_window(1, "✳ busy")], [], group_of=_by_cwd, max_tabs_per_window=2
        )

        assert _where(plan.resume) == [("a", 1, None)]
        assert [(item.session.session_id, item.reason) for item in plan.left_over] == [
            ("b", "no idle tab and every window has 2 tabs")
        ]

    def test_the_newest_sessions_within_the_cap_are_resumed(self):
        sessions = [_session(s, minutes_ago=i) for i, s in enumerate("abc")]

        plan = plan_startup(sessions, [_window(1, "pwsh")], [], group_of=_by_cwd, max_sessions=2)

        assert [slot.session.session_id for slot in plan.resume] == ["a", "b"]
        assert [(item.session.session_id, item.reason) for item in plan.left_over] == [
            ("c", "over the --max-sessions cap of 2")
        ]

    def test_resumes_use_stray_idle_tabs_before_planned_terminals(self):
        windows = [_window(1, "Workspace Terminal 1", "pwsh in elsewhere")]

        plan = plan_startup([_session("a")], windows, [_terminal(1)], group_of=_by_cwd)

        assert _where(plan.resume) == [("a", 1, (1, 1))]
        assert [(f.tab, f.terminal.name) for f in plan.fresh] == [((1, 0), "Workspace Terminal 1")]

    def test_planned_terminals_pair_by_title_then_folder_then_order(self):
        windows = [
            _window(1, "pwsh in workspace"),
            _window(2, "pwsh in personal"),
            _window(3, "Workspace Terminal 3"),
            _window(4, "PowerShell"),
        ]
        terminals = [_terminal(1), _terminal(2, cwd=r"C:\code\personal"), _terminal(3), _terminal(4)]

        plan = plan_startup([], windows, terminals, group_of=_by_cwd)

        assert {f.window: f.terminal.name for f in plan.fresh} == {
            1: "Workspace Terminal 1",
            2: "Workspace Terminal 2",
            3: "Workspace Terminal 3",
            4: "Workspace Terminal 4",
        }

    def test_planned_terminals_with_no_idle_tab_are_reported_not_typed_into(self):
        plan = plan_startup([], [_window(1, "✳ Fix the build")], [_terminal(1)], group_of=_by_cwd)

        assert plan.fresh == ()
        assert [t.name for t in plan.unmatched] == ["Workspace Terminal 1"]
        assert plan.is_empty

    def test_a_deferred_window_title_counts_as_idle(self):
        plan = plan_startup([_session("a")], [_window(1, "Workspace Terminal 1")], [_terminal(1)], group_of=_by_cwd)
        assert _where(plan.resume) == [("a", 1, (1, 0))]
        assert plan.fresh == ()


# --- fresh starts against a fake terminal --------------------------------------------------------------


class _FakeUia:
    """Tabs whose titles turn into a Claude title once their command is typed (unless told otherwise)."""

    def __init__(self, titles: dict[tuple[int, ...], str], *, never_starts: frozenset[tuple[int, ...]] = frozenset()):
        self.titles = titles
        self.never_starts = never_starts
        self.typed: list[tuple[tuple[int, ...], str]] = []

    def tabs(self, hwnd: int) -> tuple[TerminalTab, ...]:
        return tuple(
            TerminalTab(i, title, False, tab) for i, (tab, title) in enumerate(self.titles.items()) if tab[0] == hwnd
        )

    def select_tab(self, hwnd: int, runtime_id: tuple[int, ...]) -> TerminalTab:
        return TerminalTab(0, self.titles[runtime_id], True, runtime_id)

    def wait_for_prompt(self, hwnd: int) -> None:
        pass

    def selected_text(self, hwnd: int) -> str:
        return "PS C:\\code\\workspace>"

    def type_line(self, hwnd: int, runtime_id: tuple[int, ...], text: str) -> None:
        self.typed.append((runtime_id, text))
        if runtime_id not in self.never_starts:
            self.titles[runtime_id] = "✳ Claude Code"


class TestStartFresh:
    @pytest.fixture(autouse=True)
    def _no_focus_changes(self, monkeypatch: pytest.MonkeyPatch) -> None:
        monkeypatch.setattr(recover, "foreground_window", lambda: 0)
        monkeypatch.setattr(recover, "restore_foreground", lambda _: None)

    def _targets(self, uia: _FakeUia) -> list[FreshTarget]:
        return [FreshTarget(tab[0], tab, title, _terminal(tab[0])) for tab, title in uia.titles.items()]

    def test_types_each_terminal_command_in_its_folder_and_waits_for_claude_to_retitle_the_tab(self):
        uia = _FakeUia({(1, 0): "pwsh in workspace", (2, 0): "Workspace Terminal 2"})

        outcomes = recover.start_fresh(self._targets(uia), uia=uia, poll_seconds=0.01)  # pyright: ignore[reportArgumentType]

        assert [(o.target.window, o.ok, o.detail) for o in outcomes] == [
            (1, True, "started ('✳ Claude Code')"),
            (2, True, "started ('✳ Claude Code')"),
        ]
        assert uia.typed[0] == ((1, 0), "Set-Location -LiteralPath 'C:\\code\\workspace'; claude")

    def test_a_tab_that_got_busy_is_left_alone_and_one_that_never_starts_fails(self):
        uia = _FakeUia({(1, 0): "pwsh", (2, 0): "pwsh"}, never_starts=frozenset({(2, 0)}))
        targets = self._targets(uia)
        uia.titles[(1, 0)] = "✳ someone got here first"

        outcomes = recover.start_fresh(targets, uia=uia, verify_timeout=0.2, poll_seconds=0.01)  # pyright: ignore[reportArgumentType]

        assert [(o.target.window, o.ok, o.detail) for o in outcomes] == [
            (1, False, "busy now ('✳ someone got here first'); left alone"),
            (2, False, "typed, but not running after 0s"),
        ]
        assert [tab for tab, _ in uia.typed] == [(2, 0)]


# --- the command -----------------------------------------------------------------------------------------


class _StartupDriver:
    drives_terminal = True

    def __init__(self, windows: list[TerminalWindow], *, failing: frozenset[str] = frozenset()) -> None:
        self.windows = windows
        self.failing = failing
        self.resumed: list[Slot] = []
        self.fresh: list[FreshTarget] = []

    def list_windows(self) -> list[TerminalWindow]:
        return self.windows

    def resume(self, slots: Sequence[Slot], **_: object) -> list[Outcome]:
        self.resumed.extend(slots)
        return [
            Outcome(slot, ok=slot.session.session_id not in self.failing, detail="running", pid=1) for slot in slots
        ]

    def start_fresh(self, targets: Sequence[FreshTarget], **_: object) -> list[FreshOutcome]:
        self.fresh.extend(targets)
        return [FreshOutcome(target, ok=True, detail="started") for target in targets]


def _write_plan(path: Path, workspace: Path, terminals: int) -> Path:
    launch = [
        {
            "name": f"Workspace Terminal {n}",
            "type": "terminal",
            "shell": "wt-pwsh",
            "cwd": "{workspace}",
            "command": "claude",
            "command_at_logon": "deferred",
            "window_title": f"Workspace Terminal {n}",
        }
        for n in range(1, terminals + 1)
    ]
    plan = {"version": 1, "workspace": str(workspace), "desktops": [{"name": "Work", "launch": launch}]}
    path.write_text(json.dumps(plan), encoding="utf-8")
    return path


class TestStartupCommand:
    @pytest.fixture
    def env(self, tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> Path:
        monkeypatch.setenv("DESKTOP_MANAGER_LOG_DIR", str(tmp_path / "logs"))
        upgraded: list[str] = []
        monkeypatch.setattr(terminal_init, "ensure_terminal_command_initialized", lambda e: upgraded.append(e.name))
        return _write_plan(tmp_path / "plan.json", tmp_path, terminals=2)

    def _invoke(
        self,
        monkeypatch: pytest.MonkeyPatch,
        driver: _StartupDriver,
        sessions: list[SessionRecord],
        *args: str,
        locked: bool = False,
        windows: list[str | timedelta] | None = None,
        shortfall: MonitorShortfall | None = None,
        notes: list[tuple[str, list[str]]] | None = None,
    ) -> tuple[int, str]:
        found = Discovery(candidates=sessions, live={}, skipped=[])
        opened = [] if notes is None else notes

        def open_note(file_name: str, lines: Sequence[str], **_: object) -> Path:
            opened.append((file_name, list(lines)))
            return Path(file_name)

        def discover(since: str | timedelta, **_: object) -> Discovery:
            if windows is not None:
                windows.append(since)
            return found

        monkeypatch.setattr(cli, "_discover", discover)
        monkeypatch.setattr(cli, "_workstation_locked", lambda: locked)
        monkeypatch.setattr(cli, "_screen_shortfall", lambda *_: shortfall)
        monkeypatch.setattr(restore_fallbacks, "open_note", open_note)
        monkeypatch.setattr(backends, "select_backend", lambda print_only: driver)  # noqa: ARG005
        result = CliRunner().invoke(cli.claude_sessions_app, ["startup", *args])
        return result.exit_code, result.output

    def test_resumes_then_starts_fresh_logs_and_summarises(
        self, env: Path, tmp_path: Path, monkeypatch: pytest.MonkeyPatch
    ):
        driver = _StartupDriver([_window(1, "pwsh in workspace"), _window(2, "pwsh in workspace")])
        sessions = [_session("a"), _session("b", exit_kind="exited")]

        code, output = self._invoke(monkeypatch, driver, sessions, "--config", str(env), "--crashed-only")

        assert code == 0, output
        assert [s.session.session_id for s in driver.resumed] == ["a"]
        assert [(t.window, t.terminal.name) for t in driver.fresh] == [(2, "Workspace Terminal 2")]
        assert "resumed 1, started 1 fresh, skipped 0" in output
        log = (tmp_path / "logs" / "claude-sessions-startup.log").read_text(encoding="utf-8")
        assert "1 closed cleanly, not resumed" in log
        assert log.rstrip().endswith("resumed 1, started 1 fresh, skipped 0")

    def test_dry_run_types_nothing_and_writes_no_log(self, env: Path, tmp_path: Path, monkeypatch: pytest.MonkeyPatch):
        driver = _StartupDriver([_window(1, "pwsh in workspace")])

        code, output = self._invoke(monkeypatch, driver, [_session("a")], "--config", str(env), "--dry-run")

        assert code == 0, output
        assert "resume a" in output
        assert "would have resumed 1, started 0 fresh, skipped 0" in output
        assert driver.resumed == []
        assert not (tmp_path / "logs").exists()

    def test_nothing_to_do_exits_zero(self, env: Path, monkeypatch: pytest.MonkeyPatch):
        code, output = self._invoke(monkeypatch, _StartupDriver([_window(1, "✳ busy")]), [], "--config", str(env))

        assert code == 0, output
        assert "resumed 0, started 0 fresh, skipped 0" in output

    def test_sessions_left_unresumed_exit_3(self, env: Path, monkeypatch: pytest.MonkeyPatch):
        driver = _StartupDriver([_window(1, "pwsh in workspace")])

        code, output = self._invoke(
            monkeypatch, driver, [_session("a"), _session("b")], "--config", str(env), "--max-tabs", "1"
        )

        assert code == 3, output
        assert "resumed 1, started 0 fresh, skipped 1" in output
        assert "1 session(s) were left unresumed" in output

    def test_sessions_left_unresumed_are_listed_in_a_note_with_the_recover_command(
        self, env: Path, monkeypatch: pytest.MonkeyPatch
    ):
        driver = _StartupDriver([_window(1, "pwsh in workspace")])
        notes: list[tuple[str, list[str]]] = []

        code, output = self._invoke(
            monkeypatch,
            driver,
            [_session("a"), _session("b")],
            "--config",
            str(env),
            "--max-tabs",
            "1",
            "--since",
            "2h",
            notes=notes,
        )

        assert code == 3, output
        ((name, lines),) = notes
        assert name == "claude-sessions-left-over.txt"
        assert any(line.startswith("  b  ") and "no idle tab and every window has 1 tabs" in line for line in lines)
        assert lines[-1] == "  desktop-manager claude-sessions recover --since 3h b"

    def test_a_deferred_run_opens_no_note(self, env: Path, monkeypatch: pytest.MonkeyPatch):
        notes: list[tuple[str, list[str]]] = []
        driver = _StartupDriver([_window(1, "pwsh in workspace")])

        code, output = self._invoke(
            monkeypatch,
            driver,
            [_session("a"), _session("b")],
            "--config",
            str(env),
            "--max-tabs",
            "1",
            locked=True,
            notes=notes,
        )

        assert code == EXIT_DEFERRED, output
        assert notes == []

    def test_a_print_run_opens_no_note(self, env: Path, monkeypatch: pytest.MonkeyPatch):
        notes: list[tuple[str, list[str]]] = []
        driver = _StartupDriver([_window(1, "pwsh in workspace")])
        driver.drives_terminal = False

        self._invoke(
            monkeypatch, driver, [_session("a"), _session("b")], "--config", str(env), "--max-tabs", "1", notes=notes
        )

        assert notes == []

    def test_missing_screens_defer_without_typing(self, env: Path, tmp_path: Path, monkeypatch: pytest.MonkeyPatch):
        driver = _StartupDriver([_window(1, "pwsh in workspace")])

        code, output = self._invoke(
            monkeypatch, driver, [_session("a")], "--config", str(env), shortfall=MonitorShortfall(required=3, live=1)
        )

        assert code == EXIT_DEFERRED, output
        assert "the plan needs 3 screens, only 1 detected" in output
        assert driver.resumed == []
        assert driver.fresh == []

    def test_a_failed_resume_exits_1(self, env: Path, monkeypatch: pytest.MonkeyPatch):
        driver = _StartupDriver([_window(1, "pwsh in workspace")], failing=frozenset({"a"}))

        code, output = self._invoke(monkeypatch, driver, [_session("a")], "--config", str(env))

        assert code == 1, output
        assert "resumed 0, started 0 fresh, skipped 0, failed 1" in output

    def test_an_unreadable_plan_still_resumes(self, tmp_path: Path, monkeypatch: pytest.MonkeyPatch):
        monkeypatch.setenv("DESKTOP_MANAGER_LOG_DIR", str(tmp_path / "logs"))
        driver = _StartupDriver([_window(1, "pwsh")])

        code, output = self._invoke(monkeypatch, driver, [_session("a")], "--config", str(tmp_path / "missing.json"))

        assert code == 0, output
        assert "the plan did not load" in output
        assert [s.session.session_id for s in driver.resumed] == ["a"]

    def test_a_locked_workstation_defers_with_exit_4_and_types_nothing(
        self, env: Path, tmp_path: Path, monkeypatch: pytest.MonkeyPatch
    ):
        driver = _StartupDriver([_window(1, "pwsh in workspace"), _window(2, "pwsh in workspace")])

        code, output = self._invoke(monkeypatch, driver, [_session("a")], "--config", str(env), locked=True)

        assert code == 4, output
        assert "deferred: the workstation is locked; nothing typed" in output
        assert driver.resumed == []
        assert driver.fresh == []

    def test_looks_back_a_week_and_resumes_clean_exits_by_default(self, env: Path, monkeypatch: pytest.MonkeyPatch):
        driver = _StartupDriver([_window(1, "pwsh in workspace")])
        windows: list[str | timedelta] = []
        sessions = [_session("a", exit_kind="exited"), _session("b", exit_kind="crashed")]

        code, output = self._invoke(monkeypatch, driver, sessions, "--config", str(env), windows=windows)

        assert code == 0, output
        assert windows == [timedelta(days=7)]
        assert sorted(s.session.session_id for s in driver.resumed) == ["a", "b"]

    def test_an_explicit_since_sets_the_window(self, env: Path, monkeypatch: pytest.MonkeyPatch):
        windows: list[str | timedelta] = []

        code, output = self._invoke(
            monkeypatch, _StartupDriver([]), [], "--config", str(env), "--since", "2h", windows=windows
        )

        assert code == 0, output
        assert windows == [timedelta(hours=2)]
