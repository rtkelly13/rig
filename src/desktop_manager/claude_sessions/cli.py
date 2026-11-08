"""`desktop-manager claude-sessions`: find dead Claude Code sessions and resume them in the running terminal."""

from __future__ import annotations

import json
import re
import sys
from dataclasses import asdict, dataclass
from datetime import UTC, datetime, timedelta
from pathlib import Path
from typing import TYPE_CHECKING, Annotated

import typer
from rich.console import Console
from rich.markup import escape
from rich.table import Table

from desktop_manager.claude_sessions.discovery import (
    Discovery,
    SessionRecord,
    TurnState,
    claude_home,
    discover,
    process_is_alive,
    read_live_sessions,
    read_transcript,
)
from desktop_manager.claude_sessions.orca import load_orca_folders
from desktop_manager.claude_sessions.plan import (
    Placement,
    PlanError,
    Slot,
    assign_slots,
    is_idle_shell_title,
    parse_groups,
    plan_from_json,
    plan_groups,
    plan_to_json,
    stale_slots,
)
from rig.platforms import Platform, requires
from rig.typed_json import load_json

if TYPE_CHECKING:
    from collections.abc import Sequence

    from desktop_manager.claude_sessions.backends import ResumeOptions, TerminalBackend
    from desktop_manager.claude_sessions.folders import FolderFacts
    from desktop_manager.claude_sessions.startup import LeftOver
    from desktop_manager.placement import MonitorShortfall

claude_sessions_app = typer.Typer(
    name="claude-sessions",
    no_args_is_help=True,
    help=(
        "Find Claude Code sessions that died and resume them: in your open Windows Terminal via UI Automation"
        " on Windows, or as printed commands to paste elsewhere."
    ),
)
console = Console(soft_wrap=True)

_DURATION = re.compile(r"^(\d+(?:\.\d+)?)([mhd])$")


def parse_since(value: str) -> timedelta:
    """'90m', '12h', '2d' -> timedelta."""
    match = _DURATION.match(value.strip().lower())
    if match is None:
        raise typer.BadParameter("use a number and a unit, e.g. 90m, 12h, 2d")
    amount, unit = float(match.group(1)), match.group(2)
    return {"m": timedelta(minutes=amount), "h": timedelta(hours=amount), "d": timedelta(days=amount)}[unit]


_SinceOpt = Annotated[str, typer.Option("--since", help="Only sessions whose transcript changed within this window.")]


_CrashedOnlyOpt = Annotated[
    bool, typer.Option("--crashed-only", help="Skip sessions that exited cleanly (closed tab, /exit). Crashes only.")
]


_IncludeClearedOpt = Annotated[
    bool, typer.Option("--include-cleared", help="Also offer sessions you ended with /clear (left out by default).")
]


def _discover(since: str | timedelta, crashed_only: bool, include_cleared: bool = False) -> Discovery:
    window = since if isinstance(since, timedelta) else parse_since(since)
    return discover(
        since=datetime.now(UTC) - window,
        is_alive=process_is_alive,
        include_exited=not crashed_only,
        include_cleared=include_cleared,
        orca=load_orca_folders(),
    )


def _hours(window: timedelta) -> str:
    return f"{window.total_seconds() / 3600:.0f}h"


def _screen_shortfall(config_path: Path, enable: list[str], disable: list[str]) -> MonitorShortfall | None:
    """Fewer screens than the plan places windows on: restore opened no terminals to type into."""
    from desktop_manager.placement import monitor_shortfall
    from desktop_manager.plan import load_plan_context

    try:
        return monitor_shortfall(load_plan_context(config_path, enable_features=enable, disable_features=disable))
    except (OSError, ValueError):
        return None


def left_over_note(left_over: Sequence[LeftOver], window: timedelta) -> list[str]:
    """The note listing sessions startup did not resume, with the command that resumes them."""
    ids = " ".join(item.session.session_id[:8] for item in left_over)
    return [
        "Claude sessions left unresumed",
        "",
        f"Logon startup did not resume {len(left_over)} session(s) that were cut off:",
        "",
        *(f"  {item.session.session_id[:8]}  {item.session.label[:60]}  ({item.reason})" for item in left_over),
        "",
        "Resume them with:",
        f"  desktop-manager claude-sessions recover --since {_hours(window + timedelta(hours=1))} {ids}",
    ]


