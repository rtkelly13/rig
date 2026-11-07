"""The session-end ledger, agent-chosen groups (`plan`), the plan file, and `apply`'s parallel start-up.

`execute` runs against a fake terminal whose `type_line` registers the session the
way Claude does when it comes up, so the start-up loop is exercised without a screen.
"""

from __future__ import annotations

import json
import time
from collections.abc import Sequence
from datetime import UTC, datetime, timedelta
from pathlib import Path

import pytest
from typer.testing import CliRunner

from desktop_manager.claude_sessions import backends, cli, recover
from desktop_manager.claude_sessions.discovery import Discovery, LiveSession, SessionRecord, discover
from desktop_manager.claude_sessions.outcomes import Outcome
from desktop_manager.claude_sessions.plan import (
    Group,
    Placement,
    PlanError,
    Slot,
    parse_groups,
    plan_from_json,
    plan_groups,
    plan_to_json,
    stale_slots,
)
from desktop_manager.claude_sessions.terminal import TerminalTab, TerminalWindow
from desktop_manager.claude_sessions.wt_uia import TerminalAutomationError

pytestmark = pytest.mark.ci

NOW = datetime.now(UTC).replace(microsecond=0)


def _user(text: str, cwd: str, at: datetime) -> dict[str, object]:
    return {
        "type": "user",
        "entrypoint": "cli",
        "cwd": cwd,
        "timestamp": at.isoformat().replace("+00:00", "Z"),
        "message": {"role": "user", "content": text},
    }


def _write_transcript(home: Path, session_id: str, at: datetime) -> None:
    path = home / "projects" / "C--code" / f"{session_id}.jsonl"
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(_user("hi", str(home), at)) + "\n", encoding="utf-8")


def _end(home: Path, session_id: str, reason: str, at: datetime) -> None:
    ledger = home / "session-ends"
    ledger.mkdir(parents=True, exist_ok=True)
    (ledger / f"{session_id}.json").write_text(
        json.dumps({"session_id": session_id, "reason": reason, "ended_at": at.isoformat()}), encoding="utf-8"
    )


def _session(session_id: str) -> SessionRecord:
    return SessionRecord(
        session_id=session_id,
        transcript=Path(f"{session_id}.jsonl"),
        start_cwd="/work",
        last_cwd="/work",
        title=session_id,
        first_prompt=None,
        prompt_count=1,
        last_activity=NOW,
        size_bytes=0,
        exit_kind="crashed",
        interactive=True,
    )


def _window(hwnd: int, *titles: str) -> TerminalWindow:
    return TerminalWindow(
        hwnd=hwnd,
        title=titles[0],
        tabs=tuple(TerminalTab(i, title, i == 0, (hwnd, i)) for i, title in enumerate(titles)),
    )


class TestSessionEndLedger:
    @pytest.fixture(autouse=True)
    def _default_ledger(self, monkeypatch: pytest.MonkeyPatch) -> None:
        monkeypatch.delenv("CLAUDE_SESSION_LEDGER_DIR", raising=False)

    def test_cleared_sessions_are_skipped_unless_asked_for(self, tmp_path: Path):
        _write_transcript(tmp_path, "cleared", NOW - timedelta(minutes=5))
        _write_transcript(tmp_path, "crashed", NOW - timedelta(minutes=5))
        _end(tmp_path, "cleared", "clear", NOW)
        since = NOW - timedelta(hours=1)

        default = discover(since=since, is_alive=lambda *_: False, home=tmp_path)
        everything = discover(since=since, is_alive=lambda *_: False, home=tmp_path, include_cleared=True)

        assert [r.session_id for r in default.candidates] == ["crashed"]
        assert {s.record.session_id: s.reason for s in default.skipped}["cleared"] == "cleared with /clear"
        assert {r.session_id: r.cleared for r in everything.candidates} == {"cleared": True, "crashed": False}

    def test_a_session_used_again_after_its_recorded_end_is_not_cleared(self, tmp_path: Path):
        _write_transcript(tmp_path, "resumed", NOW)
        _end(tmp_path, "resumed", "clear", NOW - timedelta(minutes=10))

        found = discover(since=NOW - timedelta(hours=1), is_alive=lambda *_: False, home=tmp_path)

        assert [(r.session_id, r.end_reason) for r in found.candidates] == [("resumed", None)]

    def test_other_end_reasons_are_recorded_but_not_skipped(self, tmp_path: Path):
        _write_transcript(tmp_path, "closed", NOW - timedelta(minutes=5))
        _end(tmp_path, "closed", "prompt_input_exit", NOW)

        found = discover(since=NOW - timedelta(hours=1), is_alive=lambda *_: False, home=tmp_path)

        assert [(r.session_id, r.end_reason, r.cleared) for r in found.candidates] == [
            ("closed", "prompt_input_exit", False)
        ]

    def test_the_ledger_directory_can_be_moved(self, tmp_path: Path, monkeypatch: pytest.MonkeyPatch):
        _write_transcript(tmp_path, "cleared", NOW - timedelta(minutes=5))
        _end(tmp_path / "elsewhere", "cleared", "clear", NOW)
        monkeypatch.setenv("CLAUDE_SESSION_LEDGER_DIR", str(tmp_path / "elsewhere" / "session-ends"))

        found = discover(since=NOW - timedelta(hours=1), is_alive=lambda *_: False, home=tmp_path)

        assert found.candidates == []


