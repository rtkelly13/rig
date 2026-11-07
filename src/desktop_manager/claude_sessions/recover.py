"""Carry out a slot plan against the live Windows Terminal, verifying every session.

Three phases, so no session waits on another's start-up:

1. **Open**: every new window and new tab the plan needs, before anything is typed.
   Their shells start up side by side instead of one after another. A failure here
   stops the run with nothing typed.
2. **Launch**: select each tab, wait for its prompt, type the resume command. Typing
   needs the foreground, so this part is serial, but it takes about a second a session.
   At most `max_starting` sessions may be starting at once (typed, not yet registered),
   which caps the memory spike of many transcripts loading together.
3. **Verify**, interleaved with launching: one poll of the live-session registry covers
   every session that is starting, and each one is reported as it comes up.

The registry is read again just before each command is typed, so a session someone
resumed by hand since the plan was made is left alone, not given a second writer.
"""

from __future__ import annotations

import time
from collections.abc import Callable, Sequence
from dataclasses import dataclass
from pathlib import Path

from desktop_manager.claude_sessions.discovery import LivenessCheck, read_live_sessions
from desktop_manager.claude_sessions.outcomes import FreshOutcome, Outcome
from desktop_manager.claude_sessions.plan import Placement, Slot, resume_command, shell_kind_for_title
from desktop_manager.claude_sessions.startup import FreshTarget, is_idle_tab
from desktop_manager.claude_sessions.wt_uia import (
    UIA_ERRORS,
    TerminalAutomationError,
    WindowsTerminalUia,
    foreground_window,
    restore_foreground,
)


def place_window(hwnd: int, place: Placement) -> None:
    """Move a window into a workspace-plan screen slot."""
    from desktop_manager.placement import placement_rect_for, placement_spec
    from desktop_manager.win32.geometry import adjust_rect_for_window_frame, move_window

    spec = placement_spec(
        place.screen, place.mode, place.slot, orientation=place.orientation if place.mode == "halves" else None
    )
    move_window(hwnd, adjust_rect_for_window_frame(hwnd, placement_rect_for(spec)))


#: Claude asks before it runs in a directory nobody has trusted yet (a fresh worktree,
#: say), and registers the session only after the answer. The answer is the user's.
_TRUST_PROMPT = "trust this folder"


def _why_not_running(uia: WindowsTerminalUia, hwnd: int, tab: tuple[int, ...], waited: float) -> str:
    try:
        uia.select_tab(hwnd, tab)
        screen = uia.selected_text(hwnd)
    except UIA_ERRORS:  # a diagnosis only; must not mask the outcome
        screen = ""
    if _TRUST_PROMPT in screen.lower():
        return f"waiting at Claude's folder-trust prompt in window {hwnd}; answer it there"
    return f"typed, but not running after {waited:.0f}s"


@dataclass(frozen=True)
class _Target:
    slot: Slot
    hwnd: int
    tab: tuple[int, ...]


@dataclass(frozen=True)
class _Starting:
    target: _Target
    typed_at: float


def _open_targets(
    slots: Sequence[Slot],
    uia: WindowsTerminalUia,
    place: Callable[[int, Placement], None],
    on_event: Callable[[str], None],
) -> list[_Target] | Outcome:
    """A concrete tab per slot, opening windows and tabs as needed; stops at the first failure."""
    targets: list[_Target] = []
    new_windows: dict[int, int] = {}
    for slot in slots:
        try:
            if slot.window is not None:
                hwnd = slot.window
                tab = slot.tab if slot.tab is not None else uia.new_tab(hwnd).runtime_id
            elif slot.new_window in new_windows:
                hwnd = new_windows[slot.new_window]
                tab = uia.new_tab(hwnd).runtime_id
            else:
                hwnd = uia.open_window()
                new_windows[slot.new_window] = hwnd
                uia.wait_for_prompt(hwnd)
                selected = uia.selected_tab(hwnd)
                if selected is None:
                    raise TerminalAutomationError("the new window has no selected tab")
                tab = selected.runtime_id
                if slot.place is not None:
                    place(hwnd, slot.place)
                    on_event(f"window {hwnd}: placed at {slot.place.describe}")
        except TerminalAutomationError as error:
            on_event(f"{_label(slot)}: stopped while opening tabs, {error}")
            return Outcome(slot, ok=False, detail=str(error))
        targets.append(_Target(slot, hwnd, tab))
    return targets


def _label(slot: Slot) -> str:
    return f"{slot.session.session_id[:8]} {slot.session.label[:50]}"


