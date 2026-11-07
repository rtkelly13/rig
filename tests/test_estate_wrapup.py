"""Tests for `estate wrapup`: choosing idle Orca sessions, the facts-first gate, sending, and reading verdicts."""

from __future__ import annotations

import datetime as dt
import json
import re
from dataclasses import dataclass, replace
from pathlib import Path

import pytest
from rich.console import Console
from typer.testing import CliRunner

from rig import proc, roots
from rig.estate import wrapup, wrapup_cli, wrapup_record, wrapup_watch
from rig.estate.cli import app
from rig.estate.orca_runtime import ORCA_UNAVAILABLE_EXIT, Repo
from rig.estate.wrapup import (
    MARKER,
    GitFacts,
    LinkedPr,
    Options,
    OrcaAgent,
    OrcaSnapshot,
    OrcaTerminal,
    OrcaWorktree,
    Placed,
    Row,
    Scope,
    Status,
    Target,
    WrapupError,
)
from rig.estate.wrapup_state import SCHEMA, AnswerRecord, AskRecord, FileStateStore, WrapupState
from rig.typed_json import decode, decode_json

pytestmark = pytest.mark.ci

ROOT = Path("/code")
NOW = dt.datetime(2026, 11, 7, 12, 0, tzinfo=dt.UTC)
CLEAN = GitFacts(dirty=False, unpushed=0, merged=False)


class FakeClock:
    """Time that only moves when slept through, starting at NOW."""

    def __init__(self) -> None:
        self.elapsed: float = 0.0
        self.clock: wrapup_watch.Clock = wrapup_watch.Clock(monotonic=self._monotonic, sleep=self._sleep, now=self._now)

    def _monotonic(self) -> float:
        return self.elapsed

    def _sleep(self, seconds: float) -> None:
        self.elapsed += max(seconds, 0.001)

    def _now(self) -> dt.datetime:
        return NOW + dt.timedelta(seconds=self.elapsed)


def _ms(minutes_ago: float) -> int:
    return int((NOW - dt.timedelta(minutes=minutes_ago)).timestamp() * 1000)


def make_agent(
    pane: str,
    *,
    state: str = "done",
    idle: float = 120,
    prompt: str = "do the thing",
    reply: str | None = "Did the thing.",
    agent_type: str = "claude",
    parent_pane: str | None = None,
    waiting_message: str | None = None,
) -> OrcaAgent:
    return wrapup_decode_agent(
        {
            "paneKey": pane,
            "state": state,
            "agentType": agent_type,
            "parentPaneKey": parent_pane,
            "prompt": prompt,
            "conversationName": f"session {pane}",
            "lastAssistantMessage": reply,
            "stateStartedAt": _ms(idle),
            "programStatus": {"state": "blocked", "kind": "question", "message": waiting_message}
            if waiting_message
            else None,
        }
    )


def wrapup_decode_agent(raw: dict[str, object]) -> OrcaAgent:
    return decode(OrcaAgent, raw)


def make_worktree(
    wid: str,
    *,
    repo: str = "rig",
    repo_id: str = "rig-id",
    path: str | None = None,
    parent: str | None = None,
    agents: tuple[OrcaAgent, ...] = (),
    pr: LinkedPr | None = None,
) -> OrcaWorktree:
    return OrcaWorktree(
        worktree_id=wid,
        repo_id=repo_id,
        path=path or f"/code/personal/{repo}/temp/{wid}",
        repo=repo,
        display_name=wid,
        branch=f"refs/heads/rtkelly13/{wid}",
        parent_id=parent,
        linked_pr=pr,
        agents=agents,
    )


def make_terminal(pane: str, *, identity: str | None = "claude", writable: bool = True) -> OrcaTerminal:
    tab, leaf = pane.split(":")
    return OrcaTerminal(handle=f"term_{tab}", tab_id=tab, leaf_id=leaf, agent_identity=identity, writable=writable)


REPOS = (
    Repo(id="rig-id", path="/code/personal/rig", display_name="rig"),
    Repo(id="ds-id", path="/code/rtkelly-labs/design-system", display_name="design-system"),
    Repo(id="work-id", path="/code/acme/platform", display_name="platform"),
)