class TestPlanGroups:
    def test_idle_tabs_first_then_new_tabs_and_new_windows_numbered_per_group(self):
        windows = [_window(5, "pwsh", "✳ busy", "Command Prompt")]
        place = Placement(screen=2)
        groups = [
            Group((_session("a"), _session("b"), _session("c")), window=5),
            Group((_session("d"),), window=None, place=place),
            Group((_session("e"), _session("f")), window=None),
        ]

        slots = plan_groups(groups, windows)

        assert [(s.session.session_id, s.window, s.tab, s.new_window) for s in slots] == [
            ("a", 5, (5, 0), 0),
            ("b", 5, (5, 2), 0),
            ("c", 5, None, 0),
            ("d", None, None, 0),
            ("e", None, None, 1),
            ("f", None, None, 1),
        ]
        assert slots[3].describe == f"new window 1 at {place.describe}"
        assert slots[5].describe == "new window 2"

    def test_two_groups_on_one_window_never_share_an_idle_tab(self):
        groups = [Group((_session("a"),), window=5), Group((_session("b"),), window=5)]
        slots = plan_groups(groups, [_window(5, "pwsh")])
        assert [s.tab for s in slots] == [(5, 0), None]

    def test_reuse_idle_off_opens_new_tabs(self):
        slots = plan_groups([Group((_session("a"),), window=5, reuse_idle=False)], [_window(5, "pwsh")])
        assert slots[0].tab is None

    @pytest.mark.parametrize(
        ("groups", "message"),
        [
            ([Group((_session("a"),), window=5), Group((_session("a"),), window=None)], "more than one group"),
            ([Group((_session("a"),), window=9)], "window 9 is not open"),
            ([Group((_session("a"),), window=5, place=Placement(screen=1))], "only for new windows"),
        ],
    )
    def test_refuses_duplicates_closed_windows_and_moving_open_ones(self, groups: list[Group], message: str):
        with pytest.raises(PlanError, match=message):
            plan_groups(groups, [_window(5, "pwsh")])


class TestStaleSlots:
    def test_a_plan_that_still_fits_has_no_problems(self):
        slots = plan_groups([Group((_session("a"), _session("b")), window=5)], [_window(5, "pwsh")])
        assert stale_slots(slots, [_window(5, "pwsh", "✳ other")]) == []

    def test_closed_windows_and_tabs_that_closed_or_got_busy(self):
        slots = [
            Slot(_session("a"), 5, (5, 0), "pwsh"),
            Slot(_session("b"), 5, (5, 1), "Command Prompt"),
            Slot(_session("c"), 6, None),
            Slot(_session("d"), None, None),
        ]

        problems = stale_slots(slots, [_window(5, "✳ Fix the build")])

        assert problems == [
            "a: idle tab 'pwsh' in window 5 is now '✳ Fix the build'",
            "b: idle tab 'Command Prompt' in window 5 has closed",
            "c: window 6 has closed",
        ]


