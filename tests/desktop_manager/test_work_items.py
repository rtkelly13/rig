"""Work items: ticket parsing, session classification, attribution, state rules, git/PR parsing, JSON schema."""

from __future__ import annotations

import datetime as dt
import json
from pathlib import Path

import pytest
import typer

from desktop_manager.work import git_facts, join, prs, serialize
from desktop_manager.work.cli import parse_since
from desktop_manager.work.model import (
    Attribution,
    BranchKey,
    BranchRef,
    Checkout,
    PrInfo,
    PrLookup,
    PrStatus,
    RepoFacts,
    SessionFacts,
    SessionState,
    WorkItem,
    WorkReport,
    WorkState,
)
from rig.proc import GhResult

pytestmark = pytest.mark.ci

NOW = dt.datetime(2026, 10, 10, 12, 0, tzinfo=dt.UTC)
ROOT = Path("C:/code")
REPO = ROOT / "acme" / "app"
WORKTREE = REPO / "temp" / "wt-feature"


def _session(
    session_id: str = "s1",
    *,
    state: SessionState = SessionState.ENDED_CLEAN,
    last_cwd: str | None = str(WORKTREE),
    last_activity: dt.datetime | None = NOW - dt.timedelta(hours=1),
) -> SessionFacts:
    return SessionFacts(
        session_id=session_id,
        title="t",
        state=state,
        last_activity=last_activity,
        last_cwd=last_cwd,
        start_cwd=last_cwd,
        prompt_count=1,
    )


def _repo(*, branches: tuple[BranchRef, ...] = ()) -> RepoFacts:
    return RepoFacts(
        path=REPO,
        name="acme/app",
        default_branch="main",
        checkouts=(
            Checkout(path=REPO, branch="main", is_main_checkout=True, dirty=False),
            Checkout(path=WORKTREE, branch="feat/PROJ-1-thing", is_main_checkout=False, dirty=False),
        ),
        branches=branches,
    )


# -- tickets --------------------------------------------------------------------


@pytest.mark.parametrize(
    ("branch", "ticket"),
    [
        ("feat/PROJ-1234-thing", "PROJ-1234"),
        ("proj-3583-hub-fixes", "PROJ-3583"),
        ("rr-802/feed", "RR-802"),
        ("docs/proj-12-x-PROJ-99", "PROJ-99"),
        ("pr-894-review", None),
        ("clone-959", None),
        ("fix-123", None),
        ("v-10", None),
        ("feat/work-items", None),
        ("snapshot-2026", None),
        ("main", None),
        (None, None),
    ],
)
def test_parse_ticket(branch: str | None, ticket: str | None) -> None:
    assert join.parse_ticket(branch) == ticket


# -- sessions -------------------------------------------------------------------


@pytest.mark.parametrize(
    ("live_status", "is_live", "cleared", "exited", "turn", "expected"),
    [
        ("busy", True, True, True, "answered", SessionState.LIVE),
        ("idle", True, False, False, "answered", SessionState.WAITING_ON_USER),
        (None, False, True, True, "answered", SessionState.CLEARED),
        (None, False, False, True, "mid-tool", SessionState.ENDED_CLEAN),
        (None, False, False, False, "mid-tool", SessionState.DIED_MID_TASK),
        (None, False, False, False, "unanswered", SessionState.DIED_MID_TASK),
        (None, False, False, False, "answered", SessionState.DIED_IDLE),
    ],
)
def test_classify_session(
    live_status: str | None, is_live: bool, cleared: bool, exited: bool, turn: str, expected: SessionState
) -> None:
    state = join.classify_session(
        live_status=live_status, is_live=is_live, cleared=cleared, exited_cleanly=exited, turn_state=turn
    )
    assert state is expected


# -- attribution ----------------------------------------------------------------


def _never_resolve(_cwd: str) -> Attribution | None:
    return None


def _exists(_cwd: str) -> bool:
    return True


def _gone(_cwd: str) -> bool:
    return False


def test_attribute_prefers_the_worktree_nested_inside_its_clone() -> None:
    index = join.CheckoutIndex([_repo()])
    got = join.attribute(
        _session(last_cwd=str(WORKTREE / "src")), index, exists=_exists, resolve_unknown=_never_resolve
    )
    assert got == Attribution(repo_path=REPO, branch="feat/PROJ-1-thing")


def test_attribute_clone_root_is_its_main_checkout() -> None:
    index = join.CheckoutIndex([_repo()])
    got = join.attribute(_session(last_cwd=str(REPO)), index, exists=_exists, resolve_unknown=_never_resolve)
    assert got == Attribution(repo_path=REPO, branch="main")