def _snapshot(*worktrees: OrcaWorktree) -> OrcaSnapshot:
    return OrcaSnapshot(worktrees=worktrees, terminals=(), repos=REPOS)


def _names(placed: list[Placed]) -> list[str]:
    return [f"{'  ' * p.depth}{p.worktree.name}" for p in placed]


# ---------------------------------------------------------------- scope and targeting


@pytest.mark.usefixtures("work_scope")
def test_classify_splits_personal_org_and_work_folders() -> None:
    assert wrapup.classify(Path("/code/personal/rig"), ROOT) is Scope.PERSONAL
    assert wrapup.classify(Path("/code/rtkelly-labs/Lake.NET"), ROOT) is Scope.PERSONAL
    assert wrapup.classify(Path("/code/Resultful/Resultful"), ROOT) is Scope.PERSONAL
    assert wrapup.classify(Path("/code/acme/platform"), ROOT) is Scope.WORK
    assert wrapup.classify(Path("/code/workspace"), ROOT) is Scope.WORK
    assert wrapup.classify(Path("/code/external/tools/x"), ROOT) is None


def test_without_a_configured_work_scope_nothing_is_work() -> None:
    assert wrapup.classify(Path("/code/acme/platform"), ROOT) is None
    assert wrapup.classify(Path("/code/personal/rig"), ROOT) is Scope.PERSONAL


@pytest.mark.usefixtures("work_scope")
def test_scope_uses_the_repo_folder_not_the_worktree_folder() -> None:
    # Orca's own worktrees live outside the code root; their repo decides the scope.
    outside = make_worktree("orca-made", repo="platform", repo_id="work-id", path="/home/orca/workspaces/x")
    snapshot = _snapshot(outside, make_worktree("mine"))
    work = wrapup.select(snapshot, Target(scope=Scope.WORK, all_repos=True), ROOT)
    assert _names(work) == ["orca-made"]


def test_repo_target_picks_one_repo_case_insensitively() -> None:
    snapshot = _snapshot(make_worktree("a"), make_worktree("b", repo="design-system", repo_id="ds-id"))
    placed = wrapup.select(snapshot, Target(scope=Scope.PERSONAL, repos=("RIG",)), ROOT)
    assert _names(placed) == ["a"]


def test_worktree_narrows_by_name_branch_or_path() -> None:
    snapshot = _snapshot(make_worktree("alpha"), make_worktree("beta"))
    by_branch = wrapup.select(snapshot, Target(scope=Scope.PERSONAL, worktrees=("rtkelly13/beta",)), ROOT)
    by_path = wrapup.select(
        snapshot, Target(scope=Scope.PERSONAL, all_repos=True, worktrees=("/code/personal/rig/temp/alpha",)), ROOT
    )
    assert _names(by_branch) == ["beta"]
    assert _names(by_path) == ["alpha"]


def test_children_follow_subtasks_across_repos_in_tree_order() -> None:
    snapshot = _snapshot(
        make_worktree("parent"),
        make_worktree("kid-ds", repo="design-system", repo_id="ds-id", parent="parent"),
        make_worktree("kid-rig", parent="parent"),
        make_worktree("grandkid", parent="kid-rig"),
        make_worktree("unrelated"),
    )
    without = wrapup.select(snapshot, Target(scope=Scope.PERSONAL, worktrees=("parent",)), ROOT)
    with_children = wrapup.select(snapshot, Target(scope=Scope.PERSONAL, worktrees=("parent",), children=True), ROOT)
    assert _names(without) == ["parent"]
    assert _names(with_children) == ["parent", "  kid-ds", "  kid-rig", "    grandkid"]


def test_children_never_cross_into_the_other_scope() -> None:
    snapshot = _snapshot(
        make_worktree("parent"),
        make_worktree("work-kid", repo="platform", repo_id="work-id", parent="parent", path="/x"),
    )
    placed = wrapup.select(snapshot, Target(scope=Scope.PERSONAL, worktrees=("parent",), children=True), ROOT)
    assert _names(placed) == ["parent"]