def _workstation_locked() -> bool:
    from desktop_manager.chrome.presence import session_locked

    return session_locked()


def _ended(record: SessionRecord) -> str:
    return "cleared" if record.cleared else record.exit_kind


def _when(record: SessionRecord) -> str:
    return record.last_activity.astimezone().strftime("%a %H:%M") if record.last_activity else "?"


_TURN_LABELS = {
    "mid-tool": "[red]mid-task[/red]",
    "unanswered": "[red]no reply[/red]",
    "answered": "waiting on you",
}


def _moved(record: SessionRecord) -> bool:
    return record.last_cwd is not None and record.last_cwd != record.start_cwd


def _session_table(records: list[SessionRecord]) -> Table:
    table = Table(show_lines=False)
    for column in ("id", "last active", "ended", "stopped", "prompts", "MB", "started in", "last in", "title"):
        table.add_column(column, overflow="fold")
    for record in records:
        table.add_row(
            record.session_id[:8],
            _when(record),
            "[red]crashed[/red]" if record.exit_kind == "crashed" else _ended(record),
            _TURN_LABELS[record.turn_state],
            str(record.prompt_count),
            f"{record.size_bytes / 1_048_576:.1f}",
            escape(record.start_cwd or "?"),
            escape(record.last_cwd or "?") if _moved(record) else "[dim]same[/dim]",
            escape(record.label),
        )
    return table


@dataclass(frozen=True, slots=True)
class _ReplyTail:
    """The end of a session's last reply, and whether anything before it was cut."""

    text: str
    truncated: bool


def _reply_tail(record: SessionRecord, reply_chars: int) -> _ReplyTail:
    reply = record.last_reply or ""
    return _ReplyTail(text=reply[-reply_chars:], truncated=len(reply) > reply_chars)


def _last_dir(record: SessionRecord) -> FolderFacts | None:
    from desktop_manager.claude_sessions.folders import folder_facts

    return folder_facts(record.last_cwd) if record.last_cwd else None


@dataclass(frozen=True, slots=True)
class _SessionFacts:
    """Everything the recover skill judges a session on, in one record; `--json` emits its fields in order."""

    session_id: str
    title: str
    last_activity: str | None
    ended: str
    turn_state: TurnState
    prompts: int
    size_bytes: int
    start_dir: str | None
    last_dir: FolderFacts | None
    moved: bool
    last_prompt: str | None
    last_reply_tail: str
    last_reply_truncated: bool


def _session_facts(record: SessionRecord, *, reply_chars: int) -> _SessionFacts:
    reply = _reply_tail(record, reply_chars)
    return _SessionFacts(
        session_id=record.session_id,
        title=record.label,
        last_activity=record.last_activity.isoformat() if record.last_activity else None,
        ended=_ended(record),
        turn_state=record.turn_state,
        prompts=record.prompt_count,
        size_bytes=record.size_bytes,
        start_dir=record.start_cwd,
        last_dir=_last_dir(record),
        moved=_moved(record),
        last_prompt=record.last_prompt,
        last_reply_tail=reply.text,
        last_reply_truncated=reply.truncated,
    )


_ReplyCharsOpt = Annotated[
    int, typer.Option("--reply-chars", min=100, help="Characters kept from the end of the last reply.")
]


@claude_sessions_app.command(name="list")
def list_cmd(
    since: _SinceOpt = "24h",
    crashed_only: _CrashedOnlyOpt = False,
    include_cleared: _IncludeClearedOpt = False,
    as_json: bool = typer.Option(False, "--json", help="Full facts per session, for the recover skill to triage."),
    reply_chars: _ReplyCharsOpt = 1500,
) -> None:
    """List recent sessions that are not running, newest first. Read-only.

    Sessions in repos set up for Orca are left to Orca, and sessions you ended with
    /clear are left out (both under `skipped` in --json): the Claude
    Code SessionEnd hook records each /clear in the session-end ledger
    (~/.claude/session-ends, or $CLAUDE_SESSION_LEDGER_DIR). `--json` is the triage
    input: how each session ended and stopped, the directory it started in (where it
    resumes), the directory it was last working in with its repo, worktree and branch,
    and its last prompt and the end of its last reply.
    """
    found = _discover(since, crashed_only, include_cleared)
    if as_json:
        payload = {
            "candidates": [asdict(_session_facts(r, reply_chars=reply_chars)) for r in found.candidates],
            "running": sorted(found.live),
            "skipped": [
                {"session_id": s.record.session_id, "title": s.record.label, "reason": s.reason} for s in found.skipped
            ],
        }
        typer.echo(json.dumps(payload, indent=2))
        return
    cleared = sum(s.reason == "cleared with /clear" for s in found.skipped)
    orca = sum(s.reason.startswith("left to Orca") for s in found.skipped)
    console.print(
        f"[dim]{len(found.live)} session(s) running now, never offered; {cleared} ended with /clear, left out"
        " (--include-cleared to offer them)."
        + (f" {orca} in repos set up for Orca, left to Orca." if orca else "")
        + "[/dim]"
    )
    if not found.candidates:
        console.print("No resumable sessions in that window.")
        return
    console.print(_session_table(found.candidates))


