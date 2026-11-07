"""`desktop-manager work`: work items joining branches, worktrees, PRs and Claude sessions. Read-only."""

from __future__ import annotations

import datetime as dt
import json
import re
from collections.abc import Sequence
from pathlib import Path
from typing import Annotated

import typer
from rich.console import Console
from rich.markup import escape
from rich.table import Table

from desktop_manager.work import collect, join, serialize
from desktop_manager.work.model import PrLookup, PrStatus, SessionState, WorkItem, WorkReport, WorkState
from rig import roots

work_app = typer.Typer(
    name="work",
    no_args_is_help=True,
    help="Work items: one row per (repo, branch), with its worktrees, PR and Claude sessions.",
)
_out = Console()
_err = Console(stderr=True)

_RELATIVE = re.compile(r"^(\d+)([dhw])$")
_STATE_STYLE = {
    WorkState.ACTIVE: "bold green",
    WorkState.MERGED: "magenta",
    WorkState.CLOSED: "red",
    WorkState.STALE: "yellow",
    WorkState.TRUNK: "dim",
    WorkState.UNATTACHED: "dim cyan",
}
_PR_STYLE = {PrStatus.OPEN: "green", PrStatus.DRAFT: "dim green", PrStatus.MERGED: "magenta", PrStatus.CLOSED: "red"}
_SESSION_SHORT = {
    SessionState.LIVE: "live",
    SessionState.WAITING_ON_USER: "waiting",
    SessionState.DIED_MID_TASK: "died-mid",
    SessionState.DIED_IDLE: "died-idle",
    SessionState.ENDED_CLEAN: "ended",
    SessionState.CLEARED: "cleared",
}


def parse_since(text: str, *, now: dt.datetime) -> dt.datetime:
    """`7d`, `24h`, `2w`, or an ISO date/datetime (naive means UTC)."""
    match = _RELATIVE.match(text.strip().lower())
    if match:
        amount, unit = int(match[1]), match[2]
        hours = {"h": 1, "d": 24, "w": 24 * 7}[unit]
        return now - dt.timedelta(hours=amount * hours)
    try:
        parsed = dt.datetime.fromisoformat(text)
    except ValueError as error:
        raise typer.BadParameter(f"expected 7d / 24h / 2w or an ISO date, got {text!r}") from error
    return parsed if parsed.tzinfo else parsed.replace(tzinfo=dt.UTC)


def _ago(moment: dt.datetime | None, now: dt.datetime) -> str:
    if moment is None:
        return "-"
    seconds = (now - moment).total_seconds()
    if seconds < 3600:
        return f"{max(int(seconds // 60), 0)}m"
    if seconds < 86400 * 2:
        return f"{int(seconds // 3600)}h"
    return f"{int(seconds // 86400)}d"


def _pr_cell(item: WorkItem) -> str:
    if item.pr is not None:
        style = _PR_STYLE[item.pr.status]
        return f"[{style}]#{item.pr.number} {item.pr.status.value}[/{style}]"
    if item.pr_lookup is PrLookup.UNAVAILABLE:
        return "[yellow]?[/yellow]"
    return "[dim]-[/dim]" if item.pr_lookup is PrLookup.SKIPPED else "none"


def _local_cell(item: WorkItem) -> str:
    parts: list[str] = []
    if item.dirty:
        parts.append("[yellow]dirty[/yellow]")
    if item.unpushed:
        parts.append(f"{item.unpushed} unpushed")
    ref = item.branch_ref
    if ref is not None:
        if ref.upstream_gone:
            parts.append("[dim]gone[/dim]")
        elif ref.ahead or ref.behind:
            parts.append(f"+{ref.ahead or 0}/-{ref.behind or 0}")
    if len(item.checkouts) > 1:
        parts.append(f"{len(item.checkouts)} wt")
    return " ".join(parts) or "[dim]-[/dim]"


def _sessions_cell(item: WorkItem) -> str:
    if not item.sessions:
        return "[dim]-[/dim]"
    counts: dict[SessionState, int] = {}
    for session in item.sessions:
        counts[session.state] = counts.get(session.state, 0) + 1
    return " ".join(f"{n} {_SESSION_SHORT[state]}" for state, n in counts.items())


