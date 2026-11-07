"""`estate wrapup`: ask idle Orca agent sessions whether their change is finished, then report the answers.

The rules live in `wrapup.py`; this module parses flags and draws the table. `estate wrapup report` is
the other side: the asked agent runs it to record its verdict (`wrapup_report.py`).
"""

from __future__ import annotations

import datetime as dt
import re
from dataclasses import dataclass
from pathlib import Path

import typer
from rich.console import Console
from rich.markup import escape
from rich.table import Table

from rig import roots
from rig.estate import wrapup, wrapup_follow, wrapup_record, wrapup_report, wrapup_watch
from rig.estate.orca_runtime import ORCA_UNAVAILABLE_EXIT
from rig.estate.wrapup import Options, Row, Scope, Status, Target, WrapupError
from rig.typed_json import encode_json

console = Console()
err = Console(stderr=True)

wrapup_app = typer.Typer(
    invoke_without_command=True,
    help="Ask idle Orca Claude sessions whether their change is finished; `report` is how they answer.",
)

_DURATION = re.compile(r"^\s*(?P<n>\d+)\s*(?P<unit>[smhd]?)\s*$")
_UNIT_SECONDS = {"s": 1, "m": 60, "": 60, "h": 3600, "d": 86400}
_STYLE: dict[Status, str] = {
    Status.ASK: "yellow",
    Status.SENT: "green",
    Status.SEND_FAILED: "bold red",
    Status.DEFERRED: "dim yellow",
    Status.ANSWERED: "cyan",
    Status.ANSWERING: "cyan",
    Status.SETTLED: "green",
    Status.WORKING: "dim",
    Status.WAITING: "bold magenta",
    Status.RECENT: "dim",
    Status.NO_TERMINAL: "dim",
    Status.UNCONFIRMED: "bold yellow",
}


def parse_duration(text: str) -> dt.timedelta:
    """`30m`, `2h`, `45s`, `1d`; a bare number is minutes."""
    found = _DURATION.match(text)
    if found is None:
        raise typer.BadParameter(f"{text!r}: expected a duration such as 30m, 2h or 45s")
    return dt.timedelta(seconds=int(found.group("n")) * _UNIT_SECONDS[found.group("unit")])


def _scope(personal: bool, work: bool) -> Scope:
    if personal == work:
        raise typer.BadParameter("pass exactly one of --personal or --work")
    return Scope.PERSONAL if personal else Scope.WORK


def _target(scope: Scope, repos: list[str], all_repos: bool, worktrees: list[str], children: bool) -> Target:
    if all_repos and repos:
        raise typer.BadParameter("--all and --repo are exclusive")
    if not (all_repos or repos or worktrees):
        raise typer.BadParameter("name a target: --repo NAME (repeatable), --worktree NAME, or --all")
    return Target(scope=scope, repos=tuple(repos), all_repos=all_repos, worktrees=tuple(worktrees), children=children)


@dataclass(frozen=True, slots=True)
class _JsonRow:
    repo: str
    worktree: str
    path: str
    branch: str
    depth: int
    session: str
    state: str
    status: str
    idle_minutes: int | None
    facts: str
    settled: bool
    verdict: str | None
    verdict_detail: str
    open_children: int
    note: str
    #: From the worktree's temp/.rig/wrapup.json: when this pane was last asked, and its last recorded verdict.
    asked_at: str | None
    recorded_verdict: str | None
    recorded_at: str | None


def _progress(row: Row, message: str) -> None:
    err.print(f"[dim]{escape(row.repo)}/{escape(row.worktree)}:[/dim] {escape(message)}")


def _verdict_text(row: Row) -> str:
    if row.verdict is None:
        return ""
    if row.verdict.done:
        return "[green]done[/green]"
    return "[yellow]pending[/yellow]" + (f": {escape(row.verdict.detail)}" if row.verdict.detail else "")


