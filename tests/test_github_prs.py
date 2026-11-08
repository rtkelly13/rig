"""The shared PR resolver: chain building (pure) and resolution, with a fake ``GhRunner`` answering GraphQL."""

from __future__ import annotations

from collections.abc import Callable

import pytest

from rig import github_prs
from rig.github_prs import (
    Chain,
    OpenPr,
    PrDetails,
    PrRef,
    PrState,
    build_chains,
    build_query,
    build_queue_chains,
    build_stack_chains,
    parse_details,
    parse_open_pr,
    parse_ref,
    resolve,
    resolve_pr_states,
)
from rig.proc import GhResult
from tests.fakes.gh_runner import FakeGh, FakeRepo

pytestmark = pytest.mark.ci


def _pr(
    number: int,
    head: str,
    base: str = "main",
    *,
    created_at: str = "",
    cross_repo: bool = False,
    queue_position: int | None = None,
    labels: frozenset[str] = frozenset(),
    labeled_at: dict[str, str] | None = None,
) -> OpenPr:
    return OpenPr(number, head, base, created_at, cross_repo, queue_position, labels, labeled_at or {})


# -- refs ----------------------------------------------------------------------


@pytest.mark.parametrize(
    ("text", "expected"),
    [
        ("acme-corp/data-platform#744", PrRef("acme-corp", "data-platform", 744)),
        ("  Acme-Corp/Data-Platform#7 ", PrRef("acme-corp", "data-platform", 7)),
        ("owner/repo.js#1", PrRef("owner", "repo.js", 1)),
        ("owner/repo#0", None),
        ("owner/repo", None),
        ("owner/repo#1 } evil", None),
        ('owner"/repo#1', None),
    ],
)
def test_parse_ref(text: str, expected: PrRef | None) -> None:
    assert parse_ref(text) == expected


def test_ref_str_round_trips() -> None:
    ref = PrRef("o", "r", 5)
    assert str(ref) == "o/r#5"
    assert ref.slug == "o/r"
    assert parse_ref(str(ref)) == ref
    assert PrRef.parse(str(ref)) == ref


def test_ref_is_lowercased_on_construction() -> None:
    assert PrRef("Acme-Corp", "Data-Platform", 1) == PrRef("acme-corp", "data-platform", 1)


def test_parse_raises_for_a_non_ref() -> None:
    with pytest.raises(ValueError, match="not an owner/repo#n"):
        PrRef.parse("owner/repo")


# -- stack chains --------------------------------------------------------------


def test_stack_chain_bottom_first() -> None:
    prs = [_pr(3, "c", "b"), _pr(1, "a", "main"), _pr(2, "b", "a")]
    assert build_stack_chains(prs) == [Chain("stack", "main", (1, 2, 3))]


def test_branching_stack_is_breadth_first() -> None:
    # 778 is the root; 780, 781 and 783 all sit on it, and 790 sits on 781.
    prs = [
        _pr(778, "root", "main"),
        _pr(790, "b2", "b"),
        _pr(783, "c", "root"),
        _pr(781, "b", "root"),
        _pr(780, "a", "root"),
    ]
    assert build_stack_chains(prs) == [Chain("stack", "main", (778, 780, 781, 783, 790))]


def test_unrelated_prs_make_no_chain() -> None:
    assert build_stack_chains([_pr(1, "a"), _pr(2, "b")]) == []


def test_merged_middle_member_splits_the_stack() -> None:
    # 2 merged, so it is not in the open list: 3's base names a branch no open PR heads.
    prs = [_pr(1, "a", "main"), _pr(3, "c", "b")]
    assert build_stack_chains(prs) == []


def test_fork_head_is_never_a_base() -> None:
    prs = [_pr(1, "feature", "main", cross_repo=True), _pr(2, "x", "feature")]
    assert build_stack_chains(prs) == []


def test_self_based_pr_is_a_root() -> None:
    assert build_stack_chains([_pr(1, "a", "a")]) == []


def test_shared_head_is_never_a_base() -> None:
    prs = [_pr(1, "dup", "main"), _pr(2, "dup", "main"), _pr(3, "x", "dup")]
    assert build_stack_chains(prs) == []


# -- queue chains --------------------------------------------------------------


def test_native_queue_by_position() -> None:
    prs = [_pr(5, "e", queue_position=2), _pr(4, "d", queue_position=1), _pr(6, "f")]
    assert build_queue_chains(prs) == [Chain("queue", "main", (4, 5))]


def test_label_queue_holder_first_then_by_label_time() -> None:
    prs = [
        _pr(10, "a", labels=frozenset({"automerge"}), labeled_at={"automerge": "2026-09-01T10:00:00Z"}),
        _pr(11, "b", labels=frozenset({"automerge"}), labeled_at={"automerge": "2026-09-01T09:00:00Z"}),
        _pr(12, "c", labels=frozenset({"automerge", "locked-merging"}), labeled_at={"locked-merging": "x"}),
    ]
    assert build_queue_chains(prs) == [Chain("queue", "main", (12, 11, 10))]