def _render(report: WorkReport, items: Sequence[WorkItem], *, show_sessions: bool) -> None:
    now = report.generated_at
    table = Table(show_header=True, header_style="bold", box=None, padding=(0, 1))
    for column in ("State", "Repo", "Branch", "Ticket", "PR", "Local", "Sessions", "Age", "Why"):
        table.add_column(column, overflow="fold")
    for item in items:
        style = _STATE_STYLE[item.state]
        table.add_row(
            f"[{style}]{item.state.value}[/{style}]",
            escape(item.repo or "-"),
            escape(item.branch or "-"),
            item.ticket or "",
            _pr_cell(item),
            _local_cell(item),
            _sessions_cell(item),
            _ago(item.last_activity, now),
            escape("; ".join(item.reasons)),
        )
        if show_sessions:
            for session in item.sessions:
                table.add_row(
                    "",
                    "",
                    f"[dim]  {session.session_id[:8]}[/dim]",
                    "",
                    "",
                    "",
                    _SESSION_SHORT[session.state],
                    _ago(session.last_activity, now),
                    f"[dim]{escape(session.title[:70])}[/dim]",
                )
    _out.print(table)
    counts = join.count_states(items)
    summary = "  ".join(f"{state.value}={n}" for state, n in counts.items() if n)
    hidden = len(report.items) - len(items)
    _out.print(f"[dim]{len(items)} items ({summary}); {hidden} hidden by filters[/dim]")
    for warning in report.warnings:
        _err.print(f"[yellow]warning:[/yellow] {escape(warning)}")


@work_app.command(name="list")
def list_command(
    repo: Annotated[str | None, typer.Option("--repo", help="Only repos whose name contains this text.")] = None,
    state: Annotated[
        list[WorkState] | None, typer.Option("--state", help="Only these states (repeatable).", case_sensitive=False)
    ] = None,
    since: Annotated[
        str | None, typer.Option("--since", help="Only items active since: 7d, 24h, 2w or an ISO date.")
    ] = None,
    show_all: Annotated[
        bool, typer.Option("--all", help="Include quiet trunk items (clean default branch, no sessions).")
    ] = False,
    as_json: Annotated[
        bool, typer.Option("--json", help="Machine-readable output (schema in desktop_manager/work/serialize.py).")
    ] = False,
    sessions: Annotated[bool, typer.Option("--sessions", help="List each item's sessions under it.")] = False,
    offline: Annotated[bool, typer.Option("--offline", help="Skip GitHub; PR columns read '-'.")] = False,
    refresh: Annotated[bool, typer.Option("--refresh", help="Bypass the PR and clone-list caches.")] = False,
    no_status: Annotated[
        bool, typer.Option("--no-status", help="Skip git status per checkout (faster; dirty reads unknown).")
    ] = False,
    stale_days: Annotated[int, typer.Option("--stale-days", help="Days without activity before 'stale'.")] = 14,
    session_days: Annotated[int, typer.Option("--session-days", help="How far back to read Claude sessions.")] = 30,
    root: Annotated[Path | None, typer.Option("--root", help="Code root holding every clone.")] = None,
    timings: Annotated[bool, typer.Option("--timings", help="Print seconds per phase to stderr.")] = False,
) -> None:
    """List work items: branches across the estate joined with PRs and Claude sessions."""
    now = dt.datetime.now(dt.UTC)
    options = collect.CollectOptions(
        root=(root or roots.code_root()).expanduser().resolve(),
        session_window=dt.timedelta(days=session_days),
        stale_after=dt.timedelta(days=stale_days),
        offline=offline,
        refresh=refresh,
        probe=not no_status,
    )
    collected = collect.collect(options, now=now)
    spec = join.ItemFilter(
        repo=repo,
        states=frozenset(state or ()),
        since=parse_since(since, now=now) if since else None,
        include_quiet_trunk=show_all,
    )
    items = join.filter_items(collected.report.items, spec)
    if as_json:
        typer.echo(json.dumps(serialize.report_json(collected.report, items), indent=2, ensure_ascii=False))
    else:
        _render(collected.report, items, show_sessions=sessions)
    if timings:
        phases = "  ".join(f"{name}={seconds:.2f}s" for name, seconds in collected.timings.phases.items())
        _err.print(f"[dim]timings: {phases}  wall={collected.total:.2f}s[/dim]")
