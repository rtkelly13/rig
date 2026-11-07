"""Plan what `claude-sessions startup` does at logon. Pure: no UIA, no processes, no files.

At logon `workspace restore --defer-logon-commands` opens the plan's deferred terminals
as bare shells. Startup then fills them, in this order:

1. **Recovered sessions first.** Sessions that died within the window are resumed into
   idle tabs, grouped so sessions whose last directory is in the same repo or worktree
   share a window:

   - a group's sessions go to the window its first session landed in: an idle tab there,
     else a new tab while the window has fewer than `max_tabs_per_window` tabs;
   - otherwise an idle tab in a window no other group has taken, then any idle tab, then
     a new tab in the open window with the fewest tabs;
   - no new windows are opened at logon. A session with nowhere to go is left over.

2. **Fresh sessions after.** Each planned terminal whose tab no resume took gets its
   own command (`claude`) typed into it.

A tab is idle when its title is a bare shell's (see `plan.is_idle_shell_title`) or the
`window_title` of a deferred terminal, which a shell keeps until its prompt retitles it.
"""

from __future__ import annotations

from collections.abc import Callable, Sequence
from dataclasses import dataclass, field
from pathlib import PurePath

from desktop_manager.claude_sessions.discovery import SessionRecord
from desktop_manager.claude_sessions.plan import Slot, is_idle_shell_title
from desktop_manager.claude_sessions.terminal import TerminalTab, TerminalWindow


@dataclass(frozen=True, slots=True)
class DeferredTerminal:
    """A plan terminal whose command waits for startup: where it runs and what it runs."""

    name: str
    window_title: str | None
    cwd: str
    command: str

    def command_line(self) -> str:
        """The PowerShell line startup types into the terminal's bare shell."""
        quoted = self.cwd.replace("'", "''")
        return f"Set-Location -LiteralPath '{quoted}'; {self.command}"


@dataclass(frozen=True, slots=True)
class FreshTarget:
    """A planned terminal's idle tab that gets a fresh session."""

    window: int
    tab: tuple[int, ...]
    tab_title: str
    terminal: DeferredTerminal

    @property
    def describe(self) -> str:
        return f"'{self.tab_title}' in window {self.window} ({self.terminal.name})"


@dataclass(frozen=True, slots=True)
class LeftOver:
    """A candidate session startup does not resume, and why."""

    session: SessionRecord
    reason: str


@dataclass(frozen=True, slots=True)
class StartupPlan:
    resume: tuple[Slot, ...]
    fresh: tuple[FreshTarget, ...]
    left_over: tuple[LeftOver, ...]
    #: Planned terminals with no idle tab to type into (not opened, or already busy).
    unmatched: tuple[DeferredTerminal, ...]

    @property
    def is_empty(self) -> bool:
        return not self.resume and not self.fresh


def wants_resume_at_logon(record: SessionRecord, *, include_exited: bool) -> bool:
    """Whether startup resumes this session: by default every one discovery offers.

    Discovery has already dropped running and /clear'd sessions, /clear being how a
    session is marked done. `include_exited=False` (`--crashed-only`) narrows it to
    sessions that crashed, or were mid-task or unanswered when they stopped.
    """
    return include_exited or record.exit_kind == "crashed" or record.turn_state != "answered"


def is_idle_tab(title: str, terminals: Sequence[DeferredTerminal]) -> bool:
    folded = title.strip().casefold()
    return is_idle_shell_title(title) or any(
        terminal.window_title and terminal.window_title.strip().casefold() == folded for terminal in terminals
    )


@dataclass(frozen=True, slots=True)
class _IdleTab:
    window: int
    tab: TerminalTab


@dataclass
class _Window:
    hwnd: int
    tab_count: int
    idle: list[TerminalTab] = field(default_factory=list[TerminalTab])


def _leaf(path: str) -> str:
    return PurePath(path.rstrip("\\/")).name.casefold()


@dataclass(frozen=True, slots=True)
class _Pairing:
    by_tab: dict[tuple[int, ...], DeferredTerminal]
    unpaired: list[DeferredTerminal]


