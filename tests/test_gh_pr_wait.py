"""`rig gh pr wait|land`: wait for a PR to land through a fake GitHub and a clock that never sleeps."""

from __future__ import annotations

import json

import pytest
from typer.testing import CliRunner, Result

from rig.gh_pr.cli import Ports, make_app, parse_duration
from rig.gh_pr.wait import Clock
from rig.repo_slug import RepoSlug
from tests.fakes.clock import FakeClock
from tests.fakes.gh_graphql import FakeCheck, FakeGitHub, FakeLanding, FakePR, FakeThread
from tests.fakes.github_rest import FakeGitHubApi

pytestmark = pytest.mark.ci

REPO = "octo/repo"
RULES_ROUTE = "repos/octo/repo/rules/branches/main?per_page=100"
THREADS_REQUIRED = {"type": "pull_request", "parameters": {"required_review_thread_resolution": True}}
BUILD_REQUIRED = {"type": "required_status_checks", "parameters": {"required_status_checks": [{"context": "build"}]}}
LINEAR = {"type": "required_linear_history", "parameters": None}

BUILD_OK = FakeCheck("build", required=True)
BUILD_RUNNING = FakeCheck("build", status="IN_PROGRESS", required=True)
LINT_OK = FakeCheck("lint")
GREEN = (BUILD_OK, LINT_OK)
MERGED = FakeLanding(state="MERGED", merge_state="UNKNOWN", auto_merge=False, checks=GREEN)
CLEAN = FakeLanding(merge_state="CLEAN", checks=GREEN)


class Harness:
    """A fake GitHub holding one PR, its base branch's rules, and a clock that only moves on sleep."""

    def __init__(
        self,
        *landing: FakeLanding,
        threads: int = 0,
        rules: list[dict[str, object]] | None = None,
        page_size: int = 100,
    ) -> None:
        open_threads = [FakeThread(f"PRRT_{n}", "src/a.py", n + 1, f"Fix thing {n}") for n in range(threads)]
        done = FakeThread("PRRT_done", "src/c.py", 3, "fine", resolved=True)
        pr = FakePR(head_ref="feat/x", commits=["a" * 40], threads=[*open_threads, done], landing=list(landing))
        self.github: FakeGitHub = FakeGitHub({f"{REPO}#7": pr}, page_size=page_size)
        self.api: FakeGitHubApi = FakeGitHubApi(
            {RULES_ROUTE: [LINEAR, THREADS_REQUIRED, BUILD_REQUIRED] if rules is None else rules}
        )
        self.clock: FakeClock = FakeClock()

    def run(self, *args: str, command: str = "wait", origin: RepoSlug | None = None) -> Result:
        ports = Ports(
            graphql=self.github,
            api=self.api,
            merge=self.github.merge,
            origin=lambda: origin,
            clock=Clock(monotonic=self.clock.monotonic, sleep=self.clock.sleep),
        )
        return CliRunner().invoke(make_app(ports), [command, "7", *([] if origin else ["--repo", REPO]), *args])

    def report(self, *args: str, command: str = "wait") -> dict[str, object]:
        result = self.run("--json", *args, command=command)
        report: dict[str, object] = json.loads(result.stdout)
        return {**report, "exit": result.exit_code}


def _verdict(report: dict[str, object]) -> list[object]:
    return [report["exit"], report["outcome"], report["reason"]]


# -- the incident ------------------------------------------------------------------


def test_green_checks_with_unresolved_threads_stop_on_the_first_poll() -> None:
    """Auto-merge queued, every check green, BLOCKED by 14 open threads: the wait that stalled for 30 minutes."""
    harness = Harness(FakeLanding(merge_state="BLOCKED", checks=GREEN), threads=14)

    result = harness.run()

    assert result.exit_code == 1, result.output
    assert "octo/repo#7: BLOCKED (unresolved_threads) after 0:00: 14 unresolved review thread(s)" in result.stdout
    assert "  PRRT_0  src/a.py:1  @chatgpt-codex-connector" in result.stdout
    assert "PRRT_done" not in result.stdout
    assert "uv run rig gh threads resolve 7 <thread> --sha <fix> --repo octo/repo" in result.stdout
    assert harness.clock.sleeps == []


