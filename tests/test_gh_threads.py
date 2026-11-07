"""`rig gh threads` (TEMPORARY): list, reply to and resolve PR review threads through a fake GitHub."""

from __future__ import annotations

import json

import pytest
from typer.testing import CliRunner, Result

from rig.gh_threads.cli import make_app
from rig.gh_threads.model import summarise
from rig.repo_slug import RepoSlug
from tests.fakes.gh_graphql import FakeGitHub, FakePR, FakeThread, Write, as_port

pytestmark = pytest.mark.ci

REPO = "octo/repo"
FIX = "3f1c2a9b7d4e5f60718293a4b5c6d7e8f9012345"
OTHER = "aa11bb22cc33dd44ee55ff6600778899aabbccdd"
CODEX_BODY = (
    "**<sub><sub>![P1 Badge](https://img.shields.io/badge/P1-orange?style=flat)</sub></sub>  "
    "Use the portable helper for the dangling file symlink**\n\nOn Windows without Developer Mode...\n\n"
    "Useful? React with 👍 / 👎.\n\n<!-- codex-code-review-finding:v1 -->"
)


def _github(**kwargs: int) -> FakeGitHub:
    return FakeGitHub(
        {
            f"{REPO}#7": FakePR(
                head_ref="feat/x",
                commits=[OTHER, FIX],
                threads=[
                    FakeThread("PRRT_open", "src/a.py", 12, CODEX_BODY),
                    FakeThread("PRRT_old", "src/b.py", 40, "Rename `snake_case_name`", outdated=True),
                    FakeThread("PRRT_done", "src/c.py", 3, "fine", resolved=True),
                ],
            )
        },
        **kwargs,
    )


def _run(github: FakeGitHub, *args: str, origin: RepoSlug | None = None) -> Result:
    app = make_app(as_port(github), origin=lambda: origin)
    return CliRunner().invoke(app, ["threads", *args])


def _resolve(github: FakeGitHub, *args: str) -> Result:
    return _run(github, "resolve", "7", "PRRT_open", "--repo", REPO, *args)


# -- list -------------------------------------------------------------------------


def test_list_shows_unresolved_threads_with_location_flag_author_and_clean_summary() -> None:
    result = _run(_github(), "list", "7", "--repo", REPO)

    assert result.exit_code == 1, result.output
    lines = result.output.splitlines()
    assert lines[0] == "octo/repo#7: 2 unresolved review thread(s)"
    assert "PRRT_open  src/a.py:12  @chatgpt-codex-connector" in lines
    assert "    [P1] Use the portable helper for the dangling file symlink" in lines
    assert "PRRT_old  src/b.py:40 (outdated)  @chatgpt-codex-connector" in lines
    assert "PRRT_done" not in result.output


def test_list_reads_every_page() -> None:
    github = _github(page_size=1)

    result = _run(github, "list", "7", "--repo", REPO, "--json")

    assert [thread["id"] for thread in json.loads(result.output)] == ["PRRT_open", "PRRT_old"]
    assert github.calls == ["ReviewThreads"] * 3


def test_list_json_and_a_clean_pr_exits_zero() -> None:
    github = _github()
    for thread in github.prs[f"{REPO}#7"].threads:
        thread.resolved = True

    as_json = _run(github, "list", "7", "--repo", REPO, "--json")
    text = _run(github, "list", "7", "--repo", REPO)

    assert (as_json.exit_code, json.loads(as_json.output)) == (0, [])
    assert (text.exit_code, text.output.strip()) == (0, "octo/repo#7: no unresolved review threads")


def test_list_defaults_to_the_origin_repo_and_errors_without_one() -> None:
    found = _run(_github(), "list", "7", origin=RepoSlug("octo", "repo"))
    missing = _run(_github(), "list", "7")

    assert found.exit_code == 1
    assert missing.exit_code == 2
    assert "pass --repo owner/name" in missing.output


@pytest.mark.parametrize(
    ("args", "message"),
    [
        (["list", "8", "--repo", REPO], "octo/repo#8: no such pull request"),
        (["list", "7", "--repo", "not-a-slug"], "is not one"),
    ],
)
def test_list_errors_exit_two(args: list[str], message: str) -> None:
    result = _run(_github(), *args)
    assert result.exit_code == 2
    assert message in result.output


def test_a_graphql_error_exits_two_with_githubs_message() -> None:
    github = _github()
    github.failures["ReviewThreads"] = "Resource not accessible by integration"

    result = _run(github, "list", "7", "--repo", REPO)

    assert result.exit_code == 2
    assert "Resource not accessible by integration" in result.output


# -- resolve ----------------------------------------------------------------------


def test_resolve_replies_with_the_fix_sha_and_note_then_resolves() -> None:
    github = _github()

    result = _resolve(github, "--sha", FIX[:9], "--note", "Used file_symlink_or_skip.")

    assert result.exit_code == 0, result.output
    assert github.writes == [
        Write("ReplyToThread", "PRRT_open", f"Fixed in {FIX}.\n\nUsed file_symlink_or_skip."),
        Write("ResolveThread", "PRRT_open"),
    ]
    assert "PRRT_open (src/a.py:12): replied https://" in result.output
    assert f"resolved (fix {FIX[:12]})" in result.output


