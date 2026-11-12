"""`rig gh pr wait|land`: wait for a pull request to land, and stop with the reason when it cannot.

Exit codes: 0 merged, 1 blocked (someone has to act), 2 GitHub could not be read or the merge could
not be queued, 3 timed out while the PR was still moving.
"""

from __future__ import annotations

import re
from collections.abc import Callable
from dataclasses import dataclass, field

import typer
from pydantic import TypeAdapter

from rig import proc
from rig.gh_pr import github
from rig.gh_pr.model import Observation, Reason, Rules, ThreadRef
from rig.gh_pr.wait import GITHUB_ERROR, Clock, Outcome, Pace, Result, Tick, wait
from rig.gh_target import OriginReader, origin_slug, resolve_target
from rig.gh_threads.github import GitHubError, GraphQL, gh_graphql
from rig.proc import GitHubApi, Ran, gh_api
from rig.repo_slug import RepoSlug

EXIT_CODES: dict[Outcome, int] = {Outcome.MERGED: 0, Outcome.BLOCKED: 1, Outcome.ERROR: 2, Outcome.TIMEOUT: 3}

_DURATION = re.compile(r"^\s*(?P<n>\d+)\s*(?P<unit>[smh]?)\s*$")
_UNIT_SECONDS = {"s": 1, "m": 60, "": 60, "h": 3600}
_TIMEOUT_HELP = "Give up after this long: 45m, 2h, 90s; a bare number is minutes. 0 looks once and reports."
_SETTLE_HELP = (
    "How long a state with no named cause (BLOCKED or BEHIND with nothing pending, auto-merge off, "
    "out of the merge queue) must last before the wait stops. GitHub lags between the last check passing "
    "and merging."
)

type Merger = Callable[[RepoSlug, int], Ran]


def queue_auto_merge(repo: RepoSlug, number: int) -> Ran:
    """The real `Merger`: ``gh pr merge <n> --auto --squash``."""
    return proc.gh("pr", "merge", str(number), "--auto", "--squash", "--repo", str(repo))


@dataclass(frozen=True, slots=True)
class Ports:
    """Everything `rig gh pr` touches outside the process."""

    graphql: GraphQL = gh_graphql
    api: GitHubApi = gh_api
    merge: Merger = queue_auto_merge
    origin: OriginReader = origin_slug
    clock: Clock = field(default_factory=Clock)


@dataclass(frozen=True, slots=True)
class Report:
    """The final answer, as ``--json`` prints it."""

    repo: str
    pr: int
    url: str
    #: merged, blocked, timeout or error.
    outcome: str
    #: Why: a `rig.gh_pr.model.Reason` value, or ``github_error``.
    reason: str
    message: str
    waited_seconds: int
    polls: int
    merge_state: str
    auto_merge: bool
    queue_position: int | None
    pending_checks: tuple[str, ...]
    failed_checks: tuple[str, ...]
    unresolved_threads: tuple[ThreadRef, ...]


_REPORT: TypeAdapter[Report] = TypeAdapter(Report)


def parse_duration(text: str) -> float:
    """Seconds in `45m`, `2h` or `90s`; a bare number is minutes."""
    found = _DURATION.match(text)
    if found is None:
        raise typer.BadParameter(f"{text!r}: expected a duration such as 45m, 2h or 90s")
    return float(int(found.group("n")) * _UNIT_SECONDS[found.group("unit")])


@dataclass(slots=True)
class _Watcher:
    """Observes one PR, keeping the base branch's rules between polls."""

    ports: Ports
    repo: RepoSlug
    number: int
    rules: Rules | None = None

    def __call__(self) -> Observation:
        seen = github.observe(self.ports.graphql, self.ports.api, self.repo, self.number, self.rules)
        self.rules = seen.rules
        return seen