def test_json_report_carries_the_threads_and_nothing_else_on_stdout() -> None:
    report = Harness(FakeLanding(checks=GREEN), threads=2).report()

    assert _verdict(report) == [1, "blocked", "unresolved_threads"]
    assert report["unresolved_threads"] == [
        {
            "id": f"PRRT_{n}",
            "location": f"src/a.py:{n + 1}",
            "author": "chatgpt-codex-connector",
            "summary": f"Fix thing {n}",
            "url": f"https://github.com/c/PRRT_{n}",
        }
        for n in range(2)
    ]
    assert (report["repo"], report["pr"], report["merge_state"], report["auto_merge"]) == (REPO, 7, "BLOCKED", True)


def test_threads_block_even_while_checks_are_still_running() -> None:
    report = Harness(FakeLanding(checks=(BUILD_RUNNING,)), threads=1).report()
    assert _verdict(report) == [1, "blocked", "unresolved_threads"]


def test_threads_do_not_block_when_no_rule_requires_them_resolved() -> None:
    harness = Harness(FakeLanding(checks=(BUILD_RUNNING,)), MERGED, threads=3, rules=[LINEAR])

    assert _verdict(harness.report()) == [0, "merged", "merged"]
    assert "ReviewThreads" not in harness.github.calls


def test_classic_branch_protection_can_require_thread_resolution() -> None:
    harness = Harness(FakeLanding(checks=GREEN, classic_thread_resolution=True), threads=1, rules=[])
    assert _verdict(harness.report()) == [1, "blocked", "unresolved_threads"]


def test_unreadable_rules_still_name_the_threads_once_nothing_is_pending() -> None:
    harness = Harness(FakeLanding(checks=(BUILD_RUNNING,)), FakeLanding(checks=GREEN), threads=2)
    harness.api.failures["GET", RULES_ROUTE] = "gh: Not Found (HTTP 404)"

    report = harness.report()

    assert _verdict(report) == [1, "blocked", "unresolved_threads"]
    assert "rules could not be read" in str(report["message"])
    assert report["polls"] == 2


# -- landing ------------------------------------------------------------------------


def test_waits_through_pending_checks_and_the_merge_queue_reporting_each_change() -> None:
    harness = Harness(
        FakeLanding(checks=(BUILD_RUNNING, FakeCheck("lint", status="QUEUED"))),
        FakeLanding(checks=(BUILD_RUNNING, LINT_OK)),
        CLEAN,
        FakeLanding(auto_merge=False, queue_position=2, checks=GREEN),
        FakeLanding(auto_merge=False, queue_position=1, checks=GREEN),
        MERGED,
    )

    result = harness.run()

    assert result.exit_code == 0, result.output
    assert result.stdout.strip() == "octo/repo#7: merged after 0:25"
    assert result.stderr.splitlines() == [
        "octo/repo#7 [0:00] 2 check(s) pending: build, lint (next look in 5s)",
        "octo/repo#7 [0:05] 1 check(s) pending: build (next look in 5s)",
        "octo/repo#7 [0:10] every requirement is met; waiting for GitHub to merge or enqueue it (next look in 5s)",
        "octo/repo#7 [0:15] in the merge queue at position 2 (AWAITING_CHECKS) (next look in 5s)",
        "octo/repo#7 [0:20] in the merge queue at position 1 (AWAITING_CHECKS) (next look in 5s)",
    ]


def test_backs_off_while_nothing_changes_and_resets_when_something_does() -> None:
    running = FakeLanding(checks=(BUILD_RUNNING,))
    harness = Harness(*[running] * 9, CLEAN, MERGED)

    result = harness.run()

    assert result.exit_code == 0
    assert harness.clock.sleeps == [5, 7.5, 11.25, 16.875, 25.3125, 37.96875, 56.953125, 60, 60, 5]
    assert len(result.stderr.splitlines()) == 2


def test_a_required_check_that_has_not_reported_yet_counts_as_pending() -> None:
    harness = Harness(FakeLanding(checks=(LINT_OK,)), MERGED)

    result = harness.run()

    assert result.exit_code == 0
    assert "1 check(s) pending: build" in result.stderr


def test_merge_state_still_being_computed_is_waited_out() -> None:
    harness = Harness(FakeLanding(merge_state="UNKNOWN", mergeable="UNKNOWN", checks=GREEN), MERGED)

    result = harness.run()

    assert result.exit_code == 0
    assert "GitHub is still working out whether the PR can merge" in result.stderr