def _print(rows: list[Row], *, applied: bool, truncated: bool, background: bool = False) -> None:
    if not rows:
        console.print("[dim]No matching Claude sessions in Orca.[/dim]")
        return
    if background:
        mode = "[green]ASKING IN THE BACKGROUND[/green]"
    else:
        mode = "[green]APPLIED[/green]" if applied else "[yellow]DRY-RUN (pass --apply to send)[/yellow]"
    table = Table(title=f"Orca wrap-up ({mode})", expand=True)
    table.add_column("Repo / worktree", no_wrap=True)
    table.add_column("Session")
    table.add_column("Idle", justify="right", no_wrap=True)
    table.add_column("Facts")
    table.add_column("Status", no_wrap=True)
    table.add_column("Answer / note")
    for row in rows:
        indent = "  " * row.depth + ("└ " if row.depth else "")
        idle = f"{row.idle_minutes}m" if row.idle_minutes is not None else ""
        style = _STYLE[row.status]
        answer = _verdict_text(row)
        pending_children = wrapup.open_children(row, rows)
        extra = [escape(row.note)] if row.note else []
        if row.children:
            extra.append(f"[dim]{pending_children} of the subtask sessions not finished[/dim]")
        table.add_row(
            f"{indent}[bold]{escape(row.repo)}[/bold]/{escape(row.worktree)}",
            escape(row.session),
            idle,
            escape(wrapup.describe_facts(row.facts, row.pr)),
            f"[{style}]{row.status.value}[/{style}]",
            "\n".join(x for x in [answer, *extra] if x),
        )
    console.print(table)
    counts: dict[Status, int] = {}
    for row in rows:
        counts[row.status] = counts.get(row.status, 0) + 1
    console.print(" · ".join(f"{n} {status.value}" for status, n in counts.items()))
    if truncated:
        err.print("[yellow]Orca returned only part of the worktree list; some sessions are missing.[/yellow]")


def _json(rows: list[Row]) -> str:
    payload = [
        _JsonRow(
            repo=r.repo,
            worktree=r.worktree,
            path=Path(r.path).as_posix(),
            branch=r.branch,
            depth=r.depth,
            session=r.session,
            state=r.state,
            status=r.status.value,
            idle_minutes=r.idle_minutes,
            facts=wrapup.describe_facts(r.facts, r.pr),
            settled=wrapup.is_settled(r.facts, r.pr),
            verdict=None if r.verdict is None else ("done" if r.verdict.done else "pending"),
            verdict_detail=r.verdict.detail if r.verdict is not None else "",
            open_children=wrapup.open_children(r, rows),
            note=r.note,
            asked_at=r.asked.at if r.asked is not None else None,
            recorded_verdict=r.recorded.verdict if r.recorded is not None else None,
            recorded_at=r.recorded.at if r.recorded is not None else None,
        )
        for r in rows
    ]
    return encode_json(list[_JsonRow], payload)


@dataclass(frozen=True, slots=True)
class _Flags:
    """The targeting flags, to hand to the background follower unchanged."""

    personal: bool
    repos: tuple[str, ...]
    all_repos: bool
    worktrees: tuple[str, ...]
    children: bool
    idle: str
    max_sends: int
    wait: str
    include_settled: bool
    template: Path

    def argv(self) -> list[str]:
        args = ["--personal" if self.personal else "--work"]
        args += [x for r in self.repos for x in ("--repo", r)]
        args += ["--all"] if self.all_repos else []
        args += [x for w in self.worktrees for x in ("--worktree", w)]
        args += ["--children"] if self.children else []
        args += ["--idle", self.idle, "--max", str(self.max_sends), "--wait", self.wait]
        args += ["--include-settled"] if self.include_settled else []
        return [*args, "--template", str(self.template.resolve()), "--apply"]


