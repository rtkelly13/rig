"""Carry out a grouping plan: ungroups first, then window by window with each window in front."""

from __future__ import annotations

import time
from collections.abc import Callable, Generator, Sequence
from contextlib import contextmanager, suppress
from dataclasses import dataclass, replace
from typing import TYPE_CHECKING

from desktop_manager.chrome.grouping import GroupMove, GroupPlan, Ungroup
from desktop_manager.chrome.tabs import ChromeWindow
from rig.workspace.url_owners import UrlOwners

if TYPE_CHECKING:
    from desktop_manager.chrome.desktops import Desktops
    from desktop_manager.chrome.driver import ChromeDriver

from desktop_manager.chrome.cli_common import (
    keep_desktop,
    trace_span,
)


@dataclass(frozen=True, slots=True)
class GroupRun:
    """How far a grouping run got: tabs moved, and why it stopped (None when it finished)."""

    moved: int
    stopped: str | None = None


@dataclass(frozen=True, slots=True)
class _Brought:
    """A window brought onto this desktop, with the desktop to send it back to."""

    hwnd: int
    home: int


def plan_grouping(
    windows: Sequence[ChromeWindow], current: int | None, min_size: int, this_desktop: bool, *, phase: str = "plan"
) -> GroupPlan:
    """`plan_groups` over windows in screen order, with site hosts looked up in each profile's history."""
    from desktop_manager.chrome.grouping import key_of, plan_groups
    from desktop_manager.chrome.history import hosts_by_title

    with trace_span("plan_grouping", **{"group.phase": phase}) as span:
        unkeyed: dict[str, set[str]] = {}
        for window in windows:
            titles = unkeyed.setdefault(window.profile_key, set())
            # A tab with a URL names its own host; history is for titles alone.
            titles.update(tab.title for tab in window.tabs if tab.url is None and key_of(tab.title) is None)
        hosts = {profile: hosts_by_title(profile, titles) for profile, titles in unkeyed.items() if profile}
        plan = plan_groups(
            windows,
            current_desktop=current,
            min_size=min_size,
            all_desktops=not this_desktop,
            hosts=hosts,
            owners=_url_owners(),
        )
        span.set(
            **{
                "group.moves": len(plan.moves),
                "group.new_groups": sum(1 for move in plan.moves if move.new),
                "group.ungroups": len(plan.ungroups),
                "group.already_grouped": plan.already_grouped,
                "group.skipped_windows": len(plan.skipped),
            }
        )
        return plan


def run_group_plan(
    driver: ChromeDriver, plan: GroupPlan, *, interrupted: Callable[[], str | None] | None = None
) -> GroupRun:
    """Work through the plan: every ungroup first, then one window at a time (tabs moved,
    why it stopped). Windows on other desktops are brought here for the run.

    `interrupted` is asked before every step; a reason stops the run there."""
    with trace_span("group", **{"group.moves": len(plan.moves), "group.ungroups": len(plan.ungroups)}) as span:
        with keep_desktop(driver.desktops):
            ran = _run_group_steps(driver, plan, interrupted=interrupted)
        span.set(**{"group.moved": ran.moved, **({"group.stopped": ran.stopped} if ran.stopped else {})})
        return ran


def _run_group_steps(
    driver: ChromeDriver, plan: GroupPlan, *, interrupted: Callable[[], str | None] | None
) -> GroupRun:
    windows = {ungroup.tabs[0].window_hwnd for ungroup in plan.ungroups}
    windows |= {move.tab.window_hwnd for move in plan.moves} | {move.home_hwnd for move in plan.moves}
    with _on_this_desktop(driver.desktops, windows):
        for number in plan.ungroup_window_numbers():
            ungroups = [ungroup for ungroup in plan.ungroups if ungroup.window_number == number]
            error = _ungroup_window(driver, ungroups, interrupted=interrupted)
            if error is not None:
                return GroupRun(0, error)
        # A confirmed ungroup changes only these tabs' membership. Later steps
        # check each tab against its record, so the record must say ungrouped.
        ungrouped = {tab for ungroup in plan.ungroups for tab in ungroup.tabs}
        plan = replace(
            plan,
            moves=tuple(
                replace(move, tab=replace(move.tab, group=None)) if move.tab in ungrouped else move
                for move in plan.moves
            ),
        )
        moved = 0
        for number in plan.window_numbers():
            moves = [move for move in plan.moves if move.window_number == number]
            window = _group_window(driver, moves, interrupted=interrupted)
            moved += window.moved
            if window.stopped is not None:
                return GroupRun(moved, window.stopped)
    return GroupRun(moved)


def _desktop_note(desktops: Desktops, hwnd: int) -> str:
    from desktop_manager.chrome.presence import session_locked

    with suppress(Exception):
        if session_locked():
            return " (the session is locked)"
    with suppress(Exception):
        home, current = desktops.of_window(hwnd), desktops.current()
        if home is not None and home != current:
            return f" (it is on desktop {home}, this is desktop {current})"
    return ""


