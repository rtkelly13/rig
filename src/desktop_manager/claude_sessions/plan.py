"""Decide which terminal tab each recovered session goes into. Pure: no UIA, no processes.

Two planners produce the same `Slot`s:

- `plan_groups` follows groups the caller chose (`claude-sessions plan`): each group
  names an open window, or a new window with an optional screen slot, and the sessions
  that belong together there. The agent decides the grouping; this only finds the tabs.
- `assign_slots` decides on its own (`claude-sessions recover`), as below.

`assign_slots`' order of preference, so recovered work lands where the user already
has terminals:

1. **Idle shell tabs** in existing windows: a tab whose title is a bare shell's,
   i.e. nothing is running in it. Claude sets the tab title to a status glyph plus the
   session title (`✳ Fix the build`), so a Claude tab never matches.
2. **New tabs in existing windows**, each going to the window with the fewest tabs,
   so they spread out rather than piling into one window, up to `max_tabs_per_window`.
3. **New windows**, only once every existing window is full.
"""

from __future__ import annotations

import re
import shlex
from collections.abc import Callable, Mapping, Sequence
from dataclasses import dataclass, field
from pathlib import Path
from typing import Annotated, Literal

from pydantic import Field, StrictBool, StrictInt, StrictStr, ValidationError

from desktop_manager.claude_sessions.discovery import SessionRecord
from desktop_manager.claude_sessions.terminal import TerminalTab, TerminalWindow
from rig.typed_json import decode

ShellKind = Literal["powershell", "cmd", "posix"]

PLAN_VERSION = 1


class PlanError(ValueError):
    """The groups or the plan file cannot be carried out as written."""


_IDLE_SHELL_TITLE = re.compile(
    r"""^(?:Administrator:\s*)?(?:
        Windows\ PowerShell | PowerShell(?:\ \d[\d.]*)? | pwsh(?:\ in\ .+)? | Command\ Prompt
        | .*\\(?:powershell|pwsh|cmd)\.exe
    )$""",
    re.IGNORECASE | re.VERBOSE,
)


def is_idle_shell_title(title: str) -> bool:
    """True for titles a shell shows while nothing runs in it."""
    return bool(_IDLE_SHELL_TITLE.match(title.strip()))


def shell_kind_for_title(title: str) -> ShellKind:
    lowered = title.casefold()
    return "cmd" if "command prompt" in lowered or lowered.endswith("cmd.exe") else "powershell"


def resume_command(session: SessionRecord, *, shell: ShellKind, claude_args: Sequence[str] = ()) -> str:
    """One line that changes to the directory the session started in and resumes it there."""
    if session.start_cwd is None:
        raise ValueError(f"session {session.session_id} has no recorded directory")
    if shell == "posix":
        extra = "".join(f" {shlex.quote(arg)}" for arg in claude_args)
        return f"cd {shlex.quote(session.start_cwd)} && claude --resume {session.session_id}{extra}"
    extra = "".join(f" {arg}" for arg in claude_args)
    if shell == "cmd":
        return f'cd /d "{session.start_cwd}" && claude --resume {session.session_id}{extra}'
    quoted = session.start_cwd.replace("'", "''")
    return f"Set-Location -LiteralPath '{quoted}'; claude --resume {session.session_id}{extra}"


@dataclass(frozen=True)
class Placement:
    """A screen slot in the workspace plan's vocabulary, as `claude-sessions place` takes it."""

    screen: int
    mode: str = "halves"
    slot: int = 1
    orientation: str = "vertical"

    @property
    def describe(self) -> str:
        orientation = f"/{self.orientation}" if self.mode == "halves" else ""
        return f"screen {self.screen}: {self.mode}#{self.slot}{orientation}"


@dataclass(frozen=True)
class Slot:
    """Where one session goes.

    `window` None means a new window: slots sharing a `new_window` key share one window,
    the first taking its initial tab and the rest new tabs. `tab` None means a new tab.
    """

    session: SessionRecord
    window: int | None
    tab: tuple[int, ...] | None
    tab_title: str | None = None
    new_window: int = 0
    place: Placement | None = None

    @property
    def describe(self) -> str:
        if self.window is None:
            where = f" at {self.place.describe}" if self.place else ""
            return f"new window {self.new_window + 1}{where}"
        if self.tab is None:
            return f"new tab in window {self.window}"
        return f"idle tab '{self.tab_title}' in window {self.window}"