class TestPlanFiles:
    def _resolve(self, ids: list[str]) -> list[SessionRecord]:
        return [_session(i) for i in ids]

    def test_groups_file_reads_windows_placement_and_options(self):
        data = {
            "groups": [
                {"name": "dp", "window": 5, "sessions": ["a", "b"], "reuse_idle": False},
                {"window": "new", "sessions": ["c"], "place": {"screen": 3, "slot": 2}},
            ]
        }

        groups = parse_groups(data, self._resolve)

        assert [(g.name, g.window, g.reuse_idle, len(g.sessions)) for g in groups] == [
            ("dp", 5, False, 2),
            (None, None, True, 1),
        ]
        assert groups[1].place == Placement(screen=3, mode="halves", slot=2, orientation="vertical")

    @pytest.mark.parametrize(
        ("data", "message"),
        [
            ({}, "non-empty list"),
            ({"groups": [{"window": "x", "sessions": ["a"]}]}, "window handle"),
            ({"groups": [{"window": True, "sessions": ["a"]}]}, "window handle"),
            ({"groups": [{"window": 5, "sessions": []}]}, "non-empty list of session ids"),
            ({"groups": [{"window": "new", "sessions": ["a"], "place": {"screen": "one"}}]}, "screen"),
        ],
    )
    def test_groups_file_errors_say_where(self, data: object, message: str):
        with pytest.raises(PlanError, match=message):
            parse_groups(data, self._resolve)

    def test_plan_round_trips_through_json_re_reading_each_transcript(self):
        slots = [
            Slot(_session("a"), 5, (5, 0), "pwsh"),
            Slot(_session("b"), None, None, new_window=1, place=Placement(screen=2)),
        ]
        loaded: list[Path] = []

        def load(path: Path) -> SessionRecord:
            loaded.append(path)
            return _session(path.stem)

        again = plan_from_json(json.loads(json.dumps(plan_to_json(slots))), load)

        assert again == slots
        assert loaded == [Path("a.jsonl"), Path("b.jsonl")]

    @pytest.mark.parametrize(
        ("mutate", "message"),
        [
            (lambda plan: plan.update(version=99), "version 99"),
            (lambda plan: plan["slots"].append(dict(plan["slots"][0])), "in the plan twice"),
            (lambda plan: plan["slots"][0].update(session_id="zzz"), "is not session zzz"),
        ],
    )
    def test_plan_errors(self, mutate: object, message: str):
        plan = json.loads(json.dumps(plan_to_json([Slot(_session("a"), 5, None)])))
        mutate(plan)  # pyright: ignore[reportCallIssue]
        with pytest.raises(PlanError, match=message):
            plan_from_json(plan, lambda path: _session(path.stem))


class _FakeTerminal:
    """Opens windows and tabs, and registers a session in the live registry when its command is typed."""

    def __init__(self, sessions_dir: Path, *, never_starts: frozenset[str] = frozenset(), fail_on: str = "") -> None:
        self.sessions_dir = sessions_dir
        self.never_starts = never_starts
        self.fail_on = fail_on
        self.events: list[str] = []
        self.typed_at: dict[str, float] = {}
        self._tabs: dict[int, list[tuple[int, ...]]] = {5: [(5, 0)]}
        self._next_pid = 100

    def open_window(self) -> int:
        hwnd = 10 + len(self._tabs)
        self._tabs[hwnd] = [(hwnd, 0)]
        self.events.append(f"open window {hwnd}")
        return hwnd

    def new_tab(self, hwnd: int) -> TerminalTab:
        tab = (hwnd, len(self._tabs[hwnd]))
        self._tabs[hwnd].append(tab)
        self.events.append(f"new tab {tab}")
        return TerminalTab(len(self._tabs[hwnd]) - 1, "pwsh", True, tab)

    def selected_tab(self, hwnd: int) -> TerminalTab:
        return TerminalTab(0, "pwsh", True, self._tabs[hwnd][0])

    def tabs(self, hwnd: int) -> tuple[TerminalTab, ...]:
        return tuple(TerminalTab(i, "pwsh", False, tab) for i, tab in enumerate(self._tabs[hwnd]))

    def select_tab(self, hwnd: int, runtime_id: tuple[int, ...]) -> TerminalTab:
        return TerminalTab(0, "pwsh", True, runtime_id)

    def wait_for_prompt(self, hwnd: int) -> None:
        pass

    def selected_text(self, hwnd: int) -> str:
        return "PS C:\\work>"

    def type_line(self, hwnd: int, runtime_id: tuple[int, ...], text: str) -> None:
        session_id = text.split("--resume ")[1].split(maxsplit=1)[0]
        if session_id == self.fail_on:
            raise TerminalAutomationError("tab went away")
        self.events.append(f"type {session_id} into {runtime_id}")
        self.typed_at[session_id] = time.monotonic()
        if session_id not in self.never_starts:
            _register_live(self.sessions_dir, self._next_pid, session_id)
            self._next_pid += 1