@claude_sessions_app.command(name="show")
def show_cmd(
    session_ids: list[str] = typer.Argument(..., help="Session ids or unique prefixes, from `list`."),
    since: _SinceOpt = "24h",
    reply_chars: _ReplyCharsOpt = 1500,
) -> None:
    """Print sessions' triage facts for a human: how each stopped, where, and its last exchange. Read-only."""
    for record in _resolve(session_ids, _discover(since, crashed_only=False)):
        reply = _reply_tail(record, reply_chars)
        console.rule(f"{record.session_id[:8]}  {escape(record.label)}", style="dim")
        console.print(f"stopped: {_TURN_LABELS[record.turn_state]}  last active {_when(record)}")
        console.print(f"started in: {escape(record.start_cwd or '?')}")
        if _moved(record):
            last_dir = _last_dir(record)
            console.print(f"last in:    {escape(json.dumps(asdict(last_dir) if last_dir else None))}")
        console.print(f"[bold]last prompt:[/bold] {escape((record.last_prompt or '-')[:500])}")
        console.print("[bold]last reply:[/bold]")
        console.print(escape(("..." if reply.truncated else "") + (reply.text or "-")))


@dataclass(frozen=True, slots=True)
class _WindowWhere:
    """Virtual desktop and screen slot for a window; a field is None when it can't be read."""

    desktop: int | None
    placement: str | None


def _window_where(hwnd: int) -> _WindowWhere:
    from desktop_manager.claude_sessions.desktop_facts import window_desktop, window_slot

    return _WindowWhere(window_desktop(hwnd), window_slot(hwnd))


@claude_sessions_app.command(name="terminals")
@requires(Platform.WINDOWS)
def terminals_cmd(
    as_json: bool = typer.Option(False, "--json", help="Desktops, windows, screen slots and tabs, for the skill."),
) -> None:
    """Show open Windows Terminal windows: virtual desktop, screen slot and tabs (idle shells marked). Read-only."""
    from desktop_manager.claude_sessions.desktop_facts import virtual_desktops
    from desktop_manager.claude_sessions.wt_uia import WindowsTerminalUia

    windows = WindowsTerminalUia().list_windows()
    desktops = virtual_desktops()
    wheres = [_window_where(window.hwnd) for window in windows]
    facts = [
        {
            "hwnd": window.hwnd,
            **asdict(where),
            "tabs": [
                {"index": t.index, "title": t.title, "idle": is_idle_shell_title(t.title), "selected": t.selected}
                for t in window.tabs
            ],
        }
        for window, where in zip(windows, wheres, strict=True)
    ]
    if as_json:
        typer.echo(json.dumps({"desktops": [asdict(d) for d in desktops], "windows": facts}, indent=2))
        return
    names: dict[int | None, str] = {d.number: d.name for d in desktops}
    if not windows:
        console.print("No Windows Terminal windows open.")
    for window, at in zip(windows, wheres, strict=True):
        where = f"desktop {at.desktop} ({names.get(at.desktop, '?')}), {at.placement or 'unplaced'}"
        console.print(f"[bold]window {window.hwnd}[/bold]  {escape(where)}")
        for tab in window.tabs:
            marker = "[green]idle[/green]" if is_idle_shell_title(tab.title) else "busy"
            selected = " *" if tab.selected else ""
            console.print(f"  {tab.index:>2} {marker:<20} {escape(tab.title)}{selected}")