def test_attribute_removed_directory_goes_unattached() -> None:
    index = join.CheckoutIndex([_repo()])
    got = join.attribute(_session(), index, exists=_gone, resolve_unknown=_never_resolve)
    assert got == Attribution(repo_path=None, reason=f"directory gone: {WORKTREE}")


def test_attribute_falls_back_to_resolver_then_unattached() -> None:
    index = join.CheckoutIndex([_repo()])
    elsewhere = Attribution(repo_path=Path("D:/other"), branch="dev")
    resolved = join.attribute(
        _session(last_cwd="D:/other"), index, exists=_exists, resolve_unknown=lambda _cwd: elsewhere
    )
    assert resolved == elsewhere
    outside = join.attribute(_session(last_cwd="C:/code"), index, exists=_exists, resolve_unknown=_never_resolve)
    assert outside == Attribution(repo_path=None, reason="not in a git checkout: C:/code")


def test_attribute_uses_start_cwd_when_last_is_missing() -> None:
    index = join.CheckoutIndex([_repo()])
    session = SessionFacts("s", "t", SessionState.DIED_IDLE, None, None, str(REPO), 1)
    got = join.attribute(session, index, exists=_exists, resolve_unknown=_never_resolve)
    assert got == Attribution(repo_path=REPO, branch="main")


# -- state ----------------------------------------------------------------------


def _decide(
    *,
    is_trunk: bool = False,
    pr: PrInfo | None = None,
    dirty: bool = False,
    sessions: tuple[SessionFacts, ...] = (),
    branch_ref: BranchRef | None = None,
    last_activity: dt.datetime | None = NOW - dt.timedelta(days=30),
) -> join.StateDecision:
    return join.derive_state(
        is_trunk=is_trunk,
        pr=pr,
        checkouts=(Checkout(path=WORKTREE, branch="b", is_main_checkout=False, dirty=dirty),),
        sessions=sessions,
        branch_ref=branch_ref,
        last_activity=last_activity,
        now=NOW,
    )


def _pr(status: PrStatus) -> PrInfo:
    return PrInfo(number=7, status=status, url="u")


def test_trunk_wins_over_everything() -> None:
    decision = _decide(is_trunk=True, pr=_pr(PrStatus.MERGED), dirty=True)
    assert decision == join.StateDecision(WorkState.TRUNK, ("default branch", "uncommitted changes"))


def test_merged_lists_only_cleanup_blockers() -> None:
    running = _session(state=SessionState.WAITING_ON_USER)
    decision = _decide(
        pr=_pr(PrStatus.MERGED), dirty=True, sessions=(running,), branch_ref=BranchRef(name="b", unpushed=3)
    )
    assert decision == join.StateDecision(
        WorkState.MERGED, ("PR #7 merged", "1 running session", "uncommitted changes")
    )


def test_open_pr_or_unpushed_is_active() -> None:
    assert _decide(pr=_pr(PrStatus.DRAFT)) == join.StateDecision(WorkState.ACTIVE, ("draft PR #7",))
    assert _decide(branch_ref=BranchRef(name="b", unpushed=2)) == join.StateDecision(
        WorkState.ACTIVE, ("2 unpushed commits",)
    )


def test_closed_unless_something_keeps_it_active() -> None:
    gone = BranchRef(name="b", upstream="origin/b", upstream_gone=True)
    assert _decide(pr=_pr(PrStatus.CLOSED), branch_ref=gone) == join.StateDecision(
        WorkState.CLOSED, ("PR #7 closed unmerged", "upstream gone")
    )
    assert _decide(pr=_pr(PrStatus.CLOSED), dirty=True).state is WorkState.ACTIVE


def test_recent_activity_is_active_otherwise_stale() -> None:
    assert _decide(last_activity=NOW - dt.timedelta(days=2)) == join.StateDecision(
        WorkState.ACTIVE, ("activity 2d ago",)
    )
    assert _decide() == join.StateDecision(WorkState.STALE, ("no activity for 30d",))
    assert _decide(last_activity=None) == join.StateDecision(WorkState.STALE, ("no activity recorded",))


# -- building and filtering -----------------------------------------------------


def _build(sessions: list[join.AttributedSession]) -> list[WorkItem]:
    feature = BranchKey(REPO, "feat/PROJ-1-thing")
    inputs = join.JoinInputs(
        repos=[_repo(branches=(BranchRef(name="feat/PROJ-1-thing", last_commit=NOW - dt.timedelta(days=40)),))],
        sessions=sessions,
        prs={feature: PrInfo(number=9, status=PrStatus.MERGED, url="u", updated_at=NOW - dt.timedelta(days=3))},
        pr_lookups={REPO: PrLookup.OK},
    )
    return join.build_items(inputs, now=NOW)