def _register_live(sessions_dir: Path, pid: int, session_id: str) -> None:
    sessions_dir.mkdir(parents=True, exist_ok=True)
    (sessions_dir / f"{pid}.json").write_text(
        json.dumps({"pid": pid, "sessionId": session_id, "procStart": "1"}), encoding="utf-8"
    )


class TestExecute:
    @pytest.fixture(autouse=True)
    def _no_focus_changes(self, monkeypatch: pytest.MonkeyPatch) -> None:
        monkeypatch.setattr(recover, "foreground_window", lambda: 0)
        monkeypatch.setattr(recover, "restore_foreground", lambda _: None)

    def _run(self, terminal: _FakeTerminal, slots: Sequence[Slot], **kwargs: object) -> list[Outcome]:
        placed: list[tuple[int, Placement]] = []
        outcomes = recover.execute(
            slots,
            uia=terminal,  # pyright: ignore[reportArgumentType]
            sessions_dir=terminal.sessions_dir,
            is_alive=lambda *_: True,
            poll_seconds=0.01,
            place=lambda hwnd, place: placed.append((hwnd, place)),
            **kwargs,  # pyright: ignore[reportArgumentType]
        )
        terminal.events.extend(f"place {hwnd} at {place.describe}" for hwnd, place in placed)
        return outcomes

    def test_opens_every_tab_before_typing_and_verifies_every_session(self, tmp_path: Path):
        terminal = _FakeTerminal(tmp_path / "sessions")
        slots = [
            Slot(_session("a"), 5, (5, 0), "pwsh"),
            Slot(_session("b"), 5, None),
            Slot(_session("c"), None, None, new_window=0, place=Placement(screen=2)),
            Slot(_session("d"), None, None, new_window=0, place=Placement(screen=2)),
        ]

        outcomes = self._run(terminal, slots)

        assert [(o.slot.session.session_id, o.ok, o.detail) for o in outcomes] == [
            ("a", True, "running"),
            ("b", True, "running"),
            ("c", True, "running"),
            ("d", True, "running"),
        ]
        first_type = next(i for i, e in enumerate(terminal.events) if e.startswith("type"))
        assert terminal.events[:first_type] == ["new tab (5, 1)", "open window 11", "new tab (11, 1)"]
        assert "type c into (11, 0)" in terminal.events
        assert "type d into (11, 1)" in terminal.events

    def test_at_most_max_starting_sessions_start_at_once(self, tmp_path: Path):
        terminal = _FakeTerminal(tmp_path / "sessions", never_starts=frozenset({"a", "b"}))
        slots = [Slot(_session(s), 5, None) for s in "abc"]

        outcomes = self._run(terminal, slots, max_starting=2, verify_timeout=0.3)

        assert [(o.slot.session.session_id, o.ok) for o in outcomes] == [("a", False), ("b", False), ("c", True)]
        assert outcomes[0].detail.startswith("typed, but not running")
        assert terminal.typed_at["c"] - terminal.typed_at["a"] >= 0.3

    def test_a_session_running_by_the_time_its_turn_comes_is_left_alone(self, tmp_path: Path):
        terminal = _FakeTerminal(tmp_path / "sessions")
        _register_live(terminal.sessions_dir, 42, "b")

        outcomes = self._run(terminal, [Slot(_session("a"), 5, None), Slot(_session("b"), 5, None)])

        assert [(o.ok, o.detail) for o in outcomes] == [
            (True, "running"),
            (False, "already running (pid 42); left alone"),
        ]
        assert not any(e.startswith("type b") for e in terminal.events)

    def test_an_automation_failure_stops_launching_but_typed_sessions_are_still_verified(self, tmp_path: Path):
        terminal = _FakeTerminal(tmp_path / "sessions", fail_on="b")

        outcomes = self._run(terminal, [Slot(_session(s), 5, None) for s in "abc"])

        assert [(o.slot.session.session_id, o.ok, o.detail) for o in outcomes] == [
            ("a", True, "running"),
            ("b", False, "tab went away"),
        ]


class _FakeDriver:
    drives_terminal = True

    def __init__(self, windows: list[TerminalWindow]) -> None:
        self.windows = windows
        self.resumed: list[Slot] = []

    def list_windows(self) -> list[TerminalWindow]:
        return self.windows

    def resume(self, slots: Sequence[Slot], **_: object) -> list[Outcome]:
        self.resumed.extend(slots)
        return [Outcome(slot, ok=True, detail="running", pid=1) for slot in slots]


