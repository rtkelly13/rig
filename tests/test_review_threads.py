"""Merge preflights must read every review thread and fail closed on incomplete data."""

import json
import subprocess
from unittest.mock import patch

import pytest
from typer.testing import CliRunner

from rig.repo_governance.align_repo_conventions import app
from rig.repo_governance.pr_triage import PRItem, merge_ready_pr
from rig.repo_governance.review_threads import inspect_review_threads

pytestmark = pytest.mark.ci
REPO = "rtkelly13/blog"


def _page(*, resolved=True, next_page=False, cursor=None, author="reviewer"):
    return {
        "data": {
            "repository": {
                "pullRequest": {
                    "reviewThreads": {
                        "nodes": [
                            {
                                "id": "thread-1",
                                "isResolved": resolved,
                                "comments": {"nodes": [{"author": {"login": author} if author else None}]},
                            }
                        ],
                        "pageInfo": {"hasNextPage": next_page, "endCursor": cursor},
                    }
                }
            }
        }
    }


def _response(payload, code=0):
    return subprocess.CompletedProcess(["gh"], code, json.dumps(payload), "API failed" if code else "")


def test_reads_later_pages_and_blocks_an_unresolved_bot_thread():
    with patch(
        "subprocess.run",
        side_effect=[
            _response(_page(next_page=True, cursor="page-2")),
            _response(_page(resolved=False, author="review-bot")),
        ],
    ) as run:
        result = inspect_review_threads(REPO, 42)
    assert not result.is_aligned
    assert "@review-bot" in result.message
    assert "cursor=page-2" in run.call_args_list[1].args[0]


def test_all_resolved_pages_pass():
    with patch(
        "subprocess.run",
        side_effect=[
            _response(_page(next_page=True, cursor="page-2")),
            _response(_page()),
        ],
    ):
        assert inspect_review_threads(REPO, 42).is_aligned


@pytest.mark.parametrize(
    "payload",
    [
        {},
        {"data": None},
        {"data": {"repository": None}},
        {"data": {"repository": {"pullRequest": None}}},
        {"data": {"repository": {"pullRequest": {}}}},
        {"data": {"repository": {"pullRequest": {"reviewThreads": {"nodes": []}}}}},
        {
            "data": {
                "repository": {
                    "pullRequest": {
                        "reviewThreads": {
                            "nodes": [],
                            "pageInfo": {"endCursor": None},
                        }
                    }
                }
            }
        },
        {**_page(), "errors": [{"message": "Partial response"}]},
    ],
)
def test_incomplete_or_error_responses_block(payload):
    with patch("subprocess.run", return_value=_response(payload)):
        assert not inspect_review_threads(REPO, 42).is_aligned


def test_missing_resolution_state_blocks():
    payload = _page()
    del payload["data"]["repository"]["pullRequest"]["reviewThreads"]["nodes"][0]["isResolved"]
    with patch("subprocess.run", return_value=_response(payload)):
        assert not inspect_review_threads(REPO, 42).is_aligned


@pytest.mark.parametrize("cursor", [None, ""])
def test_missing_pagination_cursor_blocks(cursor):
    with patch("subprocess.run", return_value=_response(_page(next_page=True, cursor=cursor))) as run:
        assert not inspect_review_threads(REPO, 42).is_aligned
    assert run.call_count == 1


def test_repeated_pagination_cursor_blocks_instead_of_looping():
    with patch("subprocess.run", return_value=_response(_page(next_page=True, cursor="same"))) as run:
        assert not inspect_review_threads(REPO, 42).is_aligned
    assert run.call_count == 2


@pytest.mark.parametrize("response", [_response({}, code=1), OSError("gh unavailable")])
def test_api_failures_block(response):
    with patch("subprocess.run", side_effect=[response]):
        assert not inspect_review_threads(REPO, 42).is_aligned


def test_deleted_author_does_not_hide_unresolved_thread():
    with patch("subprocess.run", return_value=_response(_page(resolved=False, author=None))):
        result = inspect_review_threads(REPO, 42)
    assert not result.is_aligned
    assert "@unknown" in result.message


def test_empty_thread_list_passes():
    payload = _page()
    payload["data"]["repository"]["pullRequest"]["reviewThreads"]["nodes"] = []
    with patch("subprocess.run", return_value=_response(payload)):
        assert inspect_review_threads(REPO, 42).is_aligned


@pytest.mark.parametrize("pr", ["feature/topic", "https://github.com/rtkelly13/blog/pull/42"])
def test_cli_resolves_branch_or_url_before_verifying_threads(pr):
    with patch(
        "subprocess.run",
        side_effect=[
            subprocess.CompletedProcess(["gh"], 0, "pr-checks\tpass\t1m\n", ""),
            _response({"number": 42, "mergeStateStatus": "CLEAN"}),
            _response(_page(resolved=False)),
        ],
    ) as run:
        result = CliRunner().invoke(app, ["verify-pr-checks", pr, "--repo", REPO])
    assert result.exit_code == 1
    assert "unresolved conversation" in result.output
    assert "pr=42" in run.call_args_list[-1].args[0]


@pytest.mark.parametrize("payload", [_page(resolved=False), {}])
def test_automated_merge_never_runs_when_threads_are_unresolved_or_unreadable(payload):
    pr = PRItem(repo=REPO, number=42, title="Change", url="", head_branch="feature")
    with patch("subprocess.run", return_value=_response(payload)) as run:
        result = merge_ready_pr(pr)
    assert not result.ok
    assert all("merge" not in call.args[0] for call in run.call_args_list)


def test_automated_merge_checks_threads_then_squash_merges():
    pr = PRItem(repo=REPO, number=42, title="Change", url="", head_branch="feature")
    with patch("subprocess.run", side_effect=[_response(_page()), _response({})]) as run:
        assert merge_ready_pr(pr).ok
    assert run.call_args_list[1].args[0] == [
        "gh",
        "pr",
        "merge",
        "42",
        "--repo",
        REPO,
        "--squash",
        "--delete-branch",
    ]