@claude_sessions_app.command(name="place")
@requires(Platform.WINDOWS)
def place_cmd(
    hwnd: int = typer.Argument(..., help="Window handle, from `terminals`."),
    screen: int = typer.Option(..., "--screen", min=1, help="Monitor number, as in the workspace plan."),
    mode: str = typer.Option("halves", "--mode", help="Layout mode, as in the workspace plan (full, halves, ...)."),
    slot: int = typer.Option(1, "--slot", min=1, help="Slot within the layout."),
    orientation: str = typer.Option("vertical", "--orientation", help="For halves: vertical or horizontal."),
) -> None:
    """Move a terminal window into a workspace-plan slot, in the bundles' screen/layout vocabulary."""
    from desktop_manager.claude_sessions.recover import place_window
    from desktop_manager.placement import format_placement
    from desktop_manager.win32.geometry import infer_placement_for_window

    place_window(hwnd, Placement(screen=screen, mode=mode, slot=slot, orientation=orientation))
    placed = infer_placement_for_window(hwnd)
    console.print(f"window {hwnd}: {format_placement(placed) if placed else 'moved (slot not recognised)'}")


def _resolve(ids: list[str], found: Discovery) -> list[SessionRecord]:
    chosen: list[SessionRecord] = []
    for wanted in ids:
        if any(live_id.startswith(wanted) for live_id in found.live):
            raise typer.BadParameter(f"{wanted} is running now; resuming it would put two writers on one transcript")
        matches = [r for r in found.candidates if r.session_id.startswith(wanted)]
        if len(matches) != 1:
            skipped = [
                f"{s.record.session_id[:8]} ({s.reason})"
                for s in found.skipped
                if s.record.session_id.startswith(wanted)
            ]
            hint = f" Skipped: {', '.join(skipped)}." if skipped else " Widen --since?"
            raise typer.BadParameter(f"{wanted} matches {len(matches)} resumable sessions.{hint}")
        if any(record.session_id == matches[0].session_id for record in chosen):
            raise typer.BadParameter(f"{matches[0].session_id[:8]} is named twice; resume each session once")
        chosen.append(matches[0])
    return chosen


_ClaudeArgOpt = Annotated[
    list[str] | None,
    typer.Option(
        "--claude-arg", help="Extra argument for `claude --resume`, repeatable (e.g. --claude-arg=--model=opus)."
    ),
]


_MaxStartingOpt = Annotated[
    int,
    typer.Option(
        "--max-starting",
        min=1,
        help="Most sessions starting at once (typed, not yet running); caps the memory spike of loading transcripts.",
    ),
]


_StaggerOpt = Annotated[
    float, typer.Option("--stagger", min=0, help="Minimum seconds between typing one resume and the next.")
]


_VerifyTimeoutOpt = Annotated[
    float, typer.Option("--verify-timeout", help="Seconds to wait for each session to run once typed.")
]


_PrintOpt = Annotated[
    bool,
    typer.Option("--print", help="Print each session's resume command instead of typing it. The default off Windows."),
]


def _show_slots(slots: list[Slot]) -> None:
    for slot in slots:
        label = escape(slot.session.label[:50])
        console.print(f"  {slot.session.session_id[:8]}  {label:<50}  -> {escape(slot.describe)}")


def _run(slots: list[Slot], backend: TerminalBackend, options: ResumeOptions, *, dry_run: bool) -> None:
    """Show the slots, then resume them and report; exits 1 if any session did not come up."""
    if not backend.drives_terminal:
        backend.resume(slots, options=options, on_event=lambda _: None)
        return
    _show_slots(slots)
    if dry_run:
        console.print("[dim]dry run: nothing typed[/dim]")
        return

    outcomes = backend.resume(
        slots, options=options, on_event=lambda message: console.print(f"[dim]{escape(message)}[/dim]")
    )
    succeeded = sum(outcome.ok for outcome in outcomes)
    not_attempted = len(slots) - len(outcomes)
    suffix = f", {not_attempted} not attempted" if not_attempted else ""
    console.print(f"[bold]{succeeded}/{len(slots)} running[/bold]{suffix}")
    for outcome in outcomes:
        if not outcome.ok:
            console.print(f"  [red]{outcome.slot.session.session_id[:8]}[/red] {escape(outcome.detail)}")
    if succeeded != len(slots):
        raise typer.Exit(1)


