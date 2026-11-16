"""`desktop-manager chrome-tabs`: read and prune Chrome tabs through UI Automation.

C# owns `list`, `rank`, `prune`, `group` and `saved-groups` on Windows and macOS through
`rig chrome-tabs`, plus the Windows-only `install` and `uninstall`. The Python tree keeps `organise`
until its scheduled/wait slice moves, and the `traces` spelling forwards the whole line to C#.
"""

from __future__ import annotations

import contextvars
import re
import time
from collections.abc import Sequence
from dataclasses import replace
from datetime import datetime
from typing import TYPE_CHECKING, Annotated

import typer
from rich.markup import escape

from desktop_manager.chrome.cli_common import (
    command_driver,
    console,
    keep_desktop,
    list_profile_windows,
    plan_line,
    print_lines,
    trace_span,
)
from desktop_manager.chrome.dedupe import DedupePlan
from desktop_manager.chrome.driver import ChromeReadError
from desktop_manager.chrome.group_run import plan_grouping
from desktop_manager.chrome.grouping import GroupPlan
from desktop_manager.chrome.install_location import DEFAULT_WAIT_MINUTES
from desktop_manager.chrome.pr_prune import (
    AppliedClose,
    ClosePlan,
    PrunePlan,
)
from desktop_manager.chrome.render import (
    render_group_plan,
    render_memory_plan,
    render_plan,
    render_site_plan,
)
from desktop_manager.chrome.saved_consolidation import (
    consolidate_saved_groups,
    pending_profiles,
    plan_saved_groups,
    set_pending_profiles,
)
from desktop_manager.chrome.tabs import ChromeWindow, screen_order
from desktop_manager.chrome.trace_forward import forward as forward_traces
from desktop_manager.chrome.traces import RUN_SPAN, SCOPE
from desktop_manager.chrome.unattended import (
    GroupingResult,
    await_unattended_grouping,
    group_outcome,
    group_until_left_alone,
    skip_if_live,
)
from rig import tracing
from rig.github_prs import Resolution
from rig.platforms import Platform, requires
from rig.proc import GhResult, GhRunner

if TYPE_CHECKING:
    from desktop_manager.chrome.driver import ChromeDriver

chrome_tabs_app = typer.Typer(
    name="chrome-tabs",
    no_args_is_help=True,
    help="Chrome tabs via UI Automation: list them, prune GitHub PR tabs and duplicate pages, group by repo, "
    "or organise (both, window by window). "
    "No extension needed.",
)
chrome_tabs_app.command(
    name="traces",
    help="Reports over organise traces. Runs on rig chrome-tabs traces.",
    add_help_option=False,
    context_settings={"allow_extra_args": True, "ignore_unknown_options": True},
)(forward_traces)

_GH_REPO = re.compile(r'repository\(owner: "([^"]+)", name: "([^"]+)"\)')


_ThisDesktopOpt = Annotated[
    bool, typer.Option("--this-desktop", help="Group only windows on the current desktop; never bring others over.")
]


_ProfileOpt = Annotated[
    str | None, typer.Option("--profile", help="Only this profile: directory ('Profile 4') or display name.")
]


_GithubOpt = Annotated[
    bool,
    typer.Option(
        "--github/--no-github",
        help="Query GitHub for PR state, chains, size, review and checks. --no-github skips the PR rules and "
        "every GitHub call; GitHub pages are still de-duplicated by title.",
    ),
]


def _resolve(windows: Sequence[ChromeWindow], github: bool) -> Resolution:
    from desktop_manager.chrome.pr_prune import pr_tabs
    from rig.github_prs import resolve

    if not github:
        return Resolution({}, [])
    refs = {item.pr for item in pr_tabs(windows)}
    with trace_span(
        "resolve_prs", **{"github.prs": len(refs), "github.repos": len({ref.slug for ref in refs})}
    ) as span:
        resolution = resolve(refs, runner=_traced_gh(contextvars.copy_context()))
        span.set(**{"github.chains": len(resolution.chains), "github.failed_repos": sorted(resolution.failed_repos)})
        return resolution


def _traced_gh(context: contextvars.Context) -> GhRunner:
    """`run_gh` with a span per query. `resolve` runs queries on a thread pool, which does not
    inherit the caller's span, so each query runs in a copy of the caller's context."""

    def run(query: str) -> GhResult:
        return context.copy().run(_gh_query, query)

    return run


def _gh_query(query: str) -> GhResult:
    from rig.proc import run_gh

    match = _GH_REPO.search(query)
    attrs = {
        "github.repo": f"{match.group(1)}/{match.group(2)}" if match else "?",
        "github.lookups": query.count("pullRequest(number"),
        "github.open_list": "open: pullRequests" in query,
    }
    with tracing.span("github.graphql", scope=SCOPE, **attrs) as span:
        result = run_gh(query)
        span.set(**{"process.exit_code": result.returncode})
        if result.returncode != 0:
            span.error = result.stderr.strip()[:200] or f"gh exited {result.returncode}"
        return result


