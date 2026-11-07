"""`estate wrapup report` and the background follower: agents record their own verdicts, at their own pace."""

from __future__ import annotations

import datetime as dt
import re
import sys
from dataclasses import replace
from pathlib import Path

import pytest
from typer.testing import CliRunner

from rig import tracing
from rig.estate import wrapup, wrapup_follow, wrapup_record, wrapup_report, wrapup_watch
from rig.estate.cli import app
from rig.estate.wrapup import MARKER, Status
from rig.estate.wrapup_state import AnswerRecord, AskRecord, WrapupState
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
from tests.test_estate_wrapup_watch import _Orca, _sessions

pytestmark = pytest.mark.ci

fake_estate = test_estate_wrapup.fake_estate
runner = CliRunner()

_ANSI = re.compile(r"\x1b\[[0-9;]*m")


def _plain(text: str) -> str:
    """CLI output as words: colour codes and Rich's box drawing gone, wrapping undone.

    GitHub Actions makes Rich colour and box its error panel, splitting a message across rows."""
    return " ".join(_ANSI.sub("", text).translate(str.maketrans("", "", "│╭╮╰╯─")).split())


#: Captured before the autouse guard replaces it, to test the real thing with the spawn faked.
_real_start = wrapup_follow.start

WORKTREE = Path("/w/report")


def _ask(at: str, ask_id: str, pane: str = "t1:l1") -> AskRecord:
    return AskRecord(at=at, pane=pane, session="s", head="abc", facts="clean", template_sha="0" * 12, id=ask_id)


FIRST = _ask("2026-11-07T11:00:00.000+00:00", "aaaa1111")
SECOND = _ask("2026-11-07T11:30:00.000+00:00", "bbbb2222")


def _store(*asks: AskRecord, answers: tuple[AnswerRecord, ...] = ()) -> MemoryStore:
    store = MemoryStore()
    store.files[WORKTREE] = WrapupState(worktree_id="w", branch="b", asks=asks, answers=answers)
    return store


def _report(store: MemoryStore, **kwargs: object) -> wrapup_report.Reported:
    args: dict[str, object] = {"ask_id": "", "done": True, "pending": "", "todo": (), **kwargs}
    return wrapup_report.report(
        WORKTREE,
        ask_id=str(args["ask_id"]),
        done=bool(args["done"]),
        pending=str(args["pending"]),
        todo=tuple(str(t) for t in args["todo"]) if isinstance(args["todo"], tuple) else (),
        now=NOW,
        head="f00d",
        store=store,
    )


# ---------------------------------------------------------------- the command line in the prompt


def test_the_report_command_is_quoted_and_names_the_ask() -> None:
    command = wrapup_report.report_command("e5e7d88c", "/Users/me/Library/Application Support/rig/bin/rig")
    assert command == "'/Users/me/Library/Application Support/rig/bin/rig' estate wrapup report --ask e5e7d88c"
    assert wrapup_report.report_command("", "rig") == "rig estate wrapup report"


def test_the_report_command_uses_the_rig_beside_this_interpreter() -> None:
    found = wrapup_report.rig_executable()
    beside = Path(sys.executable).parent
    assert found == "rig" or Path(found).parent == beside


def test_the_prompt_carries_the_report_command_with_this_runs_ask_id() -> None:
    rows, worktrees = _sessions(1)
    row = replace(rows[0], ask_id="cafe0001")
    text = wrapup.render_prompt("Run $report --done", row, rows, worktrees)
    assert text.startswith(f"{MARKER} Run ")
    assert text.endswith("estate wrapup report --ask cafe0001 --done")


# ---------------------------------------------------------------- recording a report


def test_a_report_answers_the_named_ask() -> None:
    store = _store(FIRST, SECOND)
    result = _report(store, ask_id="aaaa1111", done=False, pending="merge #9", todo=("merge #9", "close #4"))
    assert result.answer is not None
    saved = store.files[WORKTREE].answers[-1]
    assert (saved.asked_at, saved.pane, saved.verdict, saved.detail) == (FIRST.at, "t1:l1", "pending", "merge #9")
    assert (saved.todo, saved.via, saved.head) == (("merge #9", "close #4"), "report", "f00d")


def test_without_an_id_the_newest_unanswered_ask_is_answered() -> None:
    answered = AnswerRecord(at=SECOND.at, pane="t1:l1", verdict="done", detail="", head="", asked_at=SECOND.at)
    store = _store(FIRST, SECOND, answers=(answered,))
    assert _report(store).answer is not None
    assert store.files[WORKTREE].answers[-1].asked_at == FIRST.at


def test_without_an_id_and_everything_answered_the_newest_ask_is_answered_again() -> None:
    answered = AnswerRecord(at=SECOND.at, pane="t1:l1", verdict="done", detail="", head="", asked_at=SECOND.at)
    store = _store(SECOND, answers=(answered,))
    assert _report(store, done=False, todo=("one", "two")).answer is not None
    assert store.files[WORKTREE].answers[-1].detail == "one; two"