@dataclass
class _WindowLoad:
    hwnd: int
    tab_count: int
    idle: list[TerminalTab] = field(default_factory=list[TerminalTab])


def assign_slots(
    sessions: Sequence[SessionRecord],
    windows: Sequence[TerminalWindow],
    *,
    reuse_idle: bool = True,
    max_tabs_per_window: int = 8,
    only_window: int | None = None,
) -> list[Slot]:
    loads = [
        _WindowLoad(
            hwnd=window.hwnd,
            tab_count=len(window.tabs),
            idle=[tab for tab in window.tabs if reuse_idle and is_idle_shell_title(tab.title)],
        )
        for window in windows
        if only_window is None or window.hwnd == only_window
    ]
    slots: list[Slot] = []
    pending = list(sessions)

    for load in loads:
        while pending and load.idle:
            tab = load.idle.pop(0)
            slots.append(Slot(pending.pop(0), load.hwnd, tab.runtime_id, tab.title))

    while pending:
        open_windows = [load for load in loads if load.tab_count < max_tabs_per_window]
        if not open_windows:
            break
        target = min(open_windows, key=lambda load: load.tab_count)
        target.tab_count += 1
        slots.append(Slot(pending.pop(0), target.hwnd, None))

    slots.extend(
        Slot(session, None, None, new_window=index // max_tabs_per_window) for index, session in enumerate(pending)
    )
    return slots


@dataclass(frozen=True)
class Group:
    """Sessions the caller wants together: in open window `window`, or a new window when None."""

    sessions: tuple[SessionRecord, ...]
    window: int | None
    place: Placement | None = None
    reuse_idle: bool = True
    name: str | None = None


def plan_groups(groups: Sequence[Group], windows: Sequence[TerminalWindow]) -> list[Slot]:
    """One slot per session, in group order: idle tabs of the group's window first, then new tabs."""
    seen: set[str] = set()
    for group in groups:
        for session in group.sessions:
            if session.session_id in seen:
                raise PlanError(f"{session.session_id[:8]} is in more than one group; resume each session once")
            seen.add(session.session_id)

    by_hwnd = {window.hwnd: window for window in windows}
    taken: set[tuple[int, ...]] = set()
    slots: list[Slot] = []
    new_windows = 0
    for group in groups:
        if group.window is None:
            slots.extend(Slot(s, None, None, new_window=new_windows, place=group.place) for s in group.sessions)
            new_windows += 1
            continue
        if group.place is not None:
            raise PlanError(f"window {group.window}: `place` is only for new windows; move open ones with `place`")
        window = by_hwnd.get(group.window)
        if window is None:
            raise PlanError(f"window {group.window} is not open; see `claude-sessions terminals`")
        idle = [
            (tab.runtime_id, tab.title)
            for tab in window.tabs
            if group.reuse_idle and is_idle_shell_title(tab.title) and tab.runtime_id not in taken
        ]
        for session in group.sessions:
            if idle:
                runtime_id, title = idle.pop(0)
                taken.add(runtime_id)
                slots.append(Slot(session, window.hwnd, runtime_id, title))
            else:
                slots.append(Slot(session, window.hwnd, None))
    return slots


def stale_slots(slots: Sequence[Slot], windows: Sequence[TerminalWindow]) -> list[str]:
    """Why a plan no longer fits the open terminals; empty when it still does."""
    by_hwnd = {window.hwnd: window for window in windows}
    problems: list[str] = []
    for slot in slots:
        if slot.window is None:
            continue
        label = slot.session.session_id[:8]
        window = by_hwnd.get(slot.window)
        if window is None:
            problems.append(f"{label}: window {slot.window} has closed")
            continue
        if slot.tab is None:
            continue
        tab = next((t for t in window.tabs if t.runtime_id == slot.tab), None)
        if tab is None:
            problems.append(f"{label}: idle tab '{slot.tab_title}' in window {slot.window} has closed")
        elif not is_idle_shell_title(tab.title):
            problems.append(f"{label}: idle tab '{slot.tab_title}' in window {slot.window} is now '{tab.title}'")
    return problems


# --- the groups file (agent -> plan) and the plan file (plan -> apply) ------------------------------


@dataclass(frozen=True, slots=True)
class _GroupsFile:
    groups: Annotated[list[object], Field(min_length=1)]


@dataclass(frozen=True, slots=True)
class _GroupInput:
    window: StrictInt | Literal["new"]
    sessions: Annotated[list[StrictStr], Field(min_length=1)]
    name: StrictStr | None = None
    place: Placement | None = None
    reuse_idle: StrictBool = True


@dataclass(frozen=True, slots=True)
class _PlanFile:
    version: object = None
    slots: list[object] | None = None


@dataclass(frozen=True, slots=True)
class _SlotInput:
    session_id: StrictStr
    transcript: StrictStr
    window: StrictInt | None = None
    tab: list[StrictInt] | None = None
    tab_title: StrictStr | None = None
    new_window: StrictInt = 0
    place: Placement | None = None


@dataclass(frozen=True, slots=True)
class _ErrorDetail:
    """The part of one pydantic error this module reports: where it is and what is wrong."""

    loc: tuple[int | str, ...]
    msg: str


def _decode[T](kind: type[T], value: object, where: str, problems: Mapping[str, str]) -> T:
    """``value`` as ``kind``, or a PlanError naming where it is and what is wrong with it."""
    try:
        return decode(kind, value)
    except ValidationError as error:
        first = decode(list[_ErrorDetail], error.errors())[0]
        field = str(first.loc[0]) if first.loc else ""
        problem = problems.get(field, f"`{field}`: {first.msg}" if field else "expected an object")
        raise PlanError(f"{where}: {problem}") from error


_GROUP_PROBLEMS = {
    "window": '`window` must be a window handle from `terminals`, or "new"',
    "sessions": "`sessions` must be a non-empty list of session ids",
    "place": "`place` needs a numeric `screen`; `mode`, `slot` and `orientation` are as in `place`",
}


def parse_groups(data: object, resolve: Callable[[list[str]], list[SessionRecord]]) -> list[Group]:
    """Read the groups file: `{"groups": [{"window": <hwnd> | "new", "sessions": [ids], ...}]}`.

    Optional per group: `name` (shown in output), `place` (new windows only: `screen`,
    `mode`, `slot`, `orientation`) and `reuse_idle` (default true).
    """
    raw = _decode(_GroupsFile, data, "groups file", {"groups": "`groups` must be a non-empty list"})
    groups: list[Group] = []
    for index, value in enumerate(raw.groups):
        group = _decode(_GroupInput, value, f"groups[{index}]", _GROUP_PROBLEMS)
        groups.append(
            Group(
                sessions=tuple(resolve(group.sessions)),
                window=None if group.window == "new" else group.window,
                place=group.place,
                reuse_idle=group.reuse_idle,
                name=group.name,
            )
        )
    return groups


def plan_to_json(slots: Sequence[Slot]) -> dict[str, object]:
    return {
        "version": PLAN_VERSION,
        "slots": [
            {
                "session_id": slot.session.session_id,
                "title": slot.session.label,
                "transcript": str(slot.session.transcript),
                "start_dir": slot.session.start_cwd,
                "window": slot.window,
                "tab": list(slot.tab) if slot.tab is not None else None,
                "tab_title": slot.tab_title,
                "new_window": slot.new_window,
                "place": vars(slot.place) if slot.place else None,
                "describe": slot.describe,
            }
            for slot in slots
        ],
    }


def plan_from_json(data: object, load: Callable[[Path], SessionRecord]) -> list[Slot]:
    """Rebuild a plan's slots, re-reading each transcript so `apply` acts on current facts."""
    raw = _decode(_PlanFile, data, "plan file", {})
    if raw.version != PLAN_VERSION:
        raise PlanError(f"plan file: version {raw.version!r} is not {PLAN_VERSION}; re-run `plan`")
    if raw.slots is None:
        raise PlanError("plan file: `slots` must be a list")
    slots: list[Slot] = []
    seen: set[str] = set()
    needs = "needs `session_id` and `transcript`"
    for index, value in enumerate(raw.slots):
        entry = _decode(_SlotInput, value, f"slots[{index}]", {"session_id": needs, "transcript": needs})
        if entry.session_id in seen:
            raise PlanError(f"{entry.session_id[:8]} is in the plan twice; resume each session once")
        seen.add(entry.session_id)
        session = load(Path(entry.transcript))
        if session.session_id != entry.session_id:
            raise PlanError(f"slots[{index}]: {entry.transcript} is not session {entry.session_id}")
        slots.append(
            Slot(
                session=session,
                window=entry.window,
                tab=tuple(entry.tab) if entry.tab is not None else None,
                tab_title=entry.tab_title,
                new_window=entry.new_window,
                place=entry.place,
            )
        )
    return slots
