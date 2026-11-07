from __future__ import annotations

import json
import pathlib
import subprocess
from collections.abc import Iterator

import pytest

from rig.estate import prs
from rig.estate.prs import PullRequest
from rig.proc import GhResult
from rig.repo_slug import RepoSlug

pytestmark = pytest.mark.ci


def _page(nodes: list[object], *, has_next: bool = False, cursor: str | None = None) -> dict[str, object]:
    return {"pageInfo": {"hasNextPage": has_next, "endCursor": cursor}, "nodes": nodes}


def _node(number: int, state: str, head: str) -> dict[str, object]:
    return {"number": number, "state": state, "headRefName": head}


class FakeGh:
    """Answers calls in order: `git remote get-url origin` first, then each `gh` GraphQL page."""

    def __init__(self, *stdouts: str, returncode: int = 0) -> None:
        self._stdouts: Iterator[str] = iter(stdouts)
        self._returncode = returncode
        self.calls: list[list[str]] = []

    def __call__(self, args: list[str], **_kwargs: object) -> subprocess.CompletedProcess[str]:
        self.calls.append(args)
        return subprocess.CompletedProcess(args, self._returncode, next(self._stdouts, ""), "")


_VIEW = "git@github.com:octo/repo.git\n"


def _recent(page: dict[str, object]) -> str:
    return json.dumps({"data": {"repository": {"pullRequests": page}}})


def _mine(page: dict[str, object]) -> str:
    return json.dumps({"data": {"search": page}})


def test_origin_slug_reads_the_origin_remote_without_gh(monkeypatch: pytest.MonkeyPatch) -> None:
    fake = FakeGh(_VIEW)
    monkeypatch.setattr(subprocess, "run", fake)
    assert prs.origin_slug(pathlib.Path()) == RepoSlug(owner="octo", name="repo")
    assert fake.calls == [["git", "remote", "get-url", "origin"]]


@pytest.mark.parametrize("stdout", ["", "https://gitlab.com/octo/repo.git", "/srv/git/repo"])
def test_origin_slug_is_none_without_a_github_origin(monkeypatch: pytest.MonkeyPatch, stdout: str) -> None:
    monkeypatch.setattr(subprocess, "run", FakeGh(stdout))
    assert prs.origin_slug(pathlib.Path()) is None


def test_origin_slug_is_none_when_git_fails(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setattr(subprocess, "run", FakeGh(_VIEW, returncode=2))
    assert prs.origin_slug(pathlib.Path()) is None


class FakeRunner:
    """A `GhRunner` answering GraphQL queries in order, recording each one."""

    def __init__(self, *stdouts: str) -> None:
        self._stdouts: Iterator[str] = iter(stdouts)
        self.queries: list[str] = []

    def __call__(self, query: str) -> GhResult:
        self.queries.append(query)
        out = next(self._stdouts, "")
        return GhResult(0 if out else 1, out, "")


@pytest.fixture
def octo(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setattr(prs, "origin_slug", lambda _repo: RepoSlug("octo", "repo"))


def test_newest_pr_wins_per_head_across_both_passes(octo: None) -> None:
    fake = FakeRunner(
        _recent(_page([_node(5, "MERGED", "feat"), _node(7, "OPEN", "other")], has_next=True, cursor="c1")),
        _recent(_page([_node(3, "CLOSED", "old"), None])),
        _mine(_page([_node(9, "OPEN", "feat"), {}, _node(1, "MERGED", "other")])),
    )

    heads = prs.pr_heads(pathlib.Path(), limit=400, runner=fake)

    assert heads == {
        "feat": PullRequest(number=9, state="OPEN", head_ref="feat"),
        "other": PullRequest(number=7, state="OPEN", head_ref="other"),
        "old": PullRequest(number=3, state="CLOSED", head_ref="old"),
    }
    assert 'repository(owner: "octo", name: "repo")' in fake.queries[0]
    assert "after:" not in fake.queries[0]
    assert 'after: "c1"' in fake.queries[1]
    assert '"repo:octo/repo is:pr author:@me"' in fake.queries[2]


def test_merged_heads_skips_a_branch_reused_after_merge(octo: None) -> None:
    fake = FakeRunner(
        _recent(_page([_node(5, "MERGED", "reused"), _node(6, "OPEN", "reused"), _node(2, "MERGED", "done")])),
        _mine(_page([])),
    )
    assert prs.merged_heads(pathlib.Path(), runner=fake) == {"done": 2}


def test_a_failed_graphql_call_stops_paging(octo: None) -> None:
    fake = FakeRunner(_recent(_page([_node(1, "MERGED", "a")], has_next=True, cursor="c1")), "")
    assert prs.pr_heads(pathlib.Path(), limit=400, include_mine=False, runner=fake) == {
        "a": PullRequest(number=1, state="MERGED", head_ref="a")
    }
    assert len(fake.queries) == 2


def test_no_github_origin_asks_nothing(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setattr(prs, "origin_slug", lambda _repo: None)
    fake = FakeRunner()
    assert prs.pr_heads(pathlib.Path(), runner=fake) == {}
    assert fake.queries == []