def _in_background(rows: list[Row], flags: _Flags, *, now: dt.datetime, truncated: bool, json_output: bool) -> None:
    """Start a follower for the asks, unless one is already running, and say where its log is."""
    asks = sum(r.status is Status.ASK for r in rows)
    if json_output:
        typer.echo(_json(rows))
    else:
        _print(rows, applied=False, truncated=truncated, background=asks > 0)
    if not asks:
        return
    if wrapup_follow.running():
        err.print(
            "[yellow]A wrap-up follower is already asking sessions; nothing new was started.[/yellow] "
            + f"Its log is in {escape(wrapup_follow.log_dir().as_posix())}."
        )
        raise typer.Exit(code=1)
    log = wrapup_follow.start(flags.argv(), now=now)
    err.print(
        f"Asking {asks} in the background, {max(flags.max_sends, 1)} at a time, up to {flags.wait} each. "
        + f"Log: {escape(log.as_posix())}\n"
        + "Agents record their answers with `rig estate wrapup report`; run this again without --apply to read them."
    )


@wrapup_app.callback()
def wrapup_command(
    ctx: typer.Context,
    personal: bool = typer.Option(
        False, "--personal", help="Personal and org repos (personal/, rtkelly-labs/, Resultful/)"
    ),
    work: bool = typer.Option(False, "--work", help="Work repos: the work_dirs in ~/.workspace/config.json"),
    repo: list[str] = typer.Option([], "--repo", "-r", help="Orca repo name; repeatable"),
    all_repos: bool = typer.Option(False, "--all", help="Every repo in the chosen scope"),
    worktree: list[str] = typer.Option(
        [], "--worktree", "-w", help="Worktree name, folder, branch or path; repeatable. Narrows --repo/--all"
    ),
    children: bool = typer.Option(False, "--children", help="Include child worktrees (Orca subtasks), across repos"),
    idle: str = typer.Option("30m", "--idle", help="Ask only sessions idle at least this long (30m, 2h)"),
    max_sends: int = typer.Option(
        wrapup.DEFAULT_MAX_SENDS,
        "--max",
        min=0,
        help="Most sessions asked at once. With --wait 0, most asked in one run, and the rest show as deferred",
    ),
    wait: str = typer.Option(
        "10m",
        "--wait",
        help="With --apply, how long each asked session gets to answer before the next is asked anyway (0: no waiting)",
    ),
    include_settled: bool = typer.Option(
        False, "--include-settled", help="Also ask worktrees the git and PR facts already show as finished"
    ),
    template: Path | None = typer.Option(None, "--template", help="Prompt template (default config/orca-wrapup.md)"),
    show_prompt: bool = typer.Option(False, "--show-prompt", help="Print the prompt each asked session gets"),
    apply: bool = typer.Option(False, "--apply", help="Send the prompts (default previews)"),
    follow: bool = typer.Option(
        False, "--follow", help="With --apply, ask in this terminal instead of starting a background follower"
    ),
    json_output: bool = typer.Option(False, "--json", help="Emit rows as JSON"),
) -> None:
    """Ask idle Orca Claude sessions whether their change is finished, then report each verdict.

    The prompt asks each agent to run `rig estate wrapup report` when it has checked what it needs,
    which records its verdict in the worktree's temp/.rig/wrapup.json. --apply starts a background
    follower that asks --max sessions at a time: as each one answers, starts waiting on you, or has
    had --wait, the next is asked, subtasks before their parents. Run again without --apply to read
    the answers. --follow does the asking in this terminal instead.
    Clean, pushed, merged worktrees count as settled and are not asked. Busy sessions, sessions
    waiting on a question and sub-agent panes are never sent anything.
    """
    if ctx.invoked_subcommand is not None:
        return
    target = _target(_scope(personal, work), repo, all_repos, worktree, children)
    wait_for = parse_duration(wait)
    rolling = wait_for > dt.timedelta(0)
    options = Options(
        idle=parse_duration(idle), max_sends=None if rolling else max_sends, include_settled=include_settled
    )
    template_file = template or wrapup.template_path()
    try:
        prompt_template = template_file.read_text(encoding="utf-8")
    except OSError as exc:
        err.print(f"[red]Cannot read the prompt template {template_file.as_posix()}: {exc}[/red]")
        raise typer.Exit(code=1) from exc

    try:
        snapshot = wrapup.read_snapshot()
    except WrapupError as exc:
        err.print(f"[red]{escape(str(exc))}[/red]")
        raise typer.Exit(code=ORCA_UNAVAILABLE_EXIT) from exc

    now = dt.datetime.now(dt.UTC)
    placed = wrapup.select(snapshot, target, roots.code_root())
    rows = wrapup.plan(placed, snapshot.terminals, now=now, options=options)
    by_id = {p.worktree.worktree_id: p.worktree for p in placed}

    if show_prompt:
        for i in wrapup.send_order(rows):
            row = rows[i]
            console.rule(f"{escape(row.repo)}/{escape(row.worktree)}: {escape(row.session)}")
            console.print(escape(wrapup.render_prompt(prompt_template, row, rows, by_id)), soft_wrap=True)

    # Verdicts are recorded on every run, asks only when sent: temp/.rig/ is bookkeeping, not the estate.
    if apply and rolling and not follow:
        rows = wrapup_record.record(rows, now=now)
        flags = _Flags(
            personal,
            tuple(repo),
            all_repos,
            tuple(worktree),
            children,
            idle,
            max_sends,
            wait,
            include_settled,
            template_file,
        )
        _in_background(rows, flags, now=now, truncated=snapshot.truncated, json_output=json_output)
        return
    if apply and rolling:
        with wrapup_follow.hold() as acquired:
            if not acquired:
                err.print("[yellow]Another wrap-up follower is asking sessions; this one stops.[/yellow]")
                raise typer.Exit(code=1)
            rows = wrapup_record.record(rows, now=now)
            rows = wrapup_watch.ask_rolling(
                rows, prompt_template, by_id, max_in_flight=max_sends, wait=wait_for, progress=_progress
            )
    else:
        if apply:
            rows = wrapup.apply_sends(rows, prompt_template, by_id)
        rows = wrapup_record.record(rows, now=now, template=prompt_template if apply else None)

    if json_output:
        typer.echo(_json(rows))
    else:
        _print(rows, applied=apply, truncated=snapshot.truncated)
        asks = sum(r.status is Status.ASK for r in rows)
        if rolling and not apply and asks:
            console.print(
                f"[dim]--apply asks these {asks}, {max(max_sends, 1)} at a time, waiting up to {wait} each[/dim]"
            )

    if any(r.status is Status.SEND_FAILED for r in rows):
        raise typer.Exit(code=1)