# ---------------------------------------------------------------- the facts-first gate


@pytest.mark.parametrize(
    ("facts", "pr", "settled"),
    [
        (CLEAN, LinkedPr(number=1, state="merged"), True),
        (CLEAN, LinkedPr(number=1, state="closed"), True),
        (CLEAN, LinkedPr(number=1, state="open"), False),
        (GitFacts(dirty=True, unpushed=0, merged=True), LinkedPr(number=1, state="merged"), False),
        (GitFacts(dirty=False, unpushed=2, merged=False), LinkedPr(number=1, state="merged"), False),
        (GitFacts(dirty=False, unpushed=0, merged=True), None, True),
        (CLEAN, None, False),
        (None, LinkedPr(number=1, state="merged"), False),
    ],
)
def test_settled_needs_clean_pushed_and_merged(facts: GitFacts | None, pr: LinkedPr | None, settled: bool) -> None:
    assert wrapup.is_settled(facts, pr) is settled


def test_describe_facts_reads_as_one_line() -> None:
    facts = GitFacts(dirty=True, unpushed=3, merged=False)
    pr = LinkedPr(number=70, state="draft", checks_status="pending")
    assert (
        wrapup.describe_facts(facts, pr)
        == "uncommitted changes; 3 commit(s) on no remote; PR #70 draft, checks pending"
    )
    assert wrapup.describe_facts(None, None) == "folder missing"


# ---------------------------------------------------------------- planning


class MemoryStore:
    """A StateStore keyed by worktree path; `refuse` makes every write fail with that reason."""

    def __init__(self, refuse: str | None = None) -> None:
        self.files: dict[Path, WrapupState] = {}
        self.refuse: str | None = refuse

    def read(self, worktree: Path) -> WrapupState | None:
        return self.files.get(worktree)

    def write(self, worktree: Path, state: WrapupState) -> str | None:
        if self.refuse is None:
            self.files[worktree] = state
        return self.refuse


def plan_rows(
    *worktrees: OrcaWorktree,
    terminals: tuple[OrcaTerminal, ...] = (),
    facts: GitFacts | None = CLEAN,
    options: Options | None = None,
    store: MemoryStore | None = None,
) -> list[Row]:
    placed = [Placed(w, 0 if w.parent_id is None else 1) for w in worktrees]
    return wrapup.plan(
        placed,
        terminals,
        now=NOW,
        options=options or Options(),
        facts=lambda _path: facts,
        state=(store or MemoryStore()).read,
    )


def test_status_for_each_kind_of_session() -> None:
    worktree = make_worktree(
        "w",
        agents=(
            make_agent("t1:l1"),  # idle 2h, terminal live: ask
            make_agent("t2:l2", state="working"),
            make_agent("t3:l3", state="waiting", waiting_message="Which option?"),
            make_agent("t4:l4", idle=5),  # idle 5m: recent
            make_agent("t5:l5"),  # shell terminal
            make_agent("t6:l6"),  # no live terminal
            make_agent("t7:l7", prompt=f"{MARKER} Is this done?", reply="WRAPUP: done\nAll merged."),
            make_agent("t8:l8", state="working", prompt=f"{MARKER} Is this done?"),
        ),
    )
    terminals = (
        make_terminal("t1:l1"),
        make_terminal("t2:l2"),
        make_terminal("t3:l3"),
        make_terminal("t4:l4"),
        make_terminal("t5:l5", identity=None),
        make_terminal("t7:l7"),
        make_terminal("t8:l8"),
    )
    rows = plan_rows(worktree, terminals=terminals)
    assert [r.status for r in rows] == [
        Status.ASK,
        Status.WORKING,
        Status.WAITING,
        Status.RECENT,
        Status.NO_TERMINAL,
        Status.NO_TERMINAL,
        Status.ANSWERED,
        Status.ANSWERING,
    ]
    assert rows[2].note == "Which option?"
    assert rows[6].verdict == wrapup.Verdict(done=True, detail="")


