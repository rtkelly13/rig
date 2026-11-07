"""Chrome's macOS scripting dictionary: stable window and tab IDs, titles and URLs.

JXA runs in osascript, never in a web page. Its JSON is decoded here into frozen
records, so the reader and actions never pick a raw dict apart. Anything that
does not fit raises `ChromeMacError`; a caller must not continue on a partial
snapshot.
"""

from __future__ import annotations

import json
import subprocess
from dataclasses import dataclass, field
from pathlib import Path
from typing import Annotated

from pydantic import BeforeValidator, StrictBool, StrictInt, StrictStr, ValidationError

from desktop_manager.chrome.tabs import ChromeTab
from rig.typed_json import Lenient, decode, load_json, try_decode_json

#: The largest integer JXA holds exactly; an ID beyond it could name another tab.
MAX_JXA_INTEGER = 2**53 - 1

SNAPSHOT_SCRIPT = r"""
ObjC.import('AppKit');
function run() {
    const apps = $.NSRunningApplication.runningApplicationsWithBundleIdentifier('com.google.Chrome');
    if (Number(apps.count) === 0) return JSON.stringify({pid: 0, frontmost: false, windows: []});
    const app = apps.objectAtIndex(0);
    const chrome = Application('com.google.Chrome');
    return JSON.stringify({pid: Number(app.processIdentifier), frontmost: Number(app.active) === 1,
        windows: chrome.windows().map(w => ({id: String(w.id()), index: w.index(),
            title: w.title(), active: w.activeTabIndex(), minimized: w.minimized(), mode: w.mode(),
            bounds: w.bounds(), tabs: w.tabs().map(t => ({id: String(t.id()), title: t.title(),
                url: t.url()}))}))});
}
"""

_TAB_ACTION_SCRIPT = r"""
ObjC.import('AppKit');
function run() {
    const apps = $.NSRunningApplication.runningApplicationsWithBundleIdentifier('com.google.Chrome');
    if (Number(apps.count) !== 1) return JSON.stringify({status: 'missing'});
    const chrome = Application('com.google.Chrome');
    const windowID = __WINDOW_ID__, tabID = __TAB_ID__;
    const windows = chrome.windows().filter(w => Number(w.id()) === windowID);
    if (windows.length !== 1) return JSON.stringify({status: 'missing'});
    const window = chrome.windows.byId(windowID), tabs = window.tabs();
    const matches = [];
    tabs.forEach((t, i) => { if (Number(t.id()) === tabID) matches.push(i); });
    if (matches.length !== 1) return JSON.stringify({status: 'missing'});
    const target = window.tabs.byId(tabID);
    __ACTION__
}
"""

_CLOSE_ACTION = r"""
    if (target.title() !== expected.title || target.url() !== expected.url)
        return JSON.stringify({status: 'stale'});
    target.close();
    return JSON.stringify({status: 'invoked'});
"""

SELECT_ACTION = r"""
    window.activeTabIndex = matches[0] + 1;
    return JSON.stringify({status: Number(window.activeTab().id()) === tabID ? 'selected' : 'failed'});
"""

ACTIVATE_SCRIPT = r"""
ObjC.import('AppKit');
function run() {
    const apps = $.NSRunningApplication.runningApplicationsWithBundleIdentifier('com.google.Chrome');
    if (Number(apps.count) !== 1) return JSON.stringify({status: 'missing'});
    const chrome = Application('com.google.Chrome'), id = __ID__;
    if (chrome.windows().filter(w => Number(w.id()) === id).length !== 1)
        return JSON.stringify({status: 'missing'});
    const window = chrome.windows.byId(id);
    window.minimized = false;
    window.index = 1;
    chrome.activate();
    return JSON.stringify({status: 'invoked'});
}
"""

_RESTORE_SCRIPT = r"""
ObjC.import('AppKit');
function run() {
    const state = __STATE__;
    const apps = $.NSRunningApplication.runningApplicationsWithBundleIdentifier('com.google.Chrome');
    if (Number(apps.count) === 1) {
        const chrome = Application('com.google.Chrome'), windows = chrome.windows();
        if (windows.filter(w => Number(w.id()) === state.front).length === 1)
            chrome.windows.byId(state.front).index = 1;
        if (windows.filter(w => Number(w.id()) === state.target).length === 1)
            chrome.windows.byId(state.target).minimized = state.minimized;
    }
    return JSON.stringify({status: 'restored'});
}
"""

_FRONTMOST_SCRIPT = r"""
ObjC.import('AppKit');
function run() {
    const app = $.NSWorkspace.sharedWorkspace.frontmostApplication;
    return JSON.stringify({pid: app ? Number(app.processIdentifier) : 0});
}
"""

LOCAL_STATE = Path("Library/Application Support/Google/Chrome/Local State")


class ChromeMacError(RuntimeError):
    """Chrome automation failed; callers must not continue with a partial snapshot."""


def _positive_id(value: object) -> int:
    """A scripting ID: a positive decimal integer JXA can hold exactly, as text or number."""
    if isinstance(value, bool) or not isinstance(value, (str, int)):
        raise ValueError("not an ID")
    text = str(value)
    if not (text.isascii() and text.isdigit()) or not 0 < int(text) <= MAX_JXA_INTEGER:
        raise ValueError("not a positive ID")
    return int(text)


type ScriptId = Annotated[int, BeforeValidator(_positive_id)]


@dataclass(frozen=True, slots=True)
class JxaTab:
    id: ScriptId
    title: StrictStr
    url: StrictStr


@dataclass(frozen=True, slots=True)
class JxaBounds:
    x: StrictInt
    y: StrictInt
    width: StrictInt
    height: StrictInt