def test_build_items_joins_sessions_prs_and_unattached_bucket() -> None:
    in_tree = _session("in-tree")
    stray = _session("stray", last_cwd="C:/code")
    items = _build(
        [
            join.AttributedSession(in_tree, Attribution(repo_path=REPO, branch="feat/PROJ-1-thing")),
            join.AttributedSession(stray, Attribution(repo_path=None, reason="not in a git checkout: C:/code")),
        ]
    )
    assert [(i.key, i.state, i.pr_lookup) for i in items] == [
        ("acme/app:feat/PROJ-1-thing", WorkState.MERGED, PrLookup.OK),
        ("acme/app:main", WorkState.TRUNK, PrLookup.SKIPPED),
        ("unattached", WorkState.UNATTACHED, PrLookup.SKIPPED),
    ]
    feature = items[0]
    assert feature.ticket == "PROJ-1"
    assert feature.sessions == (in_tree,)
    assert feature.last_activity == in_tree.last_activity
    assert items[2].sessions == (stray,)


def test_session_branch_without_checkout_becomes_an_item() -> None:
    items = _build([join.AttributedSession(_session(), Attribution(repo_path=REPO, branch="gone-branch"))])
    assert "acme/app:gone-branch" in [i.key for i in items]


def test_filter_hides_quiet_trunk_unless_asked() -> None:
    items = _build([])
    assert [i.key for i in join.filter_items(items, join.ItemFilter())] == ["acme/app:feat/PROJ-1-thing"]
    everything = join.filter_items(items, join.ItemFilter(include_quiet_trunk=True))
    assert len(everything) == 2
    trunk_only = join.filter_items(items, join.ItemFilter(states=frozenset({WorkState.TRUNK})))
    assert [i.key for i in trunk_only] == ["acme/app:main"]
    assert join.filter_items(items, join.ItemFilter(repo="nope")) == []
    recent = join.filter_items(items, join.ItemFilter(since=NOW - dt.timedelta(days=1), include_quiet_trunk=True))
    assert recent == []


# -- git parsing ----------------------------------------------------------------


def test_parse_track() -> None:
    assert git_facts.parse_track("", "") == git_facts.Track(ahead=None, behind=None, gone=False)
    assert git_facts.parse_track("origin/x", "[gone]") == git_facts.Track(ahead=None, behind=None, gone=True)
    assert git_facts.parse_track("origin/x", "[ahead 2, behind 1]") == git_facts.Track(ahead=2, behind=1, gone=False)
    assert git_facts.parse_track("origin/x", "") == git_facts.Track(ahead=0, behind=0, gone=False)


def test_parse_refs_with_unpushed_counts() -> None:
    lines = [
        "refs/heads/main\taaa\t2026-10-01T10:00:00+01:00\torigin/main\t\t",
        "refs/heads/feat\tbbb\t2026-10-02T10:00:00+00:00\torigin/feat\t[gone]\t",
        "refs/remotes/origin/HEAD\tccc\t2026-10-01T10:00:00+00:00\t\t\trefs/remotes/origin/main",
        "garbage",
    ]
    output = "\n".join(lines)
    assert git_facts.ref_tips(output) == ["aaa", "bbb", "ccc"]
    scan = git_facts.parse_refs(output, {"bbb": 4})
    assert scan == git_facts.RefScan(
        branches=(
            BranchRef(
                name="main",
                last_commit=dt.datetime(2026, 10, 1, 9, 0, tzinfo=dt.UTC),
                upstream="origin/main",
                ahead=0,
                behind=0,
            ),
            BranchRef(
                name="feat",
                last_commit=dt.datetime(2026, 10, 2, 10, 0, tzinfo=dt.UTC),
                upstream="origin/feat",
                upstream_gone=True,
                unpushed=4,
            ),
        ),
        default_branch="main",
    )


def test_unpushed_counts_walks_within_the_unpushed_set() -> None:
    # c3 -> c2 -> c1 -> pushed; m merges c2 and x1; x1 -> pushed. "pushed" is not listed.
    rev_list = "\n".join(["m c2 x1", "c3 c2", "c2 c1", "c1 pushed", "x1 pushed", ""])
    assert git_facts.unpushed_counts(rev_list, ["c3", "m", "c1", "pushed"]) == {
        "c3": 3,
        "m": 4,
        "c1": 1,
        "pushed": 0,
    }


# -- PRs ------------------------------------------------------------------------


def test_build_query_aliases_and_escapes_branches() -> None:
    query = prs.build_query("o/n", ["a", 'we"ird'])
    assert 'repository(owner: "o", name: "n")' in query
    assert 'b0: pullRequests(headRefName: "a"' in query
    assert 'b1: pullRequests(headRefName: "we\\"ird"' in query