def test_sub_agent_panes_and_other_agents_are_left_out() -> None:
    worktree = make_worktree(
        "w",
        agents=(make_agent("t1:l1", parent_pane="t0:l0"), make_agent("t2:l2", agent_type="codex"), make_agent("t3:l3")),
    )
    rows = plan_rows(worktree, terminals=(make_terminal("t3:l3"),))
    assert [r.session for r in rows] == ["session t3:l3"]


def test_settled_worktrees_are_not_asked_unless_included() -> None:
    worktree = make_worktree("w", agents=(make_agent("t1:l1"),), pr=LinkedPr(number=4, state="merged"))
    terminals = (make_terminal("t1:l1"),)
    assert plan_rows(worktree, terminals=terminals)[0].status is Status.SETTLED
    included = plan_rows(worktree, terminals=terminals, options=Options(include_settled=True))
    assert included[0].status is Status.ASK


def test_max_defers_parents_before_their_subtasks() -> None:
    parent = make_worktree("parent", agents=(make_agent("t1:l1"),))
    child = make_worktree("child", parent="parent", agents=(make_agent("t2:l2"),))
    rows = plan_rows(
        parent, child, terminals=(make_terminal("t1:l1"), make_terminal("t2:l2")), options=Options(max_sends=1)
    )
    assert [(r.worktree, r.status) for r in rows] == [("parent", Status.DEFERRED), ("child", Status.ASK)]
    assert rows[0].children == ("child",)


def test_answer_without_a_verdict_line_is_noted() -> None:
    worktree = make_worktree("w", agents=(make_agent("t1:l1", prompt=f"{MARKER} hi", reply="Everything is merged."),))
    row = plan_rows(worktree)[0]
    assert row.verdict is None
    assert row.note == "replied without a WRAPUP line: Everything is merged."


@pytest.mark.parametrize(
    ("reply", "verdict"),
    [
        ("WRAPUP: done", wrapup.Verdict(done=True)),
        ("**WRAPUP: pending** — merge PR #12", wrapup.Verdict(done=False, detail="merge PR #12")),
        ("Some preamble\n`WRAPUP: pending: push 2 commits`", wrapup.Verdict(done=False, detail="push 2 commits")),
        ("wrapup - DONE", wrapup.Verdict(done=True)),
        ("No verdict here", None),
        (None, None),
    ],
)
def test_parse_verdict(reply: str | None, verdict: wrapup.Verdict | None) -> None:
    assert wrapup.parse_verdict(reply) == verdict


def test_open_children_counts_unfinished_descendants() -> None:
    parent = make_worktree("parent", agents=(make_agent("t1:l1"),))
    done_kid = make_worktree(
        "done", parent="parent", agents=(make_agent("t2:l2", prompt=f"{MARKER} x", reply="WRAPUP: done"),)
    )
    open_kid = make_worktree("open", parent="parent", agents=(make_agent("t3:l3"),))
    rows = plan_rows(parent, done_kid, open_kid, terminals=(make_terminal("t3:l3"),))
    assert wrapup.open_children(rows[0], rows) == 1


# ---------------------------------------------------------------- the prompt and sending


def test_render_prompt_fills_the_template_behind_the_marker() -> None:
    parent = make_worktree(
        "parent", agents=(make_agent("t1:l1"),), pr=LinkedPr(number=7, state="open", checks_status="success")
    )
    kid = make_worktree(
        "kid", parent="parent", agents=(make_agent("t2:l2", prompt=f"{MARKER} x", reply="WRAPUP: done"),)
    )
    rows = plan_rows(parent, kid, terminals=(make_terminal("t1:l1"),))
    text = wrapup.render_prompt(
        "$worktree on $branch: $facts. Kids: $children. $unknown", rows[0], rows, {"parent": parent, "kid": kid}
    )
    assert text.startswith(f"{MARKER} parent on rtkelly13/parent: clean; PR #7 open, checks success.")
    assert "Kids: rig/kid (rtkelly13/kid; clean; no PR; done)." in text
    assert text.endswith("$unknown")