def execute(
    slots: Sequence[Slot],
    *,
    uia: WindowsTerminalUia,
    sessions_dir: Path,
    is_alive: LivenessCheck,
    claude_args: Sequence[str] = (),
    max_starting: int = 4,
    stagger_seconds: float = 0.0,
    verify_timeout: float = 60.0,
    poll_seconds: float = 0.5,
    place: Callable[[int, Placement], None] = place_window,
    on_event: Callable[[str], None] = lambda _: None,
) -> list[Outcome]:
    """Resume every slot's session, then give focus back to whatever had it.

    Outcomes come back in slot order. An automation failure stops further launches (the
    terminal is not in the state the plan expected, and typing on regardless is how
    commands land in the wrong prompt), but sessions already typed are still verified.
    """
    original_foreground = foreground_window()
    outcomes: dict[str, Outcome] = {}
    try:
        opened = _open_targets(slots, uia, place, on_event)
        if isinstance(opened, Outcome):
            outcomes[opened.slot.session.session_id] = opened
        queue = [] if isinstance(opened, Outcome) else list(opened)
        starting: list[_Starting] = []
        last_launch: float | None = None
        while queue or starting:
            live = read_live_sessions(sessions_dir, is_alive)
            for entry in list(starting):
                slot = entry.target.slot
                session_id = slot.session.session_id
                waited = time.monotonic() - entry.typed_at
                if session_id in live:
                    outcomes[session_id] = Outcome(slot, ok=True, detail="running", pid=live[session_id].pid)
                    on_event(f"{_label(slot)}: running (pid {live[session_id].pid})")
                elif waited >= verify_timeout:
                    detail = _why_not_running(uia, entry.target.hwnd, entry.target.tab, waited)
                    outcomes[session_id] = Outcome(slot, ok=False, detail=detail)
                    on_event(f"{_label(slot)}: {detail}")
                else:
                    continue
                starting.remove(entry)

            ready = last_launch is None or time.monotonic() - last_launch >= stagger_seconds
            if queue and len(starting) < max_starting and ready:
                target = queue.pop(0)
                slot = target.slot
                session_id = slot.session.session_id
                if session_id in live:
                    detail = f"already running (pid {live[session_id].pid}); left alone"
                    outcomes[session_id] = Outcome(slot, ok=False, detail=detail)
                    on_event(f"{_label(slot)}: {detail}")
                    continue
                try:
                    _type_resume(uia, target, claude_args, on_event)
                except TerminalAutomationError as error:
                    outcomes[session_id] = Outcome(slot, ok=False, detail=str(error))
                    on_event(f"{_label(slot)}: stopped, {error}")
                    queue.clear()
                    continue
                starting.append(_Starting(target, time.monotonic()))
                last_launch = time.monotonic()
                continue
            time.sleep(poll_seconds)
    finally:
        restore_foreground(original_foreground)
    return [outcomes[slot.session.session_id] for slot in slots if slot.session.session_id in outcomes]


def _type_resume(
    uia: WindowsTerminalUia, target: _Target, claude_args: Sequence[str], on_event: Callable[[str], None]
) -> None:
    hwnd, tab = target.hwnd, target.tab
    uia.select_tab(hwnd, tab)
    uia.wait_for_prompt(hwnd)
    title = next((t.title for t in uia.tabs(hwnd) if t.runtime_id == tab), "")
    command = resume_command(
        target.slot.session,
        shell=shell_kind_for_title(title),  # nosec B604 - a terminal shell kind, not subprocess
        claude_args=claude_args,
    )
    on_event(f"{_label(target.slot)}: typing into '{title}' (window {hwnd})")
    uia.type_line(hwnd, tab, command)


def _tab_title(uia: WindowsTerminalUia, hwnd: int, tab: tuple[int, ...]) -> str | None:
    return next((t.title for t in uia.tabs(hwnd) if t.runtime_id == tab), None)


@dataclass(frozen=True, slots=True)
class _Typed:
    target: FreshTarget
    typed_at: float


def start_fresh(
    targets: Sequence[FreshTarget],
    *,
    uia: WindowsTerminalUia,
    verify_timeout: float = 60.0,
    poll_seconds: float = 0.5,
    on_event: Callable[[str], None] = lambda _: None,
) -> list[FreshOutcome]:
    """Type each planned terminal's command into its idle tab, then give focus back.

    A tab that is no longer idle when its turn comes (someone started something in it)
    is left alone. A started session is one whose tab has stopped looking like a bare
    shell: Claude retitles its tab once it is up. An automation failure stops further
    typing, as in `execute`.
    """
    original_foreground = foreground_window()
    outcomes: dict[tuple[int, ...], FreshOutcome] = {}
    typed: list[_Typed] = []
    terminals = [target.terminal for target in targets]
    try:
        for target in targets:
            label = target.describe
            try:
                title = _tab_title(uia, target.window, target.tab)
                if title is None:
                    outcomes[target.tab] = FreshOutcome(target, ok=False, detail="tab has closed; left alone")
                    on_event(f"{label}: tab has closed; left alone")
                    continue
                if not is_idle_tab(title, terminals):
                    outcomes[target.tab] = FreshOutcome(target, ok=False, detail=f"busy now ('{title}'); left alone")
                    on_event(f"{label}: busy now ('{title}'); left alone")
                    continue
                uia.select_tab(target.window, target.tab)
                uia.wait_for_prompt(target.window)
                on_event(f"{label}: typing {target.terminal.command!r}")
                uia.type_line(target.window, target.tab, target.terminal.command_line())
            except UIA_ERRORS as error:
                outcomes[target.tab] = FreshOutcome(target, ok=False, detail=str(error))
                on_event(f"{label}: stopped, {error}")
                break
            typed.append(_Typed(target, time.monotonic()))

        while typed:
            for entry in list(typed):
                target = entry.target
                waited = time.monotonic() - entry.typed_at
                try:
                    title = _tab_title(uia, target.window, target.tab)
                except UIA_ERRORS:
                    title = None
                if title is not None and not is_idle_tab(title, terminals):
                    outcomes[target.tab] = FreshOutcome(target, ok=True, detail=f"started ('{title}')")
                    on_event(f"{target.describe}: started")
                elif waited >= verify_timeout:
                    detail = _why_not_running(uia, target.window, target.tab, waited)
                    outcomes[target.tab] = FreshOutcome(target, ok=False, detail=detail)
                    on_event(f"{target.describe}: {detail}")
                else:
                    continue
                typed.remove(entry)
            if typed:
                time.sleep(poll_seconds)
    finally:
        restore_foreground(original_foreground)
    return [outcomes[target.tab] for target in targets if target.tab in outcomes]
