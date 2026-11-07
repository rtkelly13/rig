"""Tests for `estate wrapup`'s rolling send: a few asks at a time, the next as each session finishes."""

from __future__ import annotations

import datetime as dt
import json
from collections.abc import Callable
from dataclasses import replace
from pathlib import Path
from typing import override

import pytest
from typer.testing import CliRunner

from rig import proc
from rig.estate import wrapup_watch
from rig.estate.cli import app
from rig.estate.wrapup import MARKER, OrcaWorktree, Row, Status
from tests import test_estate_wrapup
from tests.test_estate_wrapup import (
    NOW,
    FakeClock,
    FakeOrca,
    MemoryStore,
    make_agent,
    make_terminal,
    make_worktree,
    plan_rows,
)

pytestmark = pytest.mark.ci

#: The estate fixture: one idle session and one answered subtask, Orca and git faked.
fake_estate = test_estate_wrapup.fake_estate

WAIT = dt.timedelta(minutes=1)

#: What `orca worktree ps` shows for one pane at a moment: (state, prompt, reply), or None for "unchanged".
type PaneView = Callable[[float], tuple[str, str, str | None] | None]


class _Orca(FakeOrca):
    """Sends succeed; `worktree ps` reports each pane through its view, seconds after the run began."""

    def __init__(self, clock: FakeClock, views: dict[tuple[str, str], PaneView]) -> None:
        super().__init__()
        self.clock: FakeClock = clock
        self.views: dict[tuple[str, str], PaneView] = views
        self.sent: list[str] = []
        self.down: bool = False

    @override
    def __call__(self, args: list[str]) -> proc.Ran:
        self.calls.append(args)
        if args[:2] == ["terminal", "send"]:
            self.sent.append(args[3])
            return proc.Ran(0, '{"ok": true}', "")
        if self.down:
            return proc.Ran(1, "", "orca is not running")
        worktrees: list[dict[str, object]] = []
        for (wid, pane), view in self.views.items():
            seen = view(self.clock.elapsed)
            if seen is None:
                continue
            state, prompt, reply = seen
            started = int((NOW + dt.timedelta(seconds=self.clock.elapsed)).timestamp() * 1000)
            agent: dict[str, object] = {
                "paneKey": pane,
                "state": state,
                "agentType": "claude",
                "prompt": prompt,
                "lastAssistantMessage": reply,
                "stateStartedAt": started,
            }
            if state == "waiting":
                agent["programStatus"] = {"state": "blocked", "kind": "permission", "message": "allow gh pr merge?"}
            worktrees.append({"worktreeId": wid, "repoId": "rig-id", "path": f"/w/{wid}", "agents": [agent]})
        return proc.Ran(0, json.dumps({"ok": True, "result": {"worktrees": worktrees}}), "")


def _answers_after(seconds: float, reply: str) -> PaneView:
    def view(elapsed: float) -> tuple[str, str, str | None] | None:
        return ("done", f"{MARKER} Done?", reply) if elapsed >= seconds else None

    return view


def _sessions(count: int) -> tuple[list[Row], dict[str, OrcaWorktree]]:
    worktrees = [make_worktree(f"w{n}", agents=(make_agent(f"t{n}:l{n}"),)) for n in range(count)]
    rows = plan_rows(*worktrees, terminals=tuple(make_terminal(f"t{n}:l{n}") for n in range(count)))
    return rows, {w.worktree_id: w for w in worktrees}


def _run(
    rows: list[Row], worktrees: dict[str, OrcaWorktree], orca: _Orca, *, width: int, store: MemoryStore | None = None
) -> tuple[list[Row], list[str]]:
    said: list[str] = []

    def progress(row: Row, message: str) -> None:
        said.append(f"{row.worktree}: {message}")

    out = wrapup_watch.ask_rolling(
        rows,
        "Done?",
        worktrees,
        max_in_flight=width,
        wait=WAIT,
        progress=progress,
        run=orca,
        clock=orca.clock.clock,
        store=store or MemoryStore(),
    )
    return out, said


def test_the_next_session_is_asked_as_soon_as_one_answers() -> None:
    rows, worktrees = _sessions(3)
    clock = FakeClock()
    orca = _Orca(
        clock,
        {
            ("w0", "t0:l0"): _answers_after(10, "WRAPUP: done"),
            ("w1", "t1:l1"): _answers_after(20, "WRAPUP: pending: merge PR #9"),
            ("w2", "t2:l2"): _answers_after(30, "**WRAPUP: done**"),
        },
    )
    store = MemoryStore()
    out, said = _run(rows, worktrees, orca, width=2, store=store)
    assert [r.status for r in out] == [Status.ANSWERED] * 3
    assert [r.verdict.detail if r.verdict else None for r in out] == ["", "merge PR #9", ""]
    # w2 waited for a free slot: it was asked only once w0 had answered.
    assert said[:3] == ["w0: asked", "w1: asked", "w0: answered: done"]
    assert said[3] == "w2: asked"
    assert clock.elapsed < 60
    saved = store.files[Path(rows[1].path)]
    assert [a.pane for a in saved.asks] == ["t1:l1"]
    assert saved.answers[0].asked_at == saved.asks[0].at
    assert saved.answers[0].detail == "merge PR #9"