def test_the_shipped_template_renders() -> None:
    template = (Path(__file__).resolve().parents[1] / "config" / wrapup.TEMPLATE_FILE).read_text(encoding="utf-8")
    worktree = make_worktree("w", agents=(make_agent("t1:l1"),))
    rows = plan_rows(worktree, terminals=(make_terminal("t1:l1"),))
    text = wrapup.render_prompt(template, rows[0], rows, {"w": worktree})
    assert "WRAPUP: done" in text
    assert "$" not in text


class FakeOrca:
    def __init__(self, answers: dict[str, proc.Ran] | None = None) -> None:
        self.store: MemoryStore = MemoryStore()
        self.calls: list[list[str]] = []
        self.answers: dict[str, proc.Ran] = answers or {}

    def __call__(self, args: list[str]) -> proc.Ran:
        self.calls.append(args)
        return self.answers.get(" ".join(args[:2]), proc.Ran(0, '{"ok": true}', ""))


def test_apply_sends_deepest_first_with_submit_wait() -> None:
    parent = make_worktree("parent", agents=(make_agent("t1:l1"),))
    child = make_worktree("child", parent="parent", agents=(make_agent("t2:l2"),))
    rows = plan_rows(parent, child, terminals=(make_terminal("t1:l1"), make_terminal("t2:l2")))
    orca = FakeOrca()
    sent = wrapup.apply_sends(rows, "Done?", {"parent": parent, "child": child}, run=orca)
    assert [r.status for r in sent] == [Status.SENT, Status.SENT]
    assert [c[3] for c in orca.calls] == ["term_t2", "term_t1"]
    assert orca.calls[0][4:] == ["--text", f"{MARKER} Done?", "--enter", "--wait-submit", "10", "--json"]


def test_a_refused_send_is_reported() -> None:
    worktree = make_worktree("w", agents=(make_agent("t1:l1"),))
    rows = plan_rows(worktree, terminals=(make_terminal("t1:l1"),))
    refusal = proc.Ran(1, '{"ok": false, "error": {"message": "terminal not writable"}}', "")
    sent = wrapup.apply_sends(rows, "Done?", {"w": worktree}, run=FakeOrca({"terminal send": refusal}))
    assert sent[0].status is Status.SEND_FAILED
    assert sent[0].note == "terminal not writable"


# ---------------------------------------------------------------- reading Orca


def _ps_json() -> str:
    return json.dumps(
        {
            "ok": True,
            "result": {
                "truncated": False,
                "worktrees": [
                    {
                        "worktreeId": "w1",
                        "repoId": "rig-id",
                        "path": "/code/personal/rig/temp/w1",
                        "repo": "rig",
                        "displayName": "w1",
                        "branch": "refs/heads/feature",
                        "parentWorktreeId": None,
                        "linkedPR": {"number": 3, "state": "open", "checksStatus": "success", "title": "x"},
                        "agents": [{"paneKey": "t1:l1", "state": "done", "agentType": "claude", "newField": 1}],
                        "someNewField": True,
                    },
                    {"broken": True},
                ],
            },
        }
    )


def test_read_snapshot_decodes_and_drops_unreadable_rows() -> None:
    orca = FakeOrca(
        {
            "worktree ps": proc.Ran(0, _ps_json(), ""),
            "terminal list": proc.Ran(
                0,
                json.dumps({"ok": True, "result": {"terminals": [{"handle": "h", "tabId": "t1", "leafId": "l1"}]}}),
                "",
            ),
            "repo list": proc.Ran(
                0, json.dumps({"ok": True, "result": {"repos": [{"id": "rig-id", "path": "/code/personal/rig"}]}}), ""
            ),
        }
    )
    snapshot = wrapup.read_snapshot(orca)
    assert [w.worktree_id for w in snapshot.worktrees] == ["w1"]
    assert snapshot.worktrees[0].linked_pr == LinkedPr(number=3, state="open", checks_status="success")
    assert snapshot.terminals[0].pane_key == "t1:l1"
    assert snapshot.repos[0].path == "/code/personal/rig"