def _url_owners() -> UrlOwners:
    """The projects' declared local ports and Vercel names; none when the registry is not beside this checkout."""
    from rig.workspace.engine import config_file_path
    from rig.workspace.url_owners import load_url_owners

    try:
        return load_url_owners(config_file_path())
    except OSError:
        return UrlOwners()


@contextmanager
def _on_this_desktop(desktops: Desktops, hwnds: set[int]) -> Generator[None]:
    """Bring windows on other virtual desktops onto the current one, and send them back after.

    Clicking into a window on another desktop would switch desktops; moving the
    window here does not."""
    current = desktops.current()
    brought: list[_Brought] = []
    try:
        if current is not None:
            for hwnd in sorted(hwnds):
                home = desktops.of_window(hwnd)
                if home is not None and home != current:
                    with suppress(Exception):
                        desktops.move_window(hwnd, current)
                        brought.append(_Brought(hwnd, home))
            # The move is asynchronous; a window still on its way refuses the foreground.
            deadline = time.perf_counter() + 2.0
            while brought and time.perf_counter() < deadline:
                if all(desktops.of_window(each.hwnd) == current for each in brought):
                    break
                time.sleep(0.05)
        yield
    finally:
        for each in brought:
            if desktops.window_exists(each.hwnd):
                with suppress(Exception):
                    desktops.move_window(each.hwnd, each.home)


def _ungroup_window(
    driver: ChromeDriver, ungroups: Sequence[Ungroup], *, interrupted: Callable[[], str | None] | None = None
) -> str | None:
    attrs = {"window.number": ungroups[0].window_number, "group.ungroups": len(ungroups)}
    with trace_span("ungroup_window", **attrs) as span:
        error = _ungroup_window_steps(driver, ungroups, interrupted=interrupted)
        if error is not None:
            span.set(**{"group.stopped": error})
        return error


def _ungroup_window_steps(
    driver: ChromeDriver, ungroups: Sequence[Ungroup], *, interrupted: Callable[[], str | None] | None
) -> str | None:
    first = ungroups[0]
    if interrupted is not None and (reason := interrupted()) is not None:
        return reason
    with driver.foreground(first.tabs[0].window_hwnd) as arrived:
        if not arrived:
            note = _desktop_note(driver.desktops, first.tabs[0].window_hwnd)
            return f"window {first.window_number} would not come to the front{note}"
        for ungroup in ungroups:
            if interrupted is not None and (reason := interrupted()) is not None:
                return reason
            result = driver.ungroup(ungroup.tabs[0], title=ungroup.group.title)
            if not result.grouped:
                return f"ungrouping duplicate {ungroup.group.title} in window {ungroup.window_number}: {result.detail}"
    return None


def _group_window(
    driver: ChromeDriver, moves: Sequence[GroupMove], *, interrupted: Callable[[], str | None] | None = None
) -> GroupRun:
    """Carry out one window's moves with the window in front: (tabs moved, why it stopped).

    Stops at the first surprise, because the mouse is then no longer doing what the
    plan assumed."""
    attrs = {"window.number": moves[0].window_number, "window.profile": moves[0].profile, "group.moves": len(moves)}
    with trace_span("group_window", **attrs) as span:
        ran = _group_window_steps(driver, moves, interrupted=interrupted)
        span.set(**{"group.moved": ran.moved, **({"group.stopped": ran.stopped} if ran.stopped else {})})
        return ran


def _group_window_steps(
    driver: ChromeDriver, moves: Sequence[GroupMove], *, interrupted: Callable[[], str | None] | None
) -> GroupRun:
    first = moves[0]
    if interrupted is not None and (reason := interrupted()) is not None:
        return GroupRun(0, reason)
    with driver.foreground(first.tab.window_hwnd) as arrived:
        if not arrived:
            note = _desktop_note(driver.desktops, first.tab.window_hwnd)
            return GroupRun(0, f"window {first.window_number} would not come to the front{note}")
        for done, move in enumerate(moves):
            if interrupted is not None and (reason := interrupted()) is not None:
                return GroupRun(done, reason)
            where = f"at {move.title} tab {move.tab.index + 1} in window {move.window_number}"
            tab = move.tab
            if move.new and move.crosses:
                carried = driver.move_to_window(move.tab, move.home_hwnd)
                if carried.tab is None:
                    return GroupRun(done, f"{where}, moving it to window {move.home_number}: {carried.outcome.detail}")
                tab = carried.tab
            result = driver.group_tab(tab, title=move.title, new=move.new, home_hwnd=move.home_hwnd)
            if not result.grouped:
                return GroupRun(done, f"{where}: {result.detail}")
    return GroupRun(len(moves))