def test_a_reply_from_before_the_send_is_not_the_answer() -> None:
    rows, worktrees = _sessions(1)
    clock = FakeClock()
    stale_ms = int((NOW - dt.timedelta(minutes=5)).timestamp() * 1000)

    class _Stale(_Orca):
        @override
        def __call__(self, args: list[str]) -> proc.Ran:
            ran = super().__call__(args)
            if args[:2] == ["worktree", "ps"]:
                return proc.Ran(0, ran.out.replace('"stateStartedAt": ', f'"stateStartedAt": {stale_ms}, "x": '), "")
            return ran

    orca = _Stale(clock, {("w0", "t0:l0"): _answers_after(0, "WRAPUP: done (old)")})
    out, said = _run(rows, worktrees, orca, width=1)
    assert out[0].status is Status.SENT
    assert out[0].note == "no answer within 1m; the next run reads it"
    assert said[-1] == "w0: no reply within 1m, moving on"
    assert clock.elapsed >= 60


def test_a_session_waiting_on_its_owner_frees_its_slot() -> None:
    rows, worktrees = _sessions(2)
    clock = FakeClock()

    def asks_permission(elapsed: float) -> tuple[str, str, str | None] | None:
        return ("waiting", f"{MARKER} Done?", None) if elapsed >= 5 else None

    orca = _Orca(clock, {("w0", "t0:l0"): asks_permission, ("w1", "t1:l1"): _answers_after(10, "WRAPUP: done")})
    out, said = _run(rows, worktrees, orca, width=1)
    assert out[0].status is Status.WAITING
    assert out[0].note == "allow gh pr merge?"
    assert "w0: waiting on you: allow gh pr merge?" in said
    assert out[1].status is Status.ANSWERED
    assert clock.elapsed < 60


def test_an_idle_reply_without_the_marker_or_a_verdict_line() -> None:
    rows, worktrees = _sessions(2)
    clock = FakeClock()

    def owner_typed(elapsed: float) -> tuple[str, str, str | None] | None:
        return ("done", "something else", "ok") if elapsed >= 5 else None

    orca = _Orca(clock, {("w0", "t0:l0"): owner_typed, ("w1", "t1:l1"): _answers_after(5, "All merged, nothing left.")})
    out, said = _run(rows, worktrees, orca, width=2)
    assert out[0].status is Status.SENT
    assert out[1].status is Status.ANSWERED
    assert out[1].verdict is None
    assert out[1].note == "replied without a WRAPUP line: All merged, nothing left."
    assert "w1: answered, without a WRAPUP line" in said


def test_a_parent_waits_for_its_subtasks_and_hears_their_verdicts() -> None:
    parent = make_worktree("parent", agents=(make_agent("t1:l1"),))
    kid = make_worktree("kid", parent="parent", agents=(make_agent("t2:l2"),))
    rows = plan_rows(parent, kid, terminals=(make_terminal("t1:l1"), make_terminal("t2:l2")))
    clock = FakeClock()
    orca = _Orca(
        clock,
        {
            ("kid", "t2:l2"): _answers_after(10, "WRAPUP: pending: merge PR #9"),
            ("parent", "t1:l1"): _answers_after(15, "WRAPUP: done"),
        },
    )
    out, said = _run(rows, {"parent": parent, "kid": kid}, orca, width=5)
    assert said[:3] == ["kid: asked", "kid: answered: pending: merge PR #9", "parent: asked"]
    assert out[0].status is Status.ANSWERED
    sends = [c for c in orca.calls if c[:2] == ["terminal", "send"]]
    assert sends[1][3] == "term_t1"