def _read_json(path: str) -> object:
    text = sys.stdin.read() if path == "-" else Path(path).read_text(encoding="utf-8")
    try:
        return load_json(text)
    except json.JSONDecodeError as error:
        raise typer.BadParameter(f"{path} is not JSON: {error}") from error


@claude_sessions_app.command(name="plan")
def plan_cmd(
    groups_file: str = typer.Argument(..., help="Groups JSON file, or - for stdin."),
    out: Path | None = typer.Option(None, "--out", help="Write the plan here and print a summary; default stdout."),
    since: _SinceOpt = "24h",
    include_cleared: _IncludeClearedOpt = False,
    print_only: _PrintOpt = False,
) -> None:
    """Turn your grouping of sessions into a tab plan for `apply`. Reads the terminals; types nothing.

    The groups file says which sessions belong together, and where:

        {"groups": [
          {"name": "data-platform", "window": 591312, "sessions": ["eb5b321d", "030a66f7"]},
          {"name": "personal", "window": "new",
           "place": {"screen": 3, "mode": "halves", "slot": 1, "orientation": "vertical"},
           "sessions": ["6d6bc997"]}]}

    `window` is a handle from `terminals`, or "new" (opens on the current desktop;
    `place` puts it in a workspace-plan screen slot). A group's idle tabs are used
    first (`"reuse_idle": false` to skip them), then new tabs. Running sessions,
    cleared ones and sessions named twice are refused.
    """
    from desktop_manager.claude_sessions.backends import select_backend

    found = _discover(since, crashed_only=False, include_cleared=include_cleared)
    backend = select_backend(print_only=print_only)
    try:
        groups = parse_groups(_read_json(groups_file), lambda ids: _resolve(ids, found))
        slots = plan_groups(groups, backend.list_windows())
    except PlanError as error:
        raise typer.BadParameter(str(error)) from error
    payload = json.dumps(plan_to_json(slots), indent=2)
    if out is None:
        typer.echo(payload)
        return
    out.write_text(payload + "\n", encoding="utf-8")
    for group in groups:
        ids = {session.session_id for session in group.sessions}
        where = "new window" if group.window is None else f"window {group.window}"
        console.print(f"[bold]{escape(group.name or where)}[/bold]")
        _show_slots([slot for slot in slots if slot.session.session_id in ids])
    console.print(f"[dim]plan written to {escape(str(out))}; carry it out with `apply {escape(str(out))}`[/dim]")


@claude_sessions_app.command(name="apply")
def apply_cmd(
    plan_file: str = typer.Argument(..., help="Plan JSON from `plan`, or - for stdin."),
    max_starting: _MaxStartingOpt = 4,
    stagger: _StaggerOpt = 0.0,
    verify_timeout: _VerifyTimeoutOpt = 60.0,
    claude_arg: _ClaudeArgOpt = None,
    dry_run: bool = typer.Option(False, "--dry-run", help="Re-check the plan and show it; type nothing."),
    print_only: _PrintOpt = False,
) -> None:
    """Carry out a plan from `plan`, once it is checked against the terminals and the running sessions.

    A window or idle tab the plan names that has since closed or got busy stops the run
    before anything is typed: re-run `plan`. A session running now is left alone and
    reported. The rest open their tabs first, then are typed in turn and verified
    together, at most --max-starting coming up at once.
    """
    from desktop_manager.claude_sessions.backends import ResumeOptions, select_backend

    try:
        slots = plan_from_json(_read_json(plan_file), read_transcript)
    except (PlanError, OSError) as error:
        raise typer.BadParameter(str(error)) from error
    backend = select_backend(print_only=print_only)
    if backend.drives_terminal and (problems := stale_slots(slots, backend.list_windows())):
        console.print("[red]The plan no longer fits the open terminals; nothing typed. Re-run `plan`.[/red]")
        for problem in problems:
            console.print(f"  {escape(problem)}")
        raise typer.Exit(1)
    live = read_live_sessions(claude_home() / "sessions", process_is_alive)
    for slot in slots:
        if slot.session.session_id in live:
            pid = live[slot.session.session_id].pid
            console.print(f"[yellow]{slot.session.session_id[:8]} is running now (pid {pid}); left alone[/yellow]")
    slots = [slot for slot in slots if slot.session.session_id not in live]
    if not slots:
        console.print("Nothing to resume.")
        return
    options = ResumeOptions(
        claude_args=claude_arg or [], max_starting=max_starting, stagger_seconds=stagger, verify_timeout=verify_timeout
    )
    _run(slots, backend, options, dry_run=dry_run)