def test_resolve_finds_the_fix_on_a_later_page_of_commits() -> None:
    github = _github(page_size=1)
    assert _resolve(github, "--sha", FIX).exit_code == 0
    assert github.prs[f"{REPO}#7"].threads[0].resolved


@pytest.mark.parametrize(
    ("sha", "reason"),
    [
        ("0123456789abcdef0123456789abcdef01234567", "is not a commit on the PR's head branch feat/x"),
        ("3f1c2a", "need at least 7 hex characters"),
        ("not-a-sha", "is not a commit SHA"),
    ],
)
def test_resolve_refuses_a_fix_that_is_not_on_the_pr(sha: str, reason: str) -> None:
    github = _github()

    result = _resolve(github, "--sha", sha)

    assert result.exit_code == 1
    assert reason in result.output
    assert github.writes == []
    assert not github.prs[f"{REPO}#7"].threads[0].resolved


def test_resolve_refuses_an_ambiguous_prefix() -> None:
    github = _github()
    github.prs[f"{REPO}#7"].commits.append(FIX[:8] + "0" * 32)

    result = _resolve(github, "--sha", FIX[:8])

    assert result.exit_code == 1
    assert "ambiguous" in result.output
    assert github.writes == []


def test_resolve_refuses_a_thread_from_another_pr() -> None:
    github = _github()

    result = _run(github, "resolve", "7", "PRRT_elsewhere", "--sha", FIX, "--repo", REPO)

    assert result.exit_code == 1
    assert "PRRT_elsewhere is not a review thread on this pull request" in result.output
    assert github.writes == []


def test_dry_run_writes_nothing() -> None:
    github = _github()

    result = _resolve(github, "--sha", FIX, "--dry-run", "--json")

    assert result.exit_code == 0
    assert json.loads(result.output) == {
        "thread_id": "PRRT_open",
        "sha": FIX,
        "reply_url": None,
        "replied": False,
        "resolved": False,
        "dry_run": True,
    }
    assert github.writes == []


def test_a_second_run_changes_nothing() -> None:
    github = _github()
    _resolve(github, "--sha", FIX)

    again = _resolve(github, "--sha", FIX)

    assert again.exit_code == 0
    assert "nothing to do" in again.output
    assert len(github.writes) == 2


def test_a_failed_resolve_is_retried_without_repeating_the_reply() -> None:
    github = _github()
    github.failures["ResolveThread"] = "Resource not accessible by integration"

    first = _resolve(github, "--sha", FIX)
    del github.failures["ResolveThread"]
    second = _resolve(github, "--sha", FIX)

    assert first.exit_code == 2
    assert "Replied (https://" in first.output
    assert "Resource not accessible by integration" in first.output
    assert second.exit_code == 0
    assert "reply already posted; resolved" in second.output
    assert [write.operation for write in github.writes] == ["ReplyToThread", "ResolveThread"]


def test_a_retry_with_a_changed_note_posts_the_new_note() -> None:
    github = _github()
    github.failures["ResolveThread"] = "Resource not accessible by integration"
    _resolve(github, "--sha", FIX, "--note", "First try.")
    del github.failures["ResolveThread"]

    changed = _resolve(github, "--sha", FIX, "--note", "Also covered the outdated path.")

    assert changed.exit_code == 0
    assert [write.body for write in github.writes if write.operation == "ReplyToThread"] == [
        f"Fixed in {FIX}.\n\nFirst try.",
        f"Fixed in {FIX}.\n\nAlso covered the outdated path.",
    ]


def test_a_retry_without_a_note_keeps_the_earlier_noted_reply() -> None:
    github = _github()
    github.failures["ResolveThread"] = "Resource not accessible by integration"
    _resolve(github, "--sha", FIX, "--note", "First try.")
    del github.failures["ResolveThread"]

    plain = _resolve(github, "--sha", FIX)

    assert plain.exit_code == 0
    assert [write.operation for write in github.writes] == ["ReplyToThread", "ResolveThread"]


# -- summaries ----------------------------------------------------------------------


@pytest.mark.parametrize(
    ("body", "summary"),
    [
        (CODEX_BODY, "[P1] Use the portable helper for the dangling file symlink"),
        (
            "_⚠️ Potential issue_ | _🟠 Major_\n\n**Guard against a missing cursor.**\n\nDetails...\n\n"
            "<details>\n<summary>🤖 Prompt for AI Agents</summary>\n\nfix it\n</details>",
            "⚠️ Potential issue | 🟠 Major — Guard against a missing cursor.",
        ),
        (
            "Keep `snake_case_name` and __init__ intact? No: `x_y` stays.",
            "Keep `snake_case_name` and init intact? No: `x_y` stays.",
        ),
        ("<!-- only a marker -->", ""),
        ("word " * 40, ("word " * 24).strip() + "…"),
    ],
)
def test_summaries_strip_badges_html_and_emphasis(body: str, summary: str) -> None:
    assert summarise(body) == summary