def make_app(ports: Ports | None = None) -> typer.Typer:
    """The `rig gh pr` command group, reaching GitHub and the clock through *ports*."""
    io = ports or Ports()
    pr_app = typer.Typer(
        no_args_is_help=True,
        help="Wait for a pull request to land, and stop with the reason as soon as it cannot.",
    )

    def run(watcher: _Watcher, timeout: str, settle: str, *, as_json: bool) -> None:
        pace = Pace(timeout=parse_duration(timeout), settle=parse_duration(settle))
        label = f"{watcher.repo}#{watcher.number}"

        def progress(tick: Tick) -> None:
            typer.echo(
                f"{label} [{_clock(tick.elapsed)}] {tick.message} (next look in {tick.next_poll:.0f}s)", err=True
            )

        result = wait(watcher, pace, io.clock, progress)
        report = _report(watcher.repo, watcher.number, result)
        if as_json:
            typer.echo(_REPORT.dump_json(report, indent=2).decode("utf-8"))
        else:
            _print_report(report)
        raise typer.Exit(EXIT_CODES[result.outcome])

    @pr_app.command("wait")
    def wait_cmd(
        pr: int = typer.Argument(..., help="Pull request number."),
        repo: str | None = typer.Option(None, "--repo", help="owner/name; defaults to this clone's origin."),
        timeout: str = typer.Option("60m", "--timeout", help=_TIMEOUT_HELP),
        settle: str = typer.Option("30s", "--settle", help=_SETTLE_HELP),
        as_json: bool = typer.Option(False, "--json", help="Print the final report as one JSON document on stdout."),
    ) -> None:
        """Wait until the PR merges, or stop as soon as it cannot land without someone acting.

        Stops at once for a failed or cancelled required check, unresolved review threads when the ruleset
        requires them resolved, requested changes, a required review not given, merge conflicts, a draft,
        or a closed PR. Stops after --settle for a merge that stays BLOCKED or BEHIND with no check pending,
        for auto-merge being off, and for a PR that left the merge queue. While waiting it prints what it
        is waiting on to stderr each time that changes: pending checks, the merge-queue position.

        Polls with backoff: 5s after a change, growing to 60s while nothing changes. Read-only.

        Exits 0 merged, 1 blocked (someone has to act), 2 GitHub could not be read, 3 timed out still waiting.
        """
        watcher = _Watcher(io, resolve_target(repo, io.origin), pr)
        run(watcher, timeout, settle, as_json=as_json)

    @pr_app.command("land")
    def land_cmd(
        pr: int = typer.Argument(..., help="Pull request number."),
        repo: str | None = typer.Option(None, "--repo", help="owner/name; defaults to this clone's origin."),
        timeout: str = typer.Option("60m", "--timeout", help=_TIMEOUT_HELP),
        settle: str = typer.Option("30s", "--settle", help=_SETTLE_HELP),
        as_json: bool = typer.Option(False, "--json", help="Print the final report as one JSON document on stdout."),
    ) -> None:
        """Queue a squash auto-merge (`gh pr merge <PR> --auto --squash`), then wait as `wait` does.

        Skips the queueing when the PR is already merged, has auto-merge on or is in the merge queue.
        Branch protection still gates the merge; this never bypasses it.

        Exits 0 merged, 1 blocked (someone has to act), 2 GitHub or `gh pr merge` failed, 3 timed out
        still waiting.
        """
        watcher = _Watcher(io, resolve_target(repo, io.origin), pr)
        try:
            first = watcher().snapshot
        except GitHubError as error:
            typer.echo(str(error), err=True)
            raise typer.Exit(EXIT_CODES[Outcome.ERROR]) from error
        if first.state == "OPEN" and not first.auto_merge and first.queue is None:
            ran = io.merge(watcher.repo, pr)
            if not ran.ok:
                typer.echo(f"Could not queue auto-merge: {ran.err.strip() or f'gh exited {ran.code}'}", err=True)
                raise typer.Exit(EXIT_CODES[Outcome.ERROR])
            typer.echo(f"{watcher.repo}#{pr}: queued squash auto-merge", err=True)
        run(watcher, timeout, settle, as_json=as_json)

    return pr_app


def _clock(seconds: float) -> str:
    minutes, rest = divmod(int(seconds), 60)
    return f"{minutes}:{rest:02d}"


def _report(repo: RepoSlug, number: int, result: Result) -> Report:
    seen = result.last
    pr = seen.snapshot if seen is not None else None
    return Report(
        repo=str(repo),
        pr=number,
        url=pr.url if pr is not None else "",
        outcome=result.outcome.value,
        reason=result.reason,
        message=result.message,
        waited_seconds=int(result.waited),
        polls=result.polls,
        merge_state=pr.merge_state if pr is not None else "",
        auto_merge=pr.auto_merge if pr is not None else False,
        queue_position=pr.queue.position if pr is not None and pr.queue is not None else None,
        pending_checks=seen.pending_checks if seen is not None else (),
        failed_checks=seen.failed_checks if seen is not None else (),
        unresolved_threads=seen.threads if seen is not None else (),
    )


def _print_report(report: Report) -> None:
    label = f"{report.repo}#{report.pr}"
    waited = _clock(report.waited_seconds)
    if report.outcome == Outcome.MERGED:
        typer.echo(f"{label}: merged after {waited}")
        return
    if report.reason == GITHUB_ERROR:
        typer.echo(f"{label}: {report.message}", err=True)
        return
    typer.echo(f"{label}: {report.outcome.upper()} ({report.reason}) after {waited}: {report.message}")
    if report.reason != Reason.UNRESOLVED_THREADS:
        return
    for thread in report.unresolved_threads:
        typer.echo(f"  {thread.id}  {thread.location}  @{thread.author}")
        typer.echo(f"      {thread.summary}")
    typer.echo(
        f"Resolve each once its fix is pushed: "
        f"uv run rig gh threads resolve {report.pr} <thread> --sha <fix> --repo {report.repo}"
    )