@claude_sessions_app.command(name="recover")
def recover_cmd(
    session_ids: list[str] = typer.Argument(None, help="Session ids or unique prefixes, from `list`."),
    all_sessions: bool = typer.Option(False, "--all", help="Resume every session `list` would show."),
    since: _SinceOpt = "24h",
    crashed_only: _CrashedOnlyOpt = False,
    include_cleared: _IncludeClearedOpt = False,
    window: int | None = typer.Option(None, "--window", help="Only use this terminal window (hwnd from `terminals`)."),
    reuse_idle: bool = typer.Option(True, "--reuse-idle/--no-reuse-idle", help="Type into idle shell tabs first."),
    max_tabs: int = typer.Option(8, "--max-tabs", min=1, help="Open a new window once a window has this many tabs."),
    max_starting: _MaxStartingOpt = 4,
    stagger: _StaggerOpt = 0.0,
    verify_timeout: _VerifyTimeoutOpt = 60.0,
    claude_arg: _ClaudeArgOpt = None,
    dry_run: bool = typer.Option(False, "--dry-run", help="Show the tab plan; type nothing."),
    print_only: _PrintOpt = False,
) -> None:
    """Resume sessions inside the open Windows Terminal, choosing tabs itself: idle tabs first, then new tabs.

    To choose which sessions go together and where, use `plan` then `apply`. Drives the
    real terminal (UI Automation + keystrokes), so it briefly takes focus for each tab
    and hands it back at the end. Every tab opens first; sessions are then typed in turn
    and verified together, at most --max-starting coming up at once. Off Windows, or
    with `--print`, it drives nothing and prints a `cd <start dir> && claude --resume
    <id>` line per session to paste instead.
    """
    from desktop_manager.claude_sessions.backends import ResumeOptions, select_backend

    found = _discover(since, crashed_only, include_cleared)
    if all_sessions == bool(session_ids):
        raise typer.BadParameter("name session ids or pass --all (not both)")
    chosen = found.candidates if all_sessions else _resolve(session_ids, found)
    if not chosen:
        console.print("Nothing to resume.")
        return

    backend = select_backend(print_only=print_only)
    slots = assign_slots(
        chosen, backend.list_windows(), reuse_idle=reuse_idle, max_tabs_per_window=max_tabs, only_window=window
    )
    options = ResumeOptions(
        claude_args=claude_arg or [], max_starting=max_starting, stagger_seconds=stagger, verify_timeout=verify_timeout
    )
    _run(slots, backend, options, dry_run=dry_run)