def _close_plans(
    windows: Sequence[ChromeWindow], resolution: Resolution, *, github: bool, sites: bool, max_memory: float | None
) -> list[ClosePlan]:
    """Every close plan `organise` runs, each printed as it is made."""
    with trace_span("plan_closes") as span:
        plans = _make_close_plans(windows, resolution, github=github, sites=sites, max_memory=max_memory)
        span.set(**{f"close.{_plan_kind(plan)}": len(plan.closes) for plan in plans})
        span.set(**{"close.planned": sum(len(plan.closes) for plan in plans)})
        return plans


def _plan_kind(plan: ClosePlan) -> str:
    return {"PrunePlan": "pr", "DedupePlan": "sites", "MemoryPlan": "memory"}.get(type(plan).__name__, "other")


def _make_close_plans(
    windows: Sequence[ChromeWindow], resolution: Resolution, *, github: bool, sites: bool, max_memory: float | None
) -> list[ClosePlan]:
    from desktop_manager.chrome.dedupe import plan_dedupe
    from desktop_manager.chrome.pr_prune import plan_prune
    from desktop_manager.chrome.rank import plan_memory, rank_tabs

    plans: list[ClosePlan] = []
    reported: list[PrunePlan | DedupePlan] = []
    if github:
        pr_plan = plan_prune(windows, resolution)
        plans.append(pr_plan)
        reported.append(pr_plan)
        for slug, error in sorted(resolution.failed_repos.items()):
            console.print(f"[yellow]{escape(slug)}: open PRs unreadable, chain rules skipped: {escape(error)}[/yellow]")
        print_lines(render_plan(pr_plan))
    if sites:
        site_plan = plan_dedupe(windows)
        plans.append(site_plan)
        reported.append(site_plan)
        print_lines(render_site_plan(site_plan))
    if max_memory is not None:
        closing = {close.item.tab.runtime_id for plan in plans for close in plan.closes}
        held = {report.item.tab.runtime_id for plan in reported for report in plan.reports}
        memory_plan = plan_memory(rank_tabs(windows, resolution), max_memory * 1024, already_closing=closing, held=held)
        plans.append(memory_plan)
        print_lines(render_memory_plan(memory_plan))
    return plans


def _report_failed_closes(applied: Sequence[AppliedClose]) -> list[AppliedClose]:
    failures = [result for result in applied if not result.closed]
    for result in failures:
        console.print(f"[red]Not closed[/red] {escape(result.planned.item.label)}: {escape(result.detail)}")
    return failures


@chrome_tabs_app.command(name="organise")
@requires(Platform.WINDOWS, Platform.MACOS)
def organise(
    ctx: typer.Context,
    dry_run: bool = typer.Option(False, "--dry-run", help="Print both plans; change nothing."),
    profile: _ProfileOpt = None,
    github: _GithubOpt = True,
    sites: bool = typer.Option(
        True, "--sites/--no-sites", help="Close duplicate non-PR pages by per-site title rules."
    ),
    min_size: int = typer.Option(
        2, "--min-size", min=1, help="Fewest tabs of one repo or site that start a new group."
    ),
    unattended: bool = typer.Option(
        False,
        "--unattended",
        help="For the schedule: prune as `prune --unattended`, and group only while nobody is using the machine.",
    ),
    idle_minutes: float = typer.Option(
        1,
        "--idle-minutes",
        min=0,
        help="With --unattended: minutes without input before grouping may start. Keep it under the "
        "machine's auto-lock: a locked session cannot be grouped.",
    ),
    wait_minutes: float = typer.Option(
        DEFAULT_WAIT_MINUTES,
        "--wait-minutes",
        min=0,
        help="With --unattended: how long to keep waiting for the machine to go idle before leaving "
        "grouping to the next run. 0 gives up at once.",
    ),
    this_desktop: _ThisDesktopOpt = False,
    consolidate: bool = typer.Option(
        False,
        "--consolidate-saved",
        help="When grouping created a new group, afterwards delete closed duplicate saved groups, "
        "as `saved-groups --consolidate`.",
    ),
    trigger: str = typer.Option("manual", "--trigger", hidden=True, help="What started the run, for its trace."),
) -> None:
    """Prune, then group, one window at a time: the whole tidy in one command.

    Windows go in screen order: the current desktop first, then monitor by monitor,
    top to bottom. Each window has its tabs closed and its active tab put back before
    the next starts. Then grouping (as `group`) runs window by window over what is
    left; it brings each window forward and uses the mouse.

    `--consolidate-saved` then deletes closed duplicate saved groups, but only when
    grouping created a new group: joining an open group never saves a duplicate.
    A consolidation that failed or deleted anything is looked at again next run,
    since a delete can bring chips out of the overflow.

    `--unattended` is what the hourly task runs. It does nothing at all while a call
    is live: a tab recording or sharing the screen, an audible call site, or Chrome
    holding the microphone or camera. Grouping then waits, for up to
    `--wait-minutes`, until the machine has been idle for `--idle-minutes` and is not
    locked or on a call. The wait backs off between checks, so it costs next to
    nothing. Grouping stops at the first key press or mouse move, opens a Notepad
    note, and goes again once the machine has been left alone for 30 seconds. It
    waits up to 5 minutes for that, at most twice, then leaves the rest to the next run.

    Every step that brings windows forward puts you back on the virtual desktop you
    were on when it started.

    Every run is traced to the `chrome-tabs traces` directory; see `traces runs`.
    """
    from desktop_manager.chrome import traces

    traces.start()
    attrs = {
        "chrome.trigger": trigger,
        "chrome.unattended": unattended,
        "chrome.dry_run": dry_run,
        "chrome.github": github,
        "chrome.sites": sites,
        "chrome.profile": profile or "",
    }
    with tracing.span(RUN_SPAN, scope=SCOPE, **attrs) as run:
        try:
            failed = _organise(
                run,
                command_driver(ctx),
                dry_run=dry_run,
                profile=profile,
                github=github,
                sites=sites,
                min_size=min_size,
                unattended=unattended,
                idle_minutes=idle_minutes,
                wait_minutes=wait_minutes,
                this_desktop=this_desktop,
                consolidate=consolidate,
            )
        except ChromeReadError as error:
            failed = _unreadable(run, error, unattended=unattended)
    if failed:
        raise typer.Exit(code=1)