class TestPlanApplyCommands:
    @pytest.fixture
    def driver(self, monkeypatch: pytest.MonkeyPatch) -> _FakeDriver:
        sessions = {s: _session(s) for s in ("aaaaaaaa-1", "bbbbbbbb-2", "cccccccc-3")}
        discovery = Discovery(candidates=list(sessions.values()), live={}, skipped=[])
        driver = _FakeDriver([_window(5, "pwsh", "✳ busy")])
        monkeypatch.setattr(cli, "_discover", lambda *_, **__: discovery)
        monkeypatch.setattr(cli, "read_transcript", lambda path: sessions[path.stem])
        monkeypatch.setattr(cli, "read_live_sessions", lambda *_: {})
        monkeypatch.setattr(backends, "select_backend", lambda print_only: driver)  # noqa: ARG005
        return driver

    def _plan(self, tmp_path: Path, groups: object) -> Path:
        groups_file, plan_file = tmp_path / "groups.json", tmp_path / "plan.json"
        groups_file.write_text(json.dumps(groups), encoding="utf-8")
        result = CliRunner().invoke(cli.claude_sessions_app, ["plan", str(groups_file), "--out", str(plan_file)])
        assert result.exit_code == 0, result.output
        return plan_file

    def test_plan_then_apply_resumes_each_group_where_it_was_put(self, tmp_path: Path, driver: _FakeDriver):
        plan_file = self._plan(
            tmp_path,
            {
                "groups": [
                    {"name": "here", "window": 5, "sessions": ["aaaa", "bbbb"]},
                    {"window": "new", "sessions": ["cccc"], "place": {"screen": 2}},
                ]
            },
        )
        plan = json.loads(plan_file.read_text(encoding="utf-8"))
        assert [(s["session_id"], s["window"], s["tab"]) for s in plan["slots"]] == [
            ("aaaaaaaa-1", 5, [5, 0]),
            ("bbbbbbbb-2", 5, None),
            ("cccccccc-3", None, None),
        ]

        result = CliRunner().invoke(cli.claude_sessions_app, ["apply", str(plan_file)])

        assert result.exit_code == 0, result.output
        assert "3/3 running" in result.output
        assert [s.session.session_id for s in driver.resumed] == ["aaaaaaaa-1", "bbbbbbbb-2", "cccccccc-3"]

    def test_plan_refuses_a_session_named_twice(self, tmp_path: Path, driver: _FakeDriver):  # noqa: ARG002
        groups_file = tmp_path / "groups.json"
        groups_file.write_text(json.dumps({"groups": [{"window": 5, "sessions": ["aaaa", "aaaaaaaa-1"]}]}))

        result = CliRunner().invoke(cli.claude_sessions_app, ["plan", str(groups_file)])

        assert result.exit_code == 2
        assert "named twice" in result.output

    def test_apply_stops_before_typing_when_the_terminals_changed(self, tmp_path: Path, driver: _FakeDriver):
        plan_file = self._plan(tmp_path, {"groups": [{"window": 5, "sessions": ["aaaa"]}]})
        driver.windows = [_window(5, "✳ someone else's session")]

        result = CliRunner().invoke(cli.claude_sessions_app, ["apply", str(plan_file)])

        assert result.exit_code == 1
        assert "nothing typed" in result.output
        assert "is now '✳ someone else's session'" in result.output
        assert driver.resumed == []

    def test_apply_leaves_sessions_that_are_running_now_alone(
        self, tmp_path: Path, driver: _FakeDriver, monkeypatch: pytest.MonkeyPatch
    ):
        plan_file = self._plan(tmp_path, {"groups": [{"window": "new", "sessions": ["aaaa", "bbbb"]}]})
        live = {"aaaaaaaa-1": LiveSession(pid=77, session_id="aaaaaaaa-1", cwd=None, status=None)}
        monkeypatch.setattr(cli, "read_live_sessions", lambda *_: live)

        result = CliRunner().invoke(cli.claude_sessions_app, ["apply", str(plan_file)])

        assert result.exit_code == 0, result.output
        assert "aaaaaaaa is running now (pid 77); left alone" in result.output
        assert [s.session.session_id for s in driver.resumed] == ["bbbbbbbb-2"]