def _response(nodes_by_alias: dict[str, list[dict[str, str | int | bool]]]) -> str:
    repository = {alias: {"nodes": nodes} for alias, nodes in nodes_by_alias.items()}
    return json.dumps({"data": {"repository": repository}})


def test_parse_response_maps_states() -> None:
    stdout = _response(
        {
            "b0": [{"number": 1, "state": "OPEN", "isDraft": True, "url": "u1", "updatedAt": "2026-10-01T00:00:00Z"}],
            "b1": [{"number": 2, "state": "MERGED", "isDraft": False, "url": "u2"}],
            "b2": [],
        }
    )
    assert prs.parse_response(stdout, ["a", "b", "c"]) == {
        "a": PrInfo(1, PrStatus.DRAFT, "u1", dt.datetime(2026, 10, 1, tzinfo=dt.UTC)),
        "b": PrInfo(2, PrStatus.MERGED, "u2"),
        "c": None,
    }
    assert prs.parse_response("not json", ["a"]) is None


class _FakeGh:
    def __init__(self, result: GhResult) -> None:
        self.result = result
        self.calls = 0

    def __call__(self, _query: str) -> GhResult:
        self.calls += 1
        return self.result


def test_fetch_prs_caches_then_answers_from_cache(tmp_path: Path) -> None:
    cache_file = tmp_path / "prs.json"
    gh = _FakeGh(GhResult(0, _response({"b0": [{"number": 5, "state": "CLOSED", "url": "u"}]}), ""))
    request = prs.PrRequest(repo_path=REPO, slug="o/n", branches=("feat",))
    first = prs.fetch_prs([request], runner=gh, cache_file=cache_file, now=NOW)
    second = prs.fetch_prs([request], runner=gh, cache_file=cache_file, now=NOW + dt.timedelta(minutes=1))
    expected = {BranchKey(REPO, "feat"): PrInfo(5, PrStatus.CLOSED, "u")}
    assert (first.prs, first.lookups, first.fetched) == (expected, {REPO: PrLookup.OK}, 1)
    assert (second.prs, second.cached, gh.calls) == (expected, 1, 1)


def test_fetch_prs_degrades_when_gh_fails_or_origin_is_not_github() -> None:
    gh = _FakeGh(GhResult(1, "", "auth required"))
    other = Path("C:/code/other")
    results = prs.fetch_prs(
        [
            prs.PrRequest(repo_path=REPO, slug="o/n", branches=("feat",)),
            prs.PrRequest(repo_path=other, slug="", branches=("x",)),
        ],
        runner=gh,
        cache_file=None,
        now=NOW,
    )
    assert results.prs == {}
    assert results.lookups == {REPO: PrLookup.UNAVAILABLE, other: PrLookup.UNAVAILABLE}
    assert len(results.warnings) == 2


# -- JSON and CLI ---------------------------------------------------------------


def test_report_json_schema_keys() -> None:
    items = _build([join.AttributedSession(_session(), Attribution(repo_path=REPO, branch="feat/PROJ-1-thing"))])
    report = WorkReport(generated_at=NOW, code_root=ROOT, items=tuple(items))
    payload = json.loads(json.dumps(serialize.report_json(report, items)))
    assert set(payload) == {"schema_version", "generated_at", "code_root", "warnings", "items"}
    item = payload["items"][0]
    assert set(item) == {
        "key",
        "repo",
        "repo_path",
        "branch",
        "ticket",
        "is_trunk",
        "state",
        "reasons",
        "last_activity",
        "dirty",
        "unpushed",
        "checkouts",
        "branch_ref",
        "pr",
        "pr_lookup",
        "sessions",
    }
    assert set(item["checkouts"][0]) == {"path", "is_main_checkout", "dirty"}
    assert set(item["branch_ref"]) == {"upstream", "upstream_gone", "ahead", "behind", "unpushed", "last_commit"}
    assert set(item["pr"]) == {"number", "state", "url", "updated_at"}
    assert set(item["sessions"][0]) == {
        "session_id",
        "title",
        "state",
        "last_activity",
        "last_cwd",
        "start_cwd",
        "prompt_count",
        "pid",
    }


def test_parse_since() -> None:
    assert parse_since("7d", now=NOW) == NOW - dt.timedelta(days=7)
    assert parse_since("24h", now=NOW) == NOW - dt.timedelta(hours=24)
    assert parse_since("2w", now=NOW) == NOW - dt.timedelta(weeks=2)
    assert parse_since("2026-10-01", now=NOW) == dt.datetime(2026, 10, 1, tzinfo=dt.UTC)
    with pytest.raises(typer.BadParameter):
        parse_since("soon", now=NOW)