def test_checks_are_read_across_pages() -> None:
    failed = FakeCheck("build", conclusion="FAILURE", required=True)
    harness = Harness(FakeLanding(checks=(LINT_OK, FakeCheck("docs"), failed)), page_size=1)

    assert _verdict(harness.report()) == [1, "blocked", "check_failed"]
    assert harness.github.calls.count("PullRequestLanding") == 3


# -- blocked at once ------------------------------------------------------------------


@pytest.mark.parametrize(
    ("landing", "reason", "message"),
    [
        (
            FakeLanding(checks=(FakeCheck("build", conclusion="FAILURE", required=True), BUILD_RUNNING)),
            "check_failed",
            "1 required check(s) failed or were cancelled: build",
        ),
        (FakeLanding(checks=(FakeCheck("build", conclusion="CANCELLED", required=True),)), "check_failed", "build"),
        (
            FakeLanding(
                checks=(BUILD_OK, FakeCheck("ci/legacy", conclusion="ERROR", required=True, status_context=True))
            ),
            "check_failed",
            "ci/legacy",
        ),
        (FakeLanding(review_decision="CHANGES_REQUESTED", checks=GREEN), "changes_requested", "requested changes"),
        (FakeLanding(review_decision="REVIEW_REQUIRED", checks=GREEN), "review_required", "approving review"),
        (FakeLanding(merge_state="DIRTY", mergeable="CONFLICTING", checks=GREEN), "conflicts", "conflicts with main"),
        (FakeLanding(draft=True, merge_state="DRAFT", checks=GREEN), "draft", "is a draft"),
        (FakeLanding(state="CLOSED", auto_merge=False, checks=GREEN), "closed", "closed without merging"),
    ],
)
def test_a_named_cause_stops_the_wait_at_once(landing: FakeLanding, reason: str, message: str) -> None:
    harness = Harness(landing)

    report = harness.report()

    assert _verdict(report) == [1, "blocked", reason]
    assert message in str(report["message"])
    assert harness.clock.sleeps == []


def test_a_failed_check_that_is_not_required_does_not_stop_the_wait() -> None:
    flaky = FakeCheck("optional", conclusion="FAILURE")
    pending = FakeCheck("ci/legacy", conclusion="PENDING", status_context=True)
    harness = Harness(FakeLanding(merge_state="UNSTABLE", checks=(BUILD_OK, flaky, pending)), MERGED)

    assert _verdict(harness.report()) == [0, "merged", "merged"]


# -- blocked once it lasts ------------------------------------------------------------


@pytest.mark.parametrize(
    ("landing", "reason", "message"),
    [
        (FakeLanding(merge_state="BLOCKED", checks=GREEN), "blocked", "BLOCKED with no check pending"),
        (FakeLanding(merge_state="BEHIND", checks=GREEN), "behind", "behind main"),
        (FakeLanding(merge_state="BLOCKED", auto_merge=False, checks=GREEN), "auto_merge_off", "auto-merge is off"),
        (
            FakeLanding(merge_state="CLEAN", auto_merge=False, checks=GREEN),
            "auto_merge_off",
            "auto-merge is off although it is ready to merge",
        ),
    ],
)
def test_a_state_with_no_named_cause_must_last_the_settle_time(landing: FakeLanding, reason: str, message: str) -> None:
    harness = Harness(landing)

    report = harness.report()

    assert _verdict(report) == [1, "blocked", reason]
    assert message in str(report["message"])
    assert report["waited_seconds"] == 30
    assert harness.clock.sleeps == [5] * 6


def test_a_blocked_state_that_clears_within_the_settle_time_is_not_reported() -> None:
    harness = Harness(FakeLanding(merge_state="BLOCKED", checks=GREEN), CLEAN, MERGED)

    result = harness.run()

    assert result.exit_code == 0
    assert "(confirming for 30s before giving up)" in result.stderr


def test_leaving_the_merge_queue_without_merging_is_blocked() -> None:
    queued = FakeLanding(auto_merge=False, queue_position=1, checks=GREEN)
    harness = Harness(queued, FakeLanding(auto_merge=False, checks=GREEN))

    report = harness.report("--settle", "10s")

    assert _verdict(report) == [1, "blocked", "removed_from_merge_queue"]
    assert report["waited_seconds"] == 15