def test_a_parent_with_a_template_naming_children_hears_the_fresh_verdict() -> None:
    parent = make_worktree("parent", agents=(make_agent("t1:l1"),))
    kid = make_worktree("kid", parent="parent", agents=(make_agent("t2:l2"),))
    rows = plan_rows(parent, kid, terminals=(make_terminal("t1:l1"), make_terminal("t2:l2")))
    clock = FakeClock()
    orca = _Orca(
        clock,
        {
            ("kid", "t2:l2"): _answers_after(10, "WRAPUP: pending: merge PR #9"),
            ("parent", "t1:l1"): _answers_after(15, "WRAPUP: done"),
        },
    )
    _ = wrapup_watch.ask_rolling(
        rows,
        "Kids: $children",
        {"parent": parent, "kid": kid},
        max_in_flight=5,
        wait=WAIT,
        run=orca,
        clock=clock.clock,
        store=MemoryStore(),
    )
    parent_text = [c for c in orca.calls if c[:2] == ["terminal", "send"]][1][5]
    assert "pending: merge PR #9" in parent_text


def test_orca_going_quiet_mid_run_times_out_rather_than_failing() -> None:
    rows, worktrees = _sessions(1)
    clock = FakeClock()
    orca = _Orca(clock, {})
    orca.down = True
    out, _ = _run(rows, worktrees, orca, width=1)
    assert out[0].status is Status.SENT
    assert out[0].note.startswith("no answer within 1m")


def test_failed_sends_do_not_hold_a_slot() -> None:
    rows, worktrees = _sessions(2)
    rows[0] = replace(rows[0], terminal=None)
    clock = FakeClock()

    class _Refuses(_Orca):
        @override
        def __call__(self, args: list[str]) -> proc.Ran:
            if args[:2] == ["terminal", "send"] and args[3] == "term_t1":
                return proc.Ran(1, '{"ok": false, "error": {"message": "pane closed"}}', "")
            return super().__call__(args)

    orca = _Refuses(clock, {})
    out, said = _run(rows, worktrees, orca, width=1)
    assert [r.status for r in out] == [Status.SEND_FAILED, Status.SEND_FAILED]
    assert [r.note for r in out] == ["no terminal", "pane closed"]
    assert said == ["w0: send failed: no terminal", "w1: send failed: pane closed"]
    assert clock.elapsed == 0


def test_an_interrupt_leaves_the_unasked_deferred() -> None:
    rows, worktrees = _sessions(2)
    clock = FakeClock()

    def interrupt(_seconds: float) -> None:
        raise KeyboardInterrupt

    orca = _Orca(clock, {})
    stopping = wrapup_watch.Clock(monotonic=clock.clock.monotonic, sleep=interrupt, now=clock.clock.now)
    out = wrapup_watch.ask_rolling(
        rows, "Done?", worktrees, max_in_flight=1, wait=WAIT, run=orca, clock=stopping, store=MemoryStore()
    )
    assert [r.status for r in out] == [Status.SENT, Status.DEFERRED]
    assert out[1].note == "interrupted before it was asked"


def test_waits_that_are_not_whole_minutes_read_in_seconds() -> None:
    rows, worktrees = _sessions(1)
    clock = FakeClock()
    out = wrapup_watch.ask_rolling(
        rows,
        "Done?",
        worktrees,
        max_in_flight=1,
        wait=dt.timedelta(seconds=45),
        run=_Orca(clock, {}),
        clock=clock.clock,
        store=MemoryStore(),
    )
    assert out[0].note == "no answer within 45s; the next run reads it"


# ---------------------------------------------------------------- the command

runner = CliRunner()


@pytest.mark.usefixtures("fake_estate")
def test_wait_zero_keeps_the_per_run_cap(tmp_path: Path) -> None:
    result = runner.invoke(
        app,
        ["wrapup", "--personal", "--all", "--max", "0", "--wait", "0", "--template", str(tmp_path / "t.md"), "--json"],
    )
    assert result.exit_code == 0, result.output
    assert '"status":"deferred"' in result.stdout


def test_a_rolling_preview_says_how_it_would_ask(fake_estate: FakeOrca, tmp_path: Path) -> None:
    result = runner.invoke(app, ["wrapup", "--personal", "--all", "--max", "0", "--template", str(tmp_path / "t.md")])
    assert result.exit_code == 0, result.output
    assert "--apply asks these 1, 1 at a time, waiting up to 10m each" in " ".join(result.stdout.split())
    assert fake_estate.calls == []


@pytest.mark.usefixtures("fake_estate")
def test_a_rolling_apply_reports_progress_on_stderr(tmp_path: Path) -> None:
    result = runner.invoke(
        app,
        [
            "wrapup",
            "--personal",
            "--all",
            "--apply",
            "--follow",
            "--wait",
            "1m",
            "--template",
            str(tmp_path / "t.md"),
            "--json",
        ],
    )
    assert result.exit_code == 0, result.output
    assert "rig/w1: asked" in result.stderr
    assert "rig/w1: no reply within 1m, moving on" in result.stderr
    assert result.stdout.startswith("[")