def test_read_snapshot_raises_when_orca_is_down() -> None:
    with pytest.raises(WrapupError, match="orca worktree ps failed: not running"):
        _ = wrapup.read_snapshot(FakeOrca({"worktree ps": proc.Ran(1, "", "not running\n")}))


# ---------------------------------------------------------------- the command

runner = CliRunner()

_ANSI = re.compile(r"\x1b\[[0-9;]*m")


def _plain(text: str) -> str:
    """CLI output as words: colour codes and Rich's box drawing gone, wrapping undone.

    GitHub Actions makes Rich colour and box its error panel, splitting a message across rows."""
    return " ".join(_ANSI.sub("", text).translate(str.maketrans("", "", "│╭╮╰╯─")).split())


def test_duration_parsing() -> None:
    assert wrapup_cli.parse_duration("30m") == dt.timedelta(minutes=30)
    assert wrapup_cli.parse_duration("2h") == dt.timedelta(hours=2)
    assert wrapup_cli.parse_duration("45") == dt.timedelta(minutes=45)


@pytest.mark.parametrize(
    ("args", "message"),
    [
        (["--all"], "exactly one of --personal or --work"),
        (["--personal", "--work", "--all"], "exactly one of --personal or --work"),
        (["--personal"], "name a target"),
        (["--personal", "--all", "--repo", "rig"], "exclusive"),
    ],
)
def test_scope_and_target_are_required(args: list[str], message: str) -> None:
    result = runner.invoke(app, ["wrapup", *args])
    assert result.exit_code == 2
    assert message in _plain(result.output)


@dataclass(frozen=True, slots=True)
class _Emitted:
    status: str


def _statuses(output: str) -> list[str]:
    return [row.status for row in decode_json(list[_Emitted], output)]


@pytest.fixture
def fake_estate(monkeypatch: pytest.MonkeyPatch, tmp_path: Path) -> FakeOrca:
    worktree = make_worktree("w1", agents=(make_agent("t1:l1"),))
    kid = make_worktree(
        "w2", parent="w1", agents=(make_agent("t2:l2", prompt=f"{MARKER} x", reply="WRAPUP: pending: merge PR #9"),)
    )
    snapshot = OrcaSnapshot(worktrees=(worktree, kid), terminals=(make_terminal("t1:l1"),), repos=REPOS, truncated=True)
    orca = FakeOrca()

    def read_snapshot(run: wrapup.OrcaRunner | None = None) -> OrcaSnapshot:
        del run
        return snapshot

    def read_git_facts(path: Path) -> GitFacts:
        del path
        return CLEAN

    monkeypatch.setattr(wrapup, "read_snapshot", read_snapshot)
    monkeypatch.setattr(wrapup, "read_git_facts", read_git_facts)
    monkeypatch.setattr(wrapup, "run_orca", orca)
    monkeypatch.setattr(wrapup, "default_store", lambda: orca.store)
    monkeypatch.setattr(wrapup_record, "default_store", lambda: orca.store)
    monkeypatch.setattr(wrapup_watch, "default_store", lambda: orca.store)
    monkeypatch.setattr(roots, "code_root", lambda: ROOT)
    monkeypatch.setattr(wrapup_watch, "default_clock", lambda: FakeClock().clock)
    _ = (tmp_path / "t.md").write_text("Done yet?", encoding="utf-8")
    return orca


def test_dry_run_sends_nothing(fake_estate: FakeOrca, tmp_path: Path) -> None:
    result = runner.invoke(
        app, ["wrapup", "--personal", "--repo", "rig", "--template", str(tmp_path / "t.md"), "--json"]
    )
    assert result.exit_code == 0, result.output
    assert fake_estate.calls == []
    assert _statuses(result.stdout) == ["ask", "answered"]


def test_apply_sends_the_template(fake_estate: FakeOrca, tmp_path: Path) -> None:
    result = runner.invoke(
        app, ["wrapup", "--personal", "--all", "--apply", "--follow", "--template", str(tmp_path / "t.md"), "--json"]
    )
    assert result.exit_code == 0, result.output
    assert _statuses(result.stdout) == ["sent", "answered"]
    assert fake_estate.calls[0][5] == f"{MARKER} Done yet?"