def test_nothing_is_written_without_an_ask_to_answer() -> None:
    assert _report(MemoryStore()).error == "no wrap-up ask is recorded in this worktree"
    assert _report(_store(FIRST), ask_id="nope").error == "no ask nope is recorded in this worktree"
    assert _report(MemoryStore(refuse="nope")).answer is None


def test_a_refused_write_is_the_error() -> None:
    store = _store(FIRST)
    store.refuse = "temp/.rig/ is not gitignored here"
    assert _report(store).error == "temp/.rig/ is not gitignored here"


# ---------------------------------------------------------------- the report subcommand


@pytest.fixture
def report_store(monkeypatch: pytest.MonkeyPatch) -> MemoryStore:
    store = _store(FIRST)
    monkeypatch.setattr(wrapup_report, "default_store", lambda: store)
    monkeypatch.setattr(wrapup_report, "worktree_root", lambda _cwd: WORKTREE)
    return store


def test_the_report_subcommand_records_a_pending_verdict(report_store: MemoryStore) -> None:
    result = runner.invoke(
        app, ["wrapup", "report", "--ask", "aaaa1111", "--pending", "merge #9", "--todo", "merge #9"]
    )
    assert result.exit_code == 0, result.output
    assert "Recorded for report: pending: merge #9" in result.stdout
    assert report_store.files[WORKTREE].answers[-1].todo == ("merge #9",)


@pytest.mark.usefixtures("report_store")
@pytest.mark.parametrize(
    ("args", "message"),
    [
        (["--done", "--pending", "x"], "exactly one of --done or --pending"),
        ([], "exactly one of --done or --pending"),
        (["--pending", " "], "--pending needs a summary"),
    ],
)
def test_the_report_subcommand_needs_one_verdict(args: list[str], message: str) -> None:
    result = runner.invoke(app, ["wrapup", "report", *args])
    assert result.exit_code == 2
    assert message in _plain(result.output)


@pytest.mark.usefixtures("report_store")
def test_an_unknown_ask_points_the_agent_at_the_reply_fallback() -> None:
    result = runner.invoke(app, ["wrapup", "report", "--ask", "zzzz", "--done"])
    assert result.exit_code == 1
    assert "Reply with the WRAPUP line" in " ".join(result.stderr.split())