# -- timeout and errors -----------------------------------------------------------------


def test_timeout_exits_three_and_says_what_it_was_waiting_on() -> None:
    harness = Harness(FakeLanding(checks=(BUILD_RUNNING,)))

    result = harness.run("--timeout", "2m")

    assert result.exit_code == 3
    assert "octo/repo#7: TIMEOUT (checks_pending) after 2:00: timed out; last seen: 1 check(s) pending: build" in (
        result.stdout
    )
    assert sum(harness.clock.sleeps) == 120


def test_timeout_zero_looks_once() -> None:
    waiting = Harness(FakeLanding(checks=(BUILD_RUNNING,)))
    unsettled = Harness(FakeLanding(merge_state="BLOCKED", checks=GREEN))

    assert _verdict(waiting.report("--timeout", "0")) == [3, "timeout", "checks_pending"]
    assert _verdict(unsettled.report("--timeout", "0")) == [1, "blocked", "blocked"]
    assert waiting.clock.sleeps == unsettled.clock.sleeps == []


def test_a_few_failed_reads_mid_wait_are_retried() -> None:
    outage = FakeLanding(error="502 Bad Gateway")
    harness = Harness(FakeLanding(checks=(BUILD_RUNNING,)), outage, outage, MERGED)

    result = harness.run()

    assert result.exit_code == 0
    assert "GitHub read failed (2 in a row), retrying" in result.stderr
    assert "502 Bad Gateway" in result.stderr


def test_too_many_failed_reads_in_a_row_exit_two() -> None:
    harness = Harness(FakeLanding(checks=(BUILD_RUNNING,)), FakeLanding(error="502 Bad Gateway"))

    report = harness.report()

    assert _verdict(report) == [2, "error", "github_error"]
    assert report["polls"] == 1
    assert "502 Bad Gateway" in str(report["message"])


def test_a_failed_first_read_exits_two_without_retrying() -> None:
    harness = Harness(CLEAN)
    harness.github.prs.clear()

    result = harness.run()

    assert result.exit_code == 2
    assert "octo/repo#7: no such pull request" in result.stderr
    assert result.stdout == ""
    assert harness.clock.sleeps == []


def test_repo_defaults_to_the_origin_and_a_bad_duration_is_refused() -> None:
    assert Harness(MERGED).run(origin=RepoSlug("octo", "repo")).exit_code == 0
    assert Harness(MERGED).run("--timeout", "soon").exit_code == 2
    assert [parse_duration(text) for text in ("90s", "45m", "2h", "5")] == [90, 2700, 7200, 300]


# -- land ---------------------------------------------------------------------------


def test_land_queues_auto_merge_then_waits() -> None:
    unqueued = FakeLanding(auto_merge=False, checks=(BUILD_RUNNING,))
    harness = Harness(unqueued, unqueued, CLEAN, MERGED)

    result = harness.run(command="land")

    assert result.exit_code == 0, result.output
    assert harness.github.merges == ["octo/repo#7"]
    assert "octo/repo#7: queued squash auto-merge" in result.stderr


@pytest.mark.parametrize(
    "first",
    [FakeLanding(checks=(BUILD_RUNNING,)), FakeLanding(auto_merge=False, queue_position=1, checks=GREEN), MERGED],
)
def test_land_does_not_queue_again_when_already_queued_or_merged(first: FakeLanding) -> None:
    harness = Harness(first, MERGED)

    assert harness.run(command="land").exit_code == 0
    assert harness.github.merges == []


def test_land_reports_a_refused_merge_and_an_unreadable_pr() -> None:
    refused = Harness(FakeLanding(auto_merge=False, checks=GREEN))
    refused.github.merge_error = "GraphQL: Pull request is in clean status"
    missing = Harness(CLEAN)
    missing.github.prs.clear()

    refused_result = refused.run(command="land")
    missing_result = missing.run(command="land")

    assert (refused_result.exit_code, missing_result.exit_code) == (2, 2)
    assert "Could not queue auto-merge: GraphQL: Pull request is in clean status" in refused_result.stderr
    assert "no such pull request" in missing_result.stderr