def test_label_time_falls_back_to_created_at() -> None:
    prs = [
        _pr(20, "a", labels=frozenset({"automerge"}), created_at="2026-09-03T00:00:00Z"),
        _pr(21, "b", labels=frozenset({"automerge"}), created_at="2026-09-02T00:00:00Z"),
    ]
    assert build_queue_chains(prs) == [Chain("queue", "main", (21, 20))]


def test_native_queue_members_are_not_label_queued() -> None:
    labels = frozenset({"automerge"})
    prs = [_pr(1, "a", queue_position=1, labels=labels), _pr(2, "b", labels=labels), _pr(3, "c", labels=labels)]
    assert build_queue_chains(prs) == [Chain("queue", "main", (2, 3))]


def test_queues_are_per_base() -> None:
    prs = [_pr(1, "a", "main", queue_position=1), _pr(2, "b", "release", queue_position=1)]
    assert build_queue_chains(prs) == []


def test_build_chains_is_stacks_then_queues() -> None:
    prs = [_pr(1, "a"), _pr(2, "b", "a"), _pr(3, "c", queue_position=1), _pr(4, "d", queue_position=2)]
    assert build_chains(prs) == [Chain("stack", "main", (1, 2)), Chain("queue", "main", (3, 4))]


# -- GraphQL shape -------------------------------------------------------------


def test_build_query_quotes_names_and_aliases_lookups() -> None:
    query = build_query("o", "r", [3, 5], open_list=True, after="Y3Vy")
    assert 'repository(owner: "o", name: "r")' in query
    assert 'pullRequests(states: OPEN, first: 100, after: "Y3Vy")' in query
    assert "pr3: pullRequest(number: 3)" in query
    assert "pr5: pullRequest(number: 5)" in query
    assert "open:" not in build_query("o", "r", [3], open_list=False)


def test_parse_details_reads_size_review_and_checks() -> None:
    node: dict[str, object] = {
        "additions": 700,
        "deletions": 150,
        "changedFiles": 12,
        "isDraft": False,
        "reviewDecision": "CHANGES_REQUESTED",
        "viewerDidAuthor": True,
        "reviewRequests": {"nodes": [{"requestedReviewer": {}}, {"requestedReviewer": {"isViewer": True}}]},
        "commits": {"nodes": [{"commit": {"statusCheckRollup": {"state": "FAILURE"}}}]},
    }
    details = parse_details(node)
    assert details == PrDetails(
        additions=700,
        deletions=150,
        changed_files=12,
        review_decision="CHANGES_REQUESTED",
        checks="FAILURE",
        authored_by_viewer=True,
        review_requested=True,
    )
    assert (details.changed_lines, details.checks_failing) == (850, True)


def test_parse_details_tolerates_missing_fields() -> None:
    assert parse_details({"reviewDecision": None, "commits": {"nodes": [{"commit": {}}]}}) == PrDetails()


def test_details_do_not_affect_state_equality() -> None:
    assert PrState("OPEN", details=PrDetails(draft=True)) == PrState("OPEN")


def test_parse_open_pr_takes_latest_label_event() -> None:
    node: dict[str, object] = {
        "number": 9,
        "headRefName": "h",
        "baseRefName": "main",
        "createdAt": "2026-01-01T00:00:00Z",
        "isCrossRepository": False,
        "mergeQueueEntry": {"position": 3},
        "labels": {"nodes": [{"name": "AutoMerge"}]},
        "timelineItems": {
            "nodes": [
                {"createdAt": "2026-02-02T00:00:00Z", "label": {"name": "automerge"}},
                {"createdAt": "2026-03-03T00:00:00Z", "label": {"name": "automerge"}},
                {"createdAt": "2026-01-05T00:00:00Z", "label": {"name": "automerge"}},
            ]
        },
    }
    pr = parse_open_pr(node)
    assert pr.labels == frozenset({"automerge"})
    assert pr.labeled_at == {"automerge": "2026-03-03T00:00:00Z"}
    assert pr.queue_position == 3


# -- resolution through the shared fake GhRunner ------------------------------------------


def _node(number: int, head: str, base: str = "main") -> dict[str, object]:
    return {"number": number, "headRefName": head, "baseRefName": base, "createdAt": "2026-01-01T00:00:00Z"}


FakeFactory = Callable[..., FakeGh]


@pytest.fixture
def fake_gh() -> FakeFactory:
    return FakeGh


def _refs(*texts: str) -> list[PrRef]:
    return [ref for text in texts if (ref := parse_ref(text)) is not None]


def test_resolve_states_and_intersecting_chains(fake_gh: FakeFactory) -> None:
    fake = fake_gh(
        {
            "o/r": FakeRepo(
                states={1: "OPEN", 2: "OPEN", 3: "MERGED", 4: "CLOSED"},
                open_nodes=[_node(1, "a"), _node(2, "b", "a"), _node(7, "x"), _node(8, "y", "x")],
            )
        }
    )
    resolution = resolve(_refs("o/r#1", "o/r#2", "o/r#3", "o/r#4"), runner=fake)
    assert resolution.states == {
        PrRef("o", "r", 1): PrState("OPEN"),
        PrRef("o", "r", 2): PrState("OPEN"),
        PrRef("o", "r", 3): PrState("MERGED"),
        PrRef("o", "r", 4): PrState("CLOSED"),
    }
    # The 7 <- 8 stack has no requested member, so it is not returned.
    assert [(c.kind, c.members) for c in resolution.chains] == [("stack", (PrRef("o", "r", 1), PrRef("o", "r", 2)))]
    assert resolution.errors == []