@claude_sessions_app.command(name="startup")
def startup_cmd(
    config: Path | None = typer.Option(
        None, "--config", help="Workspace plan whose deferred terminals to fill. Defaults to the platform default."
    ),
    enable_feature: list[str] = typer.Option(
        [], "--enable", metavar="FEATURE", help="Plan feature on, as given to `workspace restore`. Repeatable."
    ),
    disable_feature: list[str] = typer.Option(
        [], "--disable", metavar="FEATURE", help="Plan feature off, as given to `workspace restore`. Repeatable."
    ),
    since: str = typer.Option(
        "7d",
        "--since",
        help="Only sessions whose transcript changed within this window. A week by default, so a weekend or a"
        " few days off still finds Friday's sessions.",
    ),
    crashed_only: bool = typer.Option(
        False,
        "--crashed-only/--include-exited",
        help="Resume only sessions that crashed or stopped mid-task, or also ones that exited cleanly (default)."
        " Sessions ended with /clear are never resumed.",
    ),
    max_tabs: int = typer.Option(10, "--max-tabs", min=1, help="Open no new tab in a window that has this many."),
    max_sessions: int = typer.Option(40, "--max-sessions", min=0, help="Resume at most this many, newest first."),
    max_starting: _MaxStartingOpt = 4,
    stagger: _StaggerOpt = 0.0,
    verify_timeout: _VerifyTimeoutOpt = 120.0,
    claude_arg: _ClaudeArgOpt = None,
    dry_run: bool = typer.Option(False, "--dry-run", help="Print the plan; type nothing, write no log."),
    print_only: _PrintOpt = False,
) -> None:
    """Fill the planned terminals at logon: resume sessions that died, then start `claude` in the rest.

    Run right after `workspace restore --defer-logon-commands`, which opens the plan's
    `command_at_logon: "deferred"` terminals as bare shells. Unattended, never prompts:

    1. Sessions active within --since (7d) that are not running and were not ended
       with /clear are resumed into idle tabs, sessions from the same repo or worktree in
       the same window, new tabs up to --max-tabs. No new windows are opened.
    2. Each planned terminal whose tab no resume took gets its own command typed in.

    Logs to claude-sessions-startup.log in the restore logs directory and ends with
    "resumed N, started M fresh, skipped K". Exit 0 when everything planned came up
    (or there was nothing to do); 1 when a resume or fresh start failed; 3 when
    sessions were left unresumed for lack of room or over --max-sessions (resume them
    with `recover`); 4 when the workstation was locked and nothing was typed (the jobs
    coordinator runs it again on its ticks). A dry run exits 0.
    """
    from desktop_manager import runtime
    from desktop_manager.claude_sessions.backends import ResumeOptions, select_backend
    from desktop_manager.claude_sessions.folders import folder_facts
    from desktop_manager.claude_sessions.startup import DeferredTerminal, group_key
    from desktop_manager.claude_sessions.startup_run import (
        EXIT_DEFERRED,
        STARTUP_LOG_FILE_NAME,
        StartupLog,
        StartupSettings,
        deferred_terminals,
        run_startup,
    )
    from desktop_manager.cli_common import config_option
    from desktop_manager.models import LaunchEntry
    from desktop_manager.plan import deferred_logon_terminals, load_plan, workspace_logs_dir

    config_path = config or config_option()
    log_dir = runtime.workspace_logs_dir() if runtime.running_copy(__file__) else workspace_logs_dir(config_path)
    log = StartupLog(None if dry_run else log_dir / STARTUP_LOG_FILE_NAME, lambda line: console.print(escape(line)))
    window = parse_since(since)
    log(f"claude-sessions startup: plan {config_path}, since {since}")

    shortfall = None if dry_run or print_only else _screen_shortfall(config_path, enable_feature, disable_feature)
    if shortfall is not None:
        log(
            f"deferred: the plan needs {shortfall.required} screens, only {shortfall.live} detected, so restore"
            " opened no terminals; nothing typed"
        )
        raise typer.Exit(EXIT_DEFERRED)

    terminals: list[DeferredTerminal] = []
    entries: list[LaunchEntry] = []
    try:
        workspace_plan = load_plan(config_path, enable_features=enable_feature, disable_features=disable_feature)
        terminals = deferred_terminals(workspace_plan)
        entries = deferred_logon_terminals(workspace_plan)
    except (OSError, ValueError) as error:
        log(f"warning: no planned terminals to start fresh; the plan did not load: {error}")

    def prepare() -> None:
        from desktop_manager.terminal_init import ensure_terminal_command_initialized

        for entry in entries:
            ensure_terminal_command_initialized(entry)

    roots: dict[str, str | None] = {}

    def git_root(path: str) -> str | None:
        if path not in roots:
            roots[path] = folder_facts(path).git_root
        return roots[path]

    settings = StartupSettings(
        include_exited=not crashed_only,
        max_tabs=max_tabs,
        max_sessions=max_sessions,
        verify_timeout=verify_timeout,
        options=ResumeOptions(
            claude_args=claude_arg or [],
            max_starting=max_starting,
            stagger_seconds=stagger,
            verify_timeout=verify_timeout,
        ),
    )
    backend = select_backend(print_only=print_only)
    summary = run_startup(
        _discover(window, crashed_only=False),
        backend,
        terminals,
        settings,
        group_of=lambda session: group_key(session, git_root),
        log=log,
        dry_run=dry_run,
        prepare=prepare,
        locked=_workstation_locked if backend.drives_terminal else lambda: False,
    )
    log(("dry run: would have " if dry_run else "") + summary.line)
    if dry_run:
        return
    if summary.left_over and not summary.deferred:
        typer.echo(
            f"{len(summary.left_over)} session(s) were left unresumed; see `claude-sessions list` and resume them"
            " with `claude-sessions recover`.",
            err=True,
        )
        if backend.drives_terminal:
            from desktop_manager.restore_fallbacks import open_note

            open_note("claude-sessions-left-over.txt", left_over_note(summary.left_over, window))
    if summary.exit_code:
        raise typer.Exit(summary.exit_code)