def _unreadable(run: tracing.Span, error: ChromeReadError, *, unattended: bool) -> bool:
    """Report a read Chrome kept changing under: True (failed) when attended, a skip when scheduled.

    Each step re-finds and checks its tab before acting, so stopping between steps leaves no step
    half done, and the next scheduled run starts over. An attended run fails so its user sees it."""
    console.print(f"[yellow]Skipped[/yellow]: {escape(str(error))}; left the rest for the next run")
    run.set(**{"run.outcome": "skipped_unreadable" if unattended else "failed", "run.skipped": str(error)})
    return not unattended


def _organise(
    run: tracing.Span,
    driver: ChromeDriver,
    *,
    dry_run: bool,
    profile: str | None,
    github: bool,
    sites: bool,
    min_size: int,
    unattended: bool,
    idle_minutes: float,
    wait_minutes: float,
    this_desktop: bool,
    consolidate: bool,
) -> bool:
    """`organise`, with its counts and verdicts recorded on the run span: True when the run failed.

    Tabs that would not close only warn (outcome `partial`); grouping or consolidating that stopped fails.
    """
    from desktop_manager.chrome.pr_prune import apply_plans

    started = time.perf_counter()
    run_started = datetime.now().astimezone()
    windows = list_profile_windows(driver, profile, phase="initial")
    if unattended and skip_if_live(windows, run):
        return False
    plans = _close_plans(windows, _resolve(windows, github), github=github, sites=sites, max_memory=None)
    current = driver.desktops.current()
    ordered = screen_order(windows, current_desktop=current)
    number = {window.hwnd: n for n, window in enumerate(ordered, start=1)}
    if unattended:
        from desktop_manager.chrome.pr_prune import hold_unattended

        runnable = hold_unattended(plans, foreground_hwnd=driver.foreground_window())
        plans = [runnable]
        run.set(**{"tabs.held": len(runnable.held)})
        if runnable.held:
            console.print(f"[bold]Held back: active tab or foreground window ({len(runnable.held)})[/bold]")
            print_lines([plan_line(close.item, "left for an attended run") for close in runnable.held])
    run.set(**{"tabs.close_planned": sum(len(plan.closes) for plan in plans)})

    if dry_run:
        closing = {close.item.tab.runtime_id for plan in plans for close in plan.closes}
        console.print("[bold]Then grouping:[/bold]")
        after = plan_grouping(_without(ordered, closing), current, min_size, this_desktop, phase="dry_run")
        print_lines(render_group_plan(after))
        console.print(f"[dim]Dry run: nothing changed. planned in {time.perf_counter() - started:.2f}s[/dim]")
        run.set(**{"run.outcome": "dry_run", "group.outcome": "dry_run", "group.planned": len(after.moves)})
        return False

    def report_closes(window: ChromeWindow, closes: list[AppliedClose]) -> None:
        if not closes:
            return
        heading = f"Window {number[window.hwnd]} [{window.profile_name or window.profile_key}]"
        closed = sum(result.closed for result in closes)
        console.print(f"{escape(heading)}: closed {closed}/{len(closes)}")

    with trace_span("close_tabs") as closing:
        order = [window.hwnd for window in ordered]
        with keep_desktop(driver.desktops):
            applied = apply_plans(plans, windows, driver, order=order, after_window=report_closes)
        failures = _report_failed_closes(applied)
        counts = {"tabs.closed": len(applied) - len(failures), "tabs.close_failed": len(failures)}
        closing.set(**counts)
    run.set(**counts)

    # Grouping reads the windows afresh: closes renumber tabs and can close whole windows.
    def read_windows(phase: str = "wait_check") -> list[ChromeWindow]:
        return screen_order(list_profile_windows(driver, profile, phase=phase), current_desktop=current)

    regrouped = read_windows("regroup")
    plan = plan_grouping(regrouped, current, min_size, this_desktop)
    print_lines(render_group_plan(plan))
    deferred = None
    grouped = GroupingResult(planned=len(plan.moves))
    if plan.moves:
        if unattended:
            wait = await_unattended_grouping(read_windows, idle_minutes, wait_minutes)
            run.set(**wait.attributes())
            deferred = wait.deferred
            if deferred is None:
                # Tabs come and go while the run waits; group what is there now.
                plan = plan_grouping(read_windows("replan"), current, min_size, this_desktop, phase="replan")
        if deferred is not None:
            console.print(f"[yellow]Grouping deferred[/yellow]: {escape(deferred)}")
        elif plan.moves:

            def replan(phase: str) -> GroupPlan:
                return plan_grouping(read_windows(phase), current, min_size, this_desktop, phase=phase)

            grouped = group_until_left_alone(
                driver, plan, read_windows=read_windows, replan=replan, unattended=unattended, run_started=run_started
            )
    moved, stopped = grouped.moved, grouped.stopped
    if stopped is not None and grouped.by_input:
        console.print(f"[yellow]Left grouping[/yellow]: {escape(stopped)}; the next run carries on")
    elif stopped is not None:
        console.print(f"[red]Stopped grouping[/red] {escape(stopped)}")
    consolidated = ""
    unsaved = None
    created = grouped.created
    pending: set[str] = pending_profiles() if consolidate else set()
    if consolidate:
        reason = "ran" if created else "pending" if pending else "no_new_groups"
        run.set(**{"saved.consolidate": reason})
    if consolidate and (created or pending):
        with trace_span("consolidate_saved") as consolidating:
            scanned = list_profile_windows(driver, profile, phase="consolidate")
            # Marked first, so a run that raises part way is looked at again.
            unscanned = pending - {window.profile_key for window in scanned}
            set_pending_profiles(unscanned | {window.profile_key for window in scanned})
            planned = plan_saved_groups(driver, scanned)
            saved = planned.plan
            done = consolidate_saved_groups(driver, planned)
            deleted, unsaved = done.deleted, done.stopped
            # A profile that deleted or failed anything goes again: a delete can bring chips out of the overflow.
            set_pending_profiles(unscanned | {delete.profile for delete in saved.deletes})
            counts = {"saved.planned": saved.total, "saved.deleted": deleted}
            consolidating.set(**counts, **({"saved.stopped": unsaved} if unsaved else {}))
        run.set(**counts)
        if unsaved is not None:
            console.print(f"[red]Stopped consolidating[/red] {escape(unsaved)}")
        consolidated = f", deleted {deleted}/{saved.total} saved duplicates"
    console.print(
        f"[green]Closed {len(applied) - len(failures)}/{len(applied)}, grouped {moved}/{grouped.planned}"
        f"{consolidated}[/green] [dim]in {time.perf_counter() - started:.2f}s[/dim]"
    )
    # A tab that would not close is still open, so the next run plans it again: a warning, not a failure.
    # So is grouping someone's input stopped; grouping that failed by itself fails the run.
    failed = unsaved is not None or (stopped is not None and not grouped.by_input)
    if failures and not failed:
        console.print(f"[yellow]Warning[/yellow]: {len(failures)} tab(s) not closed; the next run tries again")
    run.set(
        **{
            "run.outcome": "failed" if failed else "partial" if failures else "ok",
            "group.outcome": group_outcome(grouped, deferred=deferred),
            "group.planned": grouped.planned,
            "group.moved": moved,
            "group.retries": grouped.retries,
            **({"group.stopped": stopped} if stopped else {}),
            **({"group.deferred": deferred} if deferred else {}),
        }
    )
    return failed


def _without(windows: Sequence[ChromeWindow], closing: set[tuple[int, ...]]) -> list[ChromeWindow]:
    """The windows as they will be once `closing` has closed, tabs renumbered."""
    return [
        replace(
            window,
            tabs=tuple(
                replace(tab, index=index)
                for index, tab in enumerate(tab for tab in window.tabs if tab.runtime_id not in closing)
            ),
        )
        for window in windows
    ]