def test_missing_pr_is_unknown_but_the_rest_resolve(fake_gh: FakeFactory) -> None:
    fake = fake_gh({"o/r": FakeRepo(states={1: "OPEN"})})
    states = resolve_pr_states(_refs("o/r#1", "o/r#99999"), runner=fake)
    assert states[PrRef("o", "r", 1)] == PrState("OPEN")
    assert states[PrRef("o", "r", 99999)] == PrState(
        "UNKNOWN", "Could not resolve to a PullRequest with the number of 99999."
    )


def test_whole_repo_failure_is_unknown_with_no_chains(fake_gh: FakeFactory) -> None:
    fake = fake_gh({"o/r": FakeRepo(states={1: "OPEN"}), "o/gone": FakeRepo(fail="Could not resolve to a Repository")})
    resolution = resolve(_refs("o/r#1", "o/gone#2"), runner=fake)
    assert resolution.states[PrRef("o", "r", 1)] == PrState("OPEN")
    assert resolution.states[PrRef("o", "gone", 2)].state == "UNKNOWN"
    assert resolution.errors == ["o/gone: Could not resolve to a Repository"]
    assert resolution.failed_repos == {"o/gone": "Could not resolve to a Repository"}


def test_unreadable_open_list_fails_the_repo_instead_of_building_partial_chains(fake_gh: FakeFactory) -> None:
    fake = fake_gh({"o/r": FakeRepo(states={1: "OPEN"}, open_nodes=[_node(1, "a"), {"headRefName": "no-number"}])})
    resolution = resolve(_refs("o/r#1"), runner=fake)
    assert resolution.chains == []
    assert resolution.errors == ["o/r: unreadable open-PR list"]


def test_gh_missing_is_unknown_everywhere(fake_gh: FakeFactory) -> None:
    fake = fake_gh({"o/r": FakeRepo(states={1: "OPEN"})}, missing=True)
    resolution = resolve(_refs("o/r#1"), runner=fake)
    assert resolution.states == {PrRef("o", "r", 1): PrState("UNKNOWN", "gh is not on PATH")}
    assert resolution.errors == ["o/r: gh is not on PATH"]


def test_gh_timeout_is_unknown() -> None:
    def slow(_query: str) -> GhResult:
        return GhResult(124, "", "gh timed out after 30s")

    status = resolve_pr_states(_refs("o/r#1"), runner=slow)[PrRef("o", "r", 1)]
    assert status.state == "UNKNOWN"
    assert "timed out" in status.error


def test_open_list_paginates_before_chains(fake_gh: FakeFactory) -> None:
    # The stack's two members land on different pages.
    nodes = [_node(n, f"h{n}") for n in range(10, 15)] + [_node(2, "b", "a"), _node(1, "a")]
    fake = fake_gh({"o/r": FakeRepo(states={1: "OPEN", 2: "OPEN"}, open_nodes=nodes, page_size=3)})
    resolution = resolve(_refs("o/r#2"), runner=fake)
    assert len(fake.queries) == 3
    assert [c.members for c in resolution.chains] == [(PrRef("o", "r", 1), PrRef("o", "r", 2))]
    # Lookups ride on the first page only.
    assert "pr2:" in fake.queries[0]
    assert all("pr2:" not in q for q in fake.queries[1:])


def test_lookups_are_chunked(fake_gh: FakeFactory, monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setattr(github_prs, "LOOKUPS_PER_QUERY", 2)
    fake = fake_gh({"o/r": FakeRepo(states=dict.fromkeys(range(1, 6), "MERGED"))})
    states = resolve_pr_states(_refs(*(f"o/r#{n}" for n in range(1, 6))), runner=fake)
    assert len(states) == 5
    assert {s.state for s in states.values()} == {"MERGED"}
    assert len(fake.queries) == 3
    assert "open:" in fake.queries[0]
    assert all("open:" not in q for q in fake.queries[1:])


def test_repos_are_queried_separately(fake_gh: FakeFactory) -> None:
    fake = fake_gh({"o/a": FakeRepo(states={1: "OPEN"}), "o/b": FakeRepo(states={1: "MERGED"})})
    states = resolve_pr_states(_refs("o/a#1", "o/b#1"), runner=fake)
    assert states == {PrRef("o", "a", 1): PrState("OPEN"), PrRef("o", "b", 1): PrState("MERGED")}
    assert len(fake.queries) == 2


def test_no_refs_runs_nothing(fake_gh: FakeFactory) -> None:
    fake = fake_gh({})
    resolution = resolve([], runner=fake)
    assert resolution.states == {}
    assert resolution.chains == []
    assert fake.queries == []