@dataclass(frozen=True, slots=True)
class JxaWindow:
    id: ScriptId
    #: Front-to-back order; the lowest is the front window.
    index: StrictInt
    title: StrictStr
    #: One-based index of the active tab.
    active: ScriptId
    minimized: StrictBool
    #: "normal" or "incognito".
    mode: StrictStr
    tabs: tuple[JxaTab, ...]
    bounds: Lenient[JxaBounds] = None

    @property
    def active_tab(self) -> JxaTab | None:
        return self.tabs[self.active - 1] if self.active <= len(self.tabs) else None


@dataclass(frozen=True, slots=True)
class Snapshot:
    #: Chrome's process ID; 0 when Chrome is not running.
    pid: StrictInt
    frontmost: StrictBool
    windows: tuple[JxaWindow, ...]

    def front_window(self) -> JxaWindow | None:
        """The one window at the front of Chrome's stacking order, or None when that is unclear.

        Chrome can hold hidden internal windows ahead of the browser window, so the
        front is the lowest index, which need not be 1."""
        first = min((window.index for window in self.windows), default=None)
        front = [window for window in self.windows if window.index == first]
        return front[0] if len(front) == 1 else None


@dataclass(frozen=True, slots=True)
class _Reply:
    status: StrictStr


@dataclass(frozen=True, slots=True)
class ProfileInfo:
    """One profile in Chrome's `Local State` `info_cache`; Chrome owns the format."""

    name: Lenient[StrictStr] = None
    gaia_name: Lenient[StrictStr] = None
    gaia_given_name: Lenient[StrictStr] = None
    user_name: Lenient[StrictStr] = None

    def aliases(self) -> set[str]:
        """The display name and account names this profile's avatar may show."""
        found = (self.name, self.gaia_name, self.gaia_given_name, self.user_name)
        return {alias for alias in found if alias}


@dataclass(frozen=True, slots=True)
class _ProfileSection:
    info_cache: dict[str, Lenient[ProfileInfo]] = field(default_factory=lambda: {})


@dataclass(frozen=True, slots=True)
class _LocalState:
    profile: Lenient[_ProfileSection] = None


def run_jxa(script: str) -> object:
    """Run a bounded native automation query; its JSON reply, still to be decoded."""
    try:
        result = subprocess.run(
            ["/usr/bin/osascript", "-l", "JavaScript", "-"],
            input=script,
            capture_output=True,
            text=True,
            timeout=30,
            check=True,
        )
        return load_json(result.stdout)
    except (OSError, subprocess.SubprocessError, ValueError) as exc:
        raise ChromeMacError("Chrome automation failed; check macOS Automation permission and retry") from exc


def _decode[T](kind: type[T], value: object, what: str) -> T:
    try:
        return decode(kind, value)
    except ValidationError as exc:
        raise ChromeMacError(f"Invalid Chrome {what}") from exc


def snapshot() -> Snapshot:
    return _decode(Snapshot, run_jxa(SNAPSHOT_SCRIPT), "snapshot")


@dataclass(frozen=True, slots=True)
class _Frontmost:
    pid: StrictInt


def frontmost_pid() -> int | None:
    """The frontmost application's process, from AppKit; None when there is none."""
    pid = _decode(_Frontmost, run_jxa(_FRONTMOST_SCRIPT), "frontmost application").pid
    return pid if pid > 0 else None


def status(reply: object) -> str:
    return _decode(_Reply, reply, "action result").status


@dataclass(frozen=True, slots=True)
class ActionIds:
    window: int
    tab: int


def action_ids(tab: ChromeTab) -> ActionIds:
    """Mutation identities must be actual positive integers representable in JXA."""
    if len(tab.runtime_id) != 1:
        raise ChromeMacError("Invalid Chrome tab identity")
    ids = ActionIds(tab.window_hwnd, tab.runtime_id[0])
    if not all(0 < value <= MAX_JXA_INTEGER for value in (ids.window, ids.tab)):
        raise ChromeMacError("Invalid Chrome action ID")
    return ids


def tab_action(tab: ChromeTab, action: str) -> str:
    ids = action_ids(tab)
    # Substitute numeric placeholders before the action: page content can contain
    # any placeholder token and must remain plain JSON string data.
    script = _TAB_ACTION_SCRIPT.replace("__WINDOW_ID__", str(ids.window)).replace("__TAB_ID__", str(ids.tab))
    return script.replace("__ACTION__", action)


def close_action(title: str, url: str) -> str:
    """Close the target only if it still shows this title and URL, passed as one JSON object."""
    expected = json.dumps({"title": title, "url": url}, ensure_ascii=True)
    return f"const expected = {expected};\n{_CLOSE_ACTION}"


def activate_script(hwnd: int) -> str:
    if not 0 < hwnd <= MAX_JXA_INTEGER:
        raise ChromeMacError("Invalid Chrome window identity")
    return ACTIVATE_SCRIPT.replace("__ID__", str(hwnd))


def restore_script(target: int, front: int, minimized: bool) -> str:
    """Put the front window and the target's minimised state back; both IDs come from a decoded snapshot."""
    state = json.dumps({"target": target, "front": front, "minimized": minimized})
    return _RESTORE_SCRIPT.replace("__STATE__", state)


def profiles(home: Path) -> dict[str, ProfileInfo]:
    """Chrome's profiles by directory, from `Local State`; empty when it is missing or unreadable."""
    try:
        text = (home / LOCAL_STATE).read_text(encoding="utf-8")
    except OSError:
        return {}
    state = try_decode_json(_LocalState, text)
    if state is None or state.profile is None:
        return {}
    return {directory: info for directory, info in state.profile.info_cache.items() if info is not None}