@wrapup_app.command(name="report")
def report_command(
    ask: str = typer.Option("", "--ask", help="The ask id from the prompt. Default: the latest unanswered ask here"),
    done: bool = typer.Option(False, "--done", help="The change has reached its natural end"),
    pending: str | None = typer.Option(None, "--pending", help="Short summary of what is left"),
    todo: list[str] = typer.Option([], "--todo", help="One thing left for the owner; repeatable"),
) -> None:
    """Record your wrap-up verdict for the worktree you are in. Run by the asked agent, from its worktree.

    Pass --done, or --pending "<what's left>" with one --todo per item.

    Items are PRs to open or merge, issues to file, pushes to make, and decisions only the owner can make.
    """
    if done == (pending is not None):
        raise typer.BadParameter("pass exactly one of --done or --pending TEXT")
    if pending is not None and not (pending.strip() or todo):
        raise typer.BadParameter("--pending needs a summary of what is left, or at least one --todo")
    root = wrapup_report.worktree_root(Path.cwd())
    if root is None:
        err.print("[red]Not inside a git worktree:[/red] run this from the worktree you were asked about.")
        raise typer.Exit(code=1)
    result = wrapup_report.report(
        root, ask_id=ask, done=done, pending=pending or "", todo=tuple(todo), now=dt.datetime.now(dt.UTC)
    )
    if result.answer is None:
        err.print(
            f"[red]Not recorded:[/red] {escape(result.error)}. " + "Reply with the WRAPUP line from the prompt instead."
        )
        raise typer.Exit(code=1)
    said = "done" if done else f"pending: {result.answer.detail}"
    console.print(f"Recorded for {escape(root.name)}: {escape(said)}")
