"""`rig gh threads list|resolve`: TEMPORARY, retire once `gh` handles PR review threads natively.

Exit codes follow rig's convention: 0 clean (or done), 1 drift (unresolved threads listed) or a refused
resolve, 2 an error talking to GitHub.
"""

from __future__ import annotations

from dataclasses import dataclass

import typer
from pydantic import TypeAdapter

from rig.gh_target import OriginReader, origin_slug, resolve_target
from rig.gh_threads import github
from rig.gh_threads.github import GitHubError, GraphQL
from rig.gh_threads.model import ResolvePlan, Thread, plan_resolve
from rig.repo_slug import RepoSlug

_RETIRE = "TEMPORARY: retire once `gh` can list, reply to and resolve PR review threads itself."

CLEAN = 0
DRIFT = 1
REFUSED = 1
ERROR = 2


@dataclass(frozen=True, slots=True)
class ResolveOutcome:
    """What `resolve` did, or with ``--dry-run`` would do."""

    thread_id: str
    sha: str
    reply_url: str | None
    replied: bool
    resolved: bool
    dry_run: bool


_THREADS: TypeAdapter[list[Thread]] = TypeAdapter(list[Thread])
_OUTCOME: TypeAdapter[ResolveOutcome] = TypeAdapter(ResolveOutcome)


def make_app(graphql: GraphQL = github.gh_graphql, origin: OriginReader = origin_slug) -> typer.Typer:
    """The `rig gh threads` command group, talking to GitHub through *graphql*."""
    threads_app = typer.Typer(
        no_args_is_help=True,
        help=(
            f"List, reply to and resolve pull-request review threads. {_RETIRE} "
            "A thread is resolved only after its fix is pushed, with a reply naming the fix commit."
        ),
    )

    def target(repo: str | None) -> RepoSlug:
        return resolve_target(repo, origin)

    @threads_app.command("list")
    def list_cmd(
        pr: int = typer.Argument(..., help="Pull request number."),
        repo: str | None = typer.Option(None, "--repo", help="owner/name; defaults to this clone's origin."),
        as_json: bool = typer.Option(False, "--json", help="Print the threads as JSON."),
    ) -> None:
        """List the PR's unresolved review threads. Exits 1 when there are any, 0 when there are none."""
        slug = target(repo)
        try:
            observed = github.fetch_threads(graphql, slug, pr)
        except GitHubError as error:
            typer.echo(str(error), err=True)
            raise typer.Exit(ERROR) from error
        open_threads = [item.thread for item in observed if not item.thread.resolved]
        if as_json:
            typer.echo(_THREADS.dump_json(open_threads, indent=2).decode("utf-8"))
        else:
            _print_threads(slug, pr, open_threads)
        raise typer.Exit(DRIFT if open_threads else CLEAN)

    @threads_app.command("resolve")
    def resolve_cmd(
        pr: int = typer.Argument(..., help="Pull request number."),
        thread_id: str = typer.Argument(..., help="Review thread id (PRRT_…), as `list` prints it."),
        sha: str = typer.Option(..., "--sha", help="The pushed commit that fixes the thread (full SHA or prefix)."),
        note: str | None = typer.Option(None, "--note", help="Added to the reply after 'Fixed in <sha>.'"),
        repo: str | None = typer.Option(None, "--repo", help="owner/name; defaults to this clone's origin."),
        dry_run: bool = typer.Option(False, "--dry-run", help="Check and print the plan; write nothing."),
        as_json: bool = typer.Option(False, "--json", help="Print the outcome as JSON."),
    ) -> None:
        """Reply 'Fixed in <sha>.' on a thread, then resolve it.

        Refuses (exit 1) unless the SHA is one of the PR's own commits, so a thread is never resolved
        against a fix that was not pushed. Safe to rerun: an existing reply is not repeated.
        """
        slug = target(repo)
        try:
            threads = github.fetch_threads(graphql, slug, pr)
            pull = github.fetch_pull_request(graphql, slug, pr)
        except GitHubError as error:
            typer.echo(str(error), err=True)
            raise typer.Exit(ERROR) from error
        plan = plan_resolve(thread_id, sha, note, threads, pull)
        if not isinstance(plan, ResolvePlan):
            typer.echo(f"Refused: {plan.reason}", err=True)
            raise typer.Exit(REFUSED)
        outcome = _apply(graphql, plan, dry_run=dry_run, as_json=as_json)
        if as_json:
            typer.echo(_OUTCOME.dump_json(outcome, indent=2).decode("utf-8"))
        else:
            _print_outcome(plan, outcome)

    return threads_app


def _apply(graphql: GraphQL, plan: ResolvePlan, *, dry_run: bool, as_json: bool) -> ResolveOutcome:
    thread_id = plan.thread.id
    if dry_run or plan.is_empty:
        return ResolveOutcome(thread_id, plan.sha, None, replied=False, resolved=plan.thread.resolved, dry_run=dry_run)
    url: str | None = None
    try:
        if plan.reply is not None:
            url = github.reply(graphql, thread_id, plan.reply)
        if plan.resolve:
            github.resolve(graphql, thread_id)
    except GitHubError as error:
        if url:
            typer.echo(f"Replied ({url}) but did not resolve. Rerun to resolve; the reply is not repeated.", err=True)
        if as_json:
            typer.echo(_OUTCOME.dump_json(ResolveOutcome(thread_id, plan.sha, url, url is not None, False, False)))
        typer.echo(str(error), err=True)
        raise typer.Exit(ERROR) from error
    return ResolveOutcome(thread_id, plan.sha, url, replied=url is not None, resolved=True, dry_run=False)


def _print_threads(slug: RepoSlug, pr: int, threads: list[Thread]) -> None:
    if not threads:
        typer.echo(f"{slug}#{pr}: no unresolved review threads")
        return
    typer.echo(f"{slug}#{pr}: {len(threads)} unresolved review thread(s)")
    for thread in threads:
        outdated = " (outdated)" if thread.outdated else ""
        typer.echo(f"{thread.id}  {thread.location}{outdated}  @{thread.author}")
        typer.echo(f"    {thread.summary}")


def _print_outcome(plan: ResolvePlan, outcome: ResolveOutcome) -> None:
    where = f"{plan.thread.id} ({plan.thread.location})"
    if outcome.dry_run:
        steps = [f"reply {plan.reply!r}" if plan.reply else "skip the reply (already posted)"]
        steps.append("resolve" if plan.resolve else "skip resolving (already resolved)")
        typer.echo(f"Dry run, {where}: would {' then '.join(steps)}.")
        return
    if plan.is_empty:
        typer.echo(f"{where}: already replied with {plan.sha[:12]} and resolved; nothing to do.")
        return
    replied = f"replied {outcome.reply_url}" if outcome.replied else "reply already posted"
    resolved = "resolved" if plan.resolve else "was already resolved"
    typer.echo(f"{where}: {replied}; {resolved} (fix {plan.sha[:12]}).")