@pytest.mark.usefixtures("work_scope")
def test_work_scope_skips_personal_sessions(fake_estate: FakeOrca, tmp_path: Path) -> None:
    result = runner.invoke(app, ["wrapup", "--work", "--all", "--apply", "--template", str(tmp_path / "t.md")])
    assert result.exit_code == 0, result.output
    assert "No matching Claude sessions" in result.output
    assert fake_estate.calls == []


def test_table_shows_verdicts_subtasks_and_counts(
    fake_estate: FakeOrca, tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    monkeypatch.setattr(wrapup_cli, "console", Console(width=300))
    result = runner.invoke(
        app, ["wrapup", "--personal", "--all", "--show-prompt", "--template", str(tmp_path / "t.md")]
    )
    assert result.exit_code == 0, result.output
    out = " ".join(result.output.split())
    assert f"{MARKER} Done yet?" in out
    assert "DRY-RUN" in out
    assert "pending: merge PR #9" in out
    assert "1 of the subtask sessions not finished" in out
    assert "1 ask · 1 answered" in out
    assert "only part of the worktree list" in out
    assert fake_estate.calls == []


def test_a_failed_send_exits_nonzero(fake_estate: FakeOrca, tmp_path: Path) -> None:
    fake_estate.answers["terminal send"] = proc.Ran(1, "", "pane closed")
    result = runner.invoke(
        app, ["wrapup", "--personal", "--all", "--apply", "--follow", "--template", str(tmp_path / "t.md")]
    )
    assert result.exit_code == 1
    assert "send-failed" in result.output


@pytest.mark.usefixtures("fake_estate")
def test_a_missing_template_exits_before_reading_orca(tmp_path: Path) -> None:
    result = runner.invoke(app, ["wrapup", "--personal", "--all", "--template", str(tmp_path / "missing.md")])
    assert result.exit_code == 1
    assert "Cannot read the prompt template" in " ".join(result.output.split())


def test_orca_down_exits_with_the_unavailable_code(monkeypatch: pytest.MonkeyPatch, tmp_path: Path) -> None:
    def down(run: wrapup.OrcaRunner | None = None) -> OrcaSnapshot:
        del run
        raise WrapupError("orca worktree ps failed: not running")

    monkeypatch.setattr(wrapup, "read_snapshot", down)
    template = tmp_path / "t.md"
    _ = template.write_text("x", encoding="utf-8")
    result = runner.invoke(app, ["wrapup", "--personal", "--all", "--template", str(template)])
    assert result.exit_code == ORCA_UNAVAILABLE_EXIT


def test_a_bad_duration_is_rejected() -> None:
    result = runner.invoke(app, ["wrapup", "--personal", "--all", "--idle", "soon"])
    assert result.exit_code == 2
    assert "expected a duration" in " ".join(result.output.split())


# ---------------------------------------------------------------- the state file


def _ask(minutes_ago: float, pane: str = "t1:l1") -> AskRecord:
    at = (NOW - dt.timedelta(minutes=minutes_ago)).isoformat(timespec="milliseconds")
    return AskRecord(at=at, pane=pane, session="s", head="abc", facts="clean", template_sha="0" * 12)


def _store_with(worktree: OrcaWorktree, state: WrapupState) -> MemoryStore:
    store = MemoryStore()
    store.files[Path(worktree.path)] = state
    return store


def test_an_ask_the_pane_never_saw_is_unconfirmed_not_repeated() -> None:
    worktree = make_worktree("w", agents=(make_agent("t1:l1", idle=120),))
    store = _store_with(worktree, WrapupState(asks=(_ask(60),)))
    row = plan_rows(worktree, terminals=(make_terminal("t1:l1"),), store=store)[0]
    assert row.status is Status.UNCONFIRMED
    assert "has not moved since" in row.note


def test_work_resumed_after_an_ask_can_be_asked_again_and_shows_the_last_answer() -> None:
    worktree = make_worktree("w", agents=(make_agent("t1:l1", idle=60, prompt="carry on"),))
    answer = AnswerRecord(
        at="2026-11-07T09:00:00.000+00:00", pane="t1:l1", verdict="pending", detail="merge #3", head=""
    )
    store = _store_with(worktree, WrapupState(asks=(_ask(240),), answers=(answer,)))
    row = plan_rows(worktree, terminals=(make_terminal("t1:l1"),), store=store)[0]
    assert row.status is Status.ASK
    assert row.note == "last answer 2026-11-07T09:00Z: pending: merge #3"


def test_record_keeps_asks_sent_and_new_verdicts_once() -> None:
    asked = make_worktree("asked", agents=(make_agent("t1:l1"),))
    answered = make_worktree("answered", agents=(make_agent("t2:l2", prompt=f"{MARKER} x", reply="WRAPUP: done"),))
    facts = GitFacts(dirty=False, unpushed=0, merged=False, head="f00d")
    store = MemoryStore()
    rows = plan_rows(asked, answered, terminals=(make_terminal("t1:l1"),), facts=facts, store=store)
    sent = [replace(r, status=Status.SENT) if r.status is Status.ASK else r for r in rows]

    _ = wrapup_record.record(sent, now=NOW, template="Done?", store=store)
    ask = store.files[Path(asked.path)].asks[0]
    assert (ask.pane, ask.head, ask.facts, len(ask.template_sha)) == ("t1:l1", "f00d", "clean; no PR", 12)
    state = store.files[Path(answered.path)]
    assert state.answers[0].verdict == "done"
    assert state.schema == SCHEMA
    assert state.worktree_id == "answered"

    again = plan_rows(asked, answered, terminals=(make_terminal("t1:l1"),), facts=facts, store=store)
    _ = wrapup_record.record(again, now=NOW, store=store)
    assert len(store.files[Path(answered.path)].answers) == 1


def test_a_reply_read_later_carries_the_commit_it_was_asked_at() -> None:
    worktree = make_worktree("w", agents=(make_agent("t1:l1", prompt=f"{MARKER} x", reply="WRAPUP: done"),))
    store = _store_with(worktree, WrapupState(asks=(_ask(5),)))
    moved = GitFacts(dirty=False, unpushed=0, merged=False, head="beef")
    rows = plan_rows(worktree, facts=moved, store=store)
    _ = wrapup_record.record(rows, now=NOW, store=store)
    assert store.files[Path(worktree.path)].answers[0].head == "abc"


def test_a_refused_write_is_noted_on_the_row() -> None:
    worktree = make_worktree("w", agents=(make_agent("t1:l1", prompt=f"{MARKER} x", reply="WRAPUP: done"),))
    rows = plan_rows(worktree)
    out = wrapup_record.record(rows, now=NOW, store=MemoryStore(refuse="temp/.rig/ is not gitignored here"))
    assert out[0].note == "state not recorded: temp/.rig/ is not gitignored here"


def test_file_store_round_trips_and_ignores_other_schemas(tmp_path: Path) -> None:
    assert proc.git("init", "-q", cwd=tmp_path).ok
    _ = (tmp_path / ".gitignore").write_text("temp/\n", encoding="utf-8")
    store = FileStateStore()
    assert store.read(tmp_path) is None
    state = WrapupState(worktree_id="w", branch="b", asks=(_ask(5),))
    assert store.write(tmp_path, state) is None
    assert store.read(tmp_path) == state
    _ = (tmp_path / "temp" / ".rig" / "wrapup.json").write_text('{"schema": "rig.wrapup/9"}', encoding="utf-8")
    assert store.read(tmp_path) is None


def test_the_command_records_asks_and_answers(fake_estate: FakeOrca, tmp_path: Path) -> None:
    result = runner.invoke(
        app, ["wrapup", "--personal", "--all", "--apply", "--follow", "--template", str(tmp_path / "t.md")]
    )
    assert result.exit_code == 0, result.output
    files = fake_estate.store.files
    assert [a.pane for a in files[Path("/code/personal/rig/temp/w1")].asks] == ["t1:l1"]
    assert [a.detail for a in files[Path("/code/personal/rig/temp/w2")].answers] == ["merge PR #9"]