def test_outside_a_worktree_nothing_is_recorded(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setattr(wrapup_report, "worktree_root", lambda _cwd: None)
    result = runner.invoke(app, ["wrapup", "report", "--done"])
    assert result.exit_code == 1
    assert "Not inside a git worktree" in result.stderr


# ---------------------------------------------------------------- planning and recording around a report


def test_a_reported_verdict_counts_as_answered_while_the_agent_finishes_its_turn() -> None:
    worktree = make_worktree("w", agents=(make_agent("t1:l1", state="working", idle=1, prompt=f"{MARKER} x"),))
    ask = _ask((NOW - dt.timedelta(minutes=5)).isoformat(timespec="milliseconds"), "aaaa1111")
    answer = AnswerRecord(
        at=NOW.isoformat(), pane="t1:l1", verdict="pending", detail="merge #9", head="", asked_at=ask.at, via="report"
    )
    store = MemoryStore()
    store.files[Path(worktree.path)] = WrapupState(asks=(ask,), answers=(answer,))
    row = plan_rows(worktree, terminals=(make_terminal("t1:l1"),), store=store)[0]
    assert row.status is Status.ANSWERED
    assert row.verdict is not None and row.verdict.detail == "merge #9"


def test_an_answered_ask_is_never_unconfirmed() -> None:
    worktree = make_worktree("w", agents=(make_agent("t1:l1", idle=120, prompt="carry on"),))
    ask = _ask((NOW - dt.timedelta(minutes=60)).isoformat(timespec="milliseconds"), "aaaa1111")
    answer = AnswerRecord(at=NOW.isoformat(), pane="t1:l1", verdict="done", detail="", head="", asked_at=ask.at)
    store = MemoryStore()
    store.files[Path(worktree.path)] = WrapupState(asks=(ask,), answers=(answer,))
    row = plan_rows(worktree, terminals=(make_terminal("t1:l1"),), store=store)[0]
    assert row.status is Status.ASK


def test_a_reply_line_is_not_recorded_over_the_agents_own_report() -> None:
    worktree = make_worktree("w", agents=(make_agent("t1:l1", prompt=f"{MARKER} x", reply="WRAPUP: done"),))
    ask = _ask((NOW - dt.timedelta(minutes=5)).isoformat(timespec="milliseconds"), "aaaa1111")
    store = MemoryStore()
    store.files[Path(worktree.path)] = WrapupState(asks=(ask,))
    rows = plan_rows(worktree, store=store)
    assert rows[0].status is Status.ANSWERED
    # The agent reports after this run read the file, before it writes.
    late = AnswerRecord(at="x", pane="t1:l1", verdict="pending", detail="merge #9", head="", asked_at=ask.at)
    store.files[Path(worktree.path)] = store.files[Path(worktree.path)].with_answer(late)
    _ = wrapup_record.record(rows, now=NOW, store=store)
    assert [a.detail for a in store.files[Path(worktree.path)].answers] == ["merge #9"]


def test_a_reported_answer_frees_the_slot_before_orca_shows_a_reply() -> None:
    rows, worktrees = _sessions(2)
    clock = FakeClock()
    orca = _Orca(clock, {})
    store = MemoryStore()

    class Reporting(MemoryStore):
        """Each first-asked worktree reports done as soon as its ask is written."""

        def write(self, worktree: Path, state: WrapupState) -> str | None:
            if state.asks and not state.answers:
                ask = state.asks[-1]
                done = AnswerRecord(at="r", pane=ask.pane, verdict="done", detail="", head="", asked_at=ask.at)
                state = state.with_answer(replace(done, via="report"))
            return store.write(worktree, state)

        def read(self, worktree: Path) -> WrapupState | None:
            return store.read(worktree)

    said: list[str] = []
    out = wrapup_watch.ask_rolling(
        rows,
        "Run $report",
        worktrees,
        max_in_flight=1,
        wait=dt.timedelta(minutes=10),
        progress=lambda row, message: said.append(f"{row.worktree}: {message}"),
        run=orca,
        clock=clock.clock,
        store=Reporting(),
    )
    assert [r.status for r in out] == [Status.ANSWERED, Status.ANSWERED]
    assert said == ["w0: asked", "w0: answered: done", "w1: asked", "w1: answered: done"]
    assert clock.elapsed < 60
    sent_ids = [r.ask_id for r in out]
    assert all(sent_ids) and sent_ids[0] != sent_ids[1]
    texts = [call[5] for call in orca.calls if call[:2] == ["terminal", "send"]]
    assert all(f"--ask {i}" in text for i, text in zip(sent_ids, texts, strict=True))


# ---------------------------------------------------------------- --apply in the background


def test_apply_hands_the_asking_to_a_background_follower(
    fake_estate: FakeOrca, tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    started: list[list[str]] = []

    def start(args: list[str], *, now: dt.datetime) -> Path:
        del now
        started.append(args)
        return tmp_path / "follow.log"

    monkeypatch.setattr(wrapup_follow, "start", start)
    monkeypatch.setattr(wrapup_follow, "running", lambda: False)
    template = str(tmp_path / "t.md")
    result = runner.invoke(
        app, ["wrapup", "--personal", "--repo", "rig", "-w", "w1", "--children", "--apply", "--template", template]
    )
    assert result.exit_code == 0, result.output
    assert fake_estate.calls == []
    assert started == [
        [
            "--personal",
            "--repo",
            "rig",
            "--worktree",
            "w1",
            "--children",
            "--idle",
            "30m",
            "--max",
            "5",
            "--wait",
            "10m",
            "--template",
            str(Path(template).resolve()),
            "--apply",
        ]
    ]
    assert "ASKING IN THE BACKGROUND" in result.stdout
    assert "follow.log" in "".join(_ANSI.sub("", result.stderr).split())


def test_a_running_follower_is_not_doubled(
    fake_estate: FakeOrca, tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    monkeypatch.setattr(wrapup_follow, "running", lambda: True)
    result = runner.invoke(
        app, ["wrapup", "--personal", "--all", "--apply", "--json", "--template", str(tmp_path / "t.md")]
    )
    assert result.exit_code == 1
    assert "already asking" in result.stderr
    assert fake_estate.calls == []


def test_nothing_starts_in_the_background_with_nothing_to_ask(fake_estate: FakeOrca, tmp_path: Path) -> None:
    result = runner.invoke(app, ["wrapup", "--work", "--all", "--apply", "--template", str(tmp_path / "t.md")])
    assert result.exit_code == 0, result.output
    assert fake_estate.calls == []


def test_a_follower_stops_when_another_holds_the_lock(fake_estate: FakeOrca, tmp_path: Path) -> None:
    with wrapup_follow.hold() as acquired:
        assert acquired
        result = runner.invoke(
            app, ["wrapup", "--personal", "--all", "--apply", "--follow", "--template", str(tmp_path / "t.md")]
        )
    assert result.exit_code == 1
    assert "Another wrap-up follower" in result.stderr
    assert fake_estate.calls == []


def test_the_lock_is_held_by_one_follower_at_a_time(tmp_path: Path) -> None:
    lock = tmp_path / "follow.lock"
    assert not wrapup_follow.running(lock)
    with wrapup_follow.hold(lock) as first:
        assert first
        assert wrapup_follow.running(lock)
    assert not wrapup_follow.running(lock)


def test_start_runs_this_command_with_follow_logging_to_a_file(monkeypatch: pytest.MonkeyPatch) -> None:
    spawned: list[tuple[list[str], Path | None]] = []

    def spawn(args: list[str], *, scope: str, log: Path | None = None) -> int:
        del scope
        spawned.append((list(args), log))
        return 1

    monkeypatch.setattr(tracing, "spawn_detached", spawn)
    log = _real_start(["--personal", "--all", "--apply"], now=NOW)
    assert log.parent == wrapup_follow.log_dir()
    assert log.name == "follow-20261107T120000Z.log"
    argv, logged = spawned[0]
    assert argv == [sys.executable, "-m", "rig.estate.cli", "wrapup", "--personal", "--all", "--apply", "--follow"]
    assert logged == log