def _match_terminals(idle: Sequence[_IdleTab], terminals: Sequence[DeferredTerminal]) -> _Pairing:
    """Pair idle tabs with planned terminals, at most one tab each.

    Best effort, as a shell prompt retitles its tab: a tab still showing a terminal's
    `window_title` first, then a tab whose title ends in the terminal's folder name
    (`pwsh in workspace`), then the remaining tabs in order.
    """
    paired: dict[tuple[int, ...], DeferredTerminal] = {}
    pending = list(terminals)

    def pair(matches: Callable[[str, DeferredTerminal], bool]) -> None:
        for item in idle:
            if item.tab.runtime_id in paired:
                continue
            terminal = next((t for t in pending if matches(item.tab.title.strip().casefold(), t)), None)
            if terminal is not None:
                paired[item.tab.runtime_id] = terminal
                pending.remove(terminal)

    pair(lambda title, t: bool(t.window_title) and title == (t.window_title or "").strip().casefold())
    pair(lambda title, t: bool(_leaf(t.cwd)) and title.endswith(f" in {_leaf(t.cwd)}"))
    pair(lambda _title, _t: True)
    return _Pairing(paired, pending)


def _ordered(sessions: Sequence[SessionRecord], group_of: Callable[[SessionRecord], str]) -> list[SessionRecord]:
    """Grouped, groups in order of their first (newest) session, each group newest first."""
    groups: dict[str, list[SessionRecord]] = {}
    for session in sessions:
        groups.setdefault(group_of(session), []).append(session)
    return [session for members in groups.values() for session in members]


def plan_startup(
    sessions: Sequence[SessionRecord],
    windows: Sequence[TerminalWindow],
    terminals: Sequence[DeferredTerminal],
    *,
    group_of: Callable[[SessionRecord], str],
    max_tabs_per_window: int = 10,
    max_sessions: int = 40,
) -> StartupPlan:
    """Which sessions resume where, and which planned terminals start fresh. `sessions` newest first."""
    kept, over = list(sessions[:max_sessions]), sessions[max_sessions:]
    left_over = [LeftOver(s, f"over the --max-sessions cap of {max_sessions}") for s in over]

    loads = [
        _Window(
            hwnd=window.hwnd,
            tab_count=len(window.tabs),
            idle=[tab for tab in window.tabs if is_idle_tab(tab.title, terminals)],
        )
        for window in windows
    ]
    idle_at_start = [_IdleTab(load.hwnd, tab) for load in loads for tab in load.idle]
    pairing = _match_terminals(idle_at_start, terminals)
    paired = pairing.by_tab

    claimed_by: dict[str, _Window] = {}
    claimed: set[int] = set()
    resume: list[Slot] = []

    def take_idle(load: _Window, session: SessionRecord) -> Slot:
        # Tabs with no planned terminal go first, so planned terminals keep theirs for fresh starts.
        tab = next((t for t in load.idle if t.runtime_id not in paired), load.idle[0])
        load.idle.remove(tab)
        return Slot(session, load.hwnd, tab.runtime_id, tab.title)

    def new_tab(load: _Window, session: SessionRecord) -> Slot:
        load.tab_count += 1
        return Slot(session, load.hwnd, None)

    for session in _ordered(kept, group_of):
        group = group_of(session)
        home = claimed_by.get(group)
        slot: Slot | None = None
        target: _Window | None = None
        if home is not None and home.idle:
            target, slot = home, take_idle(home, session)
        elif home is not None and home.tab_count < max_tabs_per_window:
            target, slot = home, new_tab(home, session)
        else:
            target = next((load for load in loads if load.idle and load.hwnd not in claimed), None) or next(
                (load for load in loads if load.idle), None
            )
            if target is not None:
                slot = take_idle(target, session)
            else:
                roomy = [load for load in loads if load.tab_count < max_tabs_per_window]
                if roomy:
                    target = min(roomy, key=lambda load: load.tab_count)
                    slot = new_tab(target, session)
        if slot is None or target is None:
            reason = (
                f"no idle tab and every window has {max_tabs_per_window} tabs"
                if loads
                else "no Windows Terminal window is open"
            )
            left_over.append(LeftOver(session, reason))
            continue
        resume.append(slot)
        claimed.add(target.hwnd)
        claimed_by.setdefault(group, target)

    used = {slot.tab for slot in resume if slot.tab is not None}
    fresh = [
        FreshTarget(item.window, item.tab.runtime_id, item.tab.title, paired[item.tab.runtime_id])
        for item in idle_at_start
        if item.tab.runtime_id in paired and item.tab.runtime_id not in used
    ]
    return StartupPlan(
        resume=tuple(resume), fresh=tuple(fresh), left_over=tuple(left_over), unmatched=tuple(pairing.unpaired)
    )


def group_key(session: SessionRecord, git_root: Callable[[str], str | None]) -> str:
    """The repo or worktree a session was last working in; its directory outside git."""
    path = session.last_cwd or session.start_cwd or ""
    return ((git_root(path) if path else None) or path).casefold()
