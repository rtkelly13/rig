from __future__ import annotations

import time
from collections.abc import Callable

from desktop_manager import win32
from desktop_manager.geometry import rect_similarity
from desktop_manager.models import LaunchEntry, PlacementOutcome, PlacementSpec, WindowMatch
from desktop_manager.win32 import desktops, geometry, inventory
from desktop_manager.win32.errors import PYVDA_ERRORS
from desktop_manager.win32.geometry import placement_rect_for
from rig import tracing

_SCOPE = "desktop_manager.win32"


def choose_best_window_handle(
    handles: list[int], placement: PlacementSpec | None, *, target_desktop_number: int | None = None
) -> int:
    if not handles:
        raise ValueError("Expected at least one window handle")
    if placement is None and target_desktop_number is None:
        return handles[0]

    best_hwnd = handles[0]
    best_score = -1.0
    target_bounds = placement_rect_for(placement) if placement is not None else None
    for hwnd in handles:
        score = 0.0
        if target_desktop_number is not None and desktops.get_window_desktop_number(hwnd) == target_desktop_number:
            score += 2.0
        if target_bounds is not None:
            bounds = geometry.window_bounds(hwnd)
            if bounds is None:
                continue
            score += rect_similarity(bounds, target_bounds)
        if score > best_score:
            best_score = score
            best_hwnd = hwnd
    return best_hwnd


def wait_for_window_by_title(
    title: str,
    timeout_seconds: float = 10.0,
    *,
    exact: bool = False,
    current_desktop_only: bool = True,
    excluded_hwnds: set[int] | None = None,
    diagnostic_callback: Callable[[str], None] | None = None,
) -> int | None:
    win32.ensure_windows()
    deadline = time.monotonic() + timeout_seconds
    target = title.casefold()
    excluded = excluded_hwnds or set()
    if diagnostic_callback is not None:
        diagnostic_callback(
            f"Polling title target={title!r} exact={exact} "
            f"current_desktop_only={current_desktop_only} excluded={sorted(excluded)} "
            f"timeout={timeout_seconds:.2f}s"
        )

    while time.monotonic() < deadline:
        matches = inventory.collect_matching_windows(target, exact=exact, current_desktop_only=current_desktop_only)
        filtered = [hwnd for hwnd in matches if hwnd not in excluded]
        if filtered:
            if diagnostic_callback is not None:
                diagnostic_callback(f"Matched title target={title!r} hwnds={matches} filtered={filtered}")
            return filtered[0]
        if matches and diagnostic_callback is not None:
            diagnostic_callback(
                f"Title target={title!r} only matched excluded hwnds={matches}; waiting for fresh window"
            )
        time.sleep(0.15)

    if diagnostic_callback is not None:
        diagnostic_callback(f"Timed out waiting for title target={title!r}")
    return None


def wait_for_window_by_process(
    process_name: str,
    timeout_seconds: float = 10.0,
    *,
    target_placement: PlacementSpec | None = None,
    current_desktop_only: bool = True,
    target_desktop_number: int | None = None,
    excluded_hwnds: set[int] | None = None,
    single_instance: bool = False,
    alive_process_grace_seconds: float = 0.0,
    diagnostic_callback: Callable[[str], None] | None = None,
) -> int | None:
    win32.ensure_windows()
    target = process_name.casefold()
    deadline = time.monotonic() + timeout_seconds
    if diagnostic_callback is not None:
        diagnostic_callback(
            "Polling process "
            f"target={process_name!r} current_desktop_only={current_desktop_only} "
            f"target_desktop={target_desktop_number} excluded={sorted(excluded_hwnds or set())} "
            f"timeout={timeout_seconds:.2f}s grace={alive_process_grace_seconds:.2f}s"
        )

    extended = False
    while True:
        while time.monotonic() < deadline:
            matches = inventory.collect_windows_matching_process(target, current_desktop_only=current_desktop_only)
            if matches:
                original_matches = list(matches)
                if excluded_hwnds:
                    fresh_matches = [hwnd for hwnd in matches if hwnd not in excluded_hwnds]
                    if not fresh_matches:
                        if single_instance:
                            # A single-instance app (e.g. Slack, 1Password) re-focuses
                            # its existing window instead of spawning a new one, so a
                            # fresh hwnd never appears. Reuse the running window right
                            # away rather than polling until the timeout expires.
                            chosen = choose_best_window_handle(
                                original_matches,
                                target_placement,
                                target_desktop_number=target_desktop_number,
                            )
                            if diagnostic_callback is not None:
                                diagnostic_callback(
                                    f"Process target={process_name!r} is single-instance; reusing existing "
                                    f"window {chosen} from hwnds={original_matches}"
                                )
                            return chosen
                        if diagnostic_callback is not None:
                            diagnostic_callback(
                                f"Process target={process_name!r} only matched excluded "
                                f"hwnds={original_matches}; waiting for fresh window"
                            )
                        time.sleep(0.15)
                        continue
                    matches = fresh_matches
                chosen = choose_best_window_handle(
                    matches,
                    target_placement,
                    target_desktop_number=target_desktop_number,
                )
                if diagnostic_callback is not None:
                    diagnostic_callback(
                        f"Matched process target={process_name!r} hwnds={original_matches} "
                        f"filtered={matches} chosen={chosen}"
                    )
                return chosen
            time.sleep(0.15)

        # The base timeout is tuned for a warm machine. At logon the process
        # often starts within a second but takes far longer to paint its first
        # window while the disk is saturated, and giving up here makes the caller
        # relaunch -- spawning a duplicate instance that later appears wherever
        # it likes. Extend the wait once while the process is demonstrably alive.
        if extended or alive_process_grace_seconds <= 0 or not inventory.is_process_running(target):
            break
        extended = True
        deadline = time.monotonic() + alive_process_grace_seconds
        if diagnostic_callback is not None:
            diagnostic_callback(
                f"Process target={process_name!r} is running but has no window yet; "
                f"extending wait by {alive_process_grace_seconds:.2f}s"
            )

    if diagnostic_callback is not None:
        diagnostic_callback(f"Timed out waiting for process target={process_name!r}")
    return None


def wait_for_window_title_change(
    hwnd: int,
    original_title: str,
    timeout_seconds: float = 10.0,
    *,
    diagnostic_callback: Callable[[str], None] | None = None,
) -> str | None:
    win32.ensure_windows()
    deadline = time.monotonic() + timeout_seconds
    target = original_title.casefold()
    if diagnostic_callback is not None:
        diagnostic_callback(
            f"Waiting for hwnd={hwnd} title to change from {original_title!r} within {timeout_seconds:.2f}s"
        )

    while time.monotonic() < deadline:
        current_title = geometry.window_title(hwnd)
        if current_title and current_title.casefold() != target:
            if diagnostic_callback is not None:
                diagnostic_callback(f"Window hwnd={hwnd} changed title to {current_title!r}")
            return current_title
        time.sleep(0.15)

    if diagnostic_callback is not None:
        diagnostic_callback(f"Timed out waiting for hwnd={hwnd} title to change from {original_title!r}")
    return None


def _move_window_to_target_desktop(hwnd: int, target_desktop_number: int | None) -> bool:
    if target_desktop_number is None:
        return False

    current_desktop = desktops.get_window_desktop_number(hwnd)
    if current_desktop == target_desktop_number:
        return False

    try:
        desktops.move_window_to_desktop_number(hwnd, target_desktop_number)
    except PYVDA_ERRORS:
        return False
    time.sleep(0.15)
    return True


@tracing.traced("win32.apply_window_placement", scope=_SCOPE)
def apply_window_placement(
    entry: LaunchEntry,
    timeout_seconds: float = 10.0,
    *,
    hwnd: int | None = None,
    target_desktop_number: int | None = None,
    excluded_hwnds: set[int] | None = None,
    diagnostic_callback: Callable[[str], None] | None = None,
) -> PlacementOutcome:
    if not entry.placement:
        return PlacementOutcome(placed=False)

    if diagnostic_callback is not None:
        diagnostic_callback(
            f"Applying placement for {entry.name} target_desktop={target_desktop_number} "
            f"placement={entry.placement.to_dict()}"
        )

    moved_to_target_desktop = False
    if hwnd is None:
        match = wait_for_window_for_entry(
            entry,
            timeout_seconds=timeout_seconds,
            current_desktop_only=target_desktop_number is None,
            target_desktop_number=target_desktop_number,
            move_to_target_desktop=target_desktop_number is not None,
            excluded_hwnds=excluded_hwnds,
            diagnostic_callback=diagnostic_callback,
        )
        hwnd = match.hwnd
        moved_to_target_desktop = match.moved_to_target_desktop
    elif target_desktop_number is not None:
        moved_to_target_desktop = _move_window_to_target_desktop(hwnd, target_desktop_number)
        if diagnostic_callback is not None and moved_to_target_desktop:
            diagnostic_callback(f"Moved hwnd={hwnd} for {entry.name} to desktop {target_desktop_number}")
    if hwnd is None:
        raise RuntimeError(f"Could not find window for placement: {entry.name}")

    rect = placement_rect_for(entry.placement)
    if diagnostic_callback is not None:
        diagnostic_callback(f"Moving hwnd={hwnd} for {entry.name} to rect={rect.to_dict()}")
    geometry.move_window(hwnd, rect)
    return PlacementOutcome(placed=True, moved_to_target_desktop=moved_to_target_desktop)


def _matched(
    entry: LaunchEntry,
    hwnd: int,
    *,
    move_to_target_desktop: bool,
    target_desktop_number: int | None,
    diagnostic_callback: Callable[[str], None] | None,
) -> WindowMatch:
    moved = False
    if move_to_target_desktop:
        moved = _move_window_to_target_desktop(hwnd, target_desktop_number)
        if diagnostic_callback is not None and moved:
            diagnostic_callback(f"Moved hwnd={hwnd} for {entry.name} to desktop {target_desktop_number}")
    return WindowMatch(hwnd=hwnd, moved_to_target_desktop=moved)


@tracing.traced("win32.wait_for_window_for_entry", scope=_SCOPE)
def wait_for_window_for_entry(
    entry: LaunchEntry,
    timeout_seconds: float = 10.0,
    *,
    current_desktop_only: bool = True,
    target_desktop_number: int | None = None,
    move_to_target_desktop: bool = False,
    excluded_hwnds: set[int] | None = None,
    allow_process_fallback_for_terminals: bool = False,
    diagnostic_callback: Callable[[str], None] | None = None,
) -> WindowMatch:
    from desktop_manager.restore_timing import entry_window_wait_grace_seconds

    if diagnostic_callback is not None:
        diagnostic_callback(
            f"Waiting for entry {entry.name} title={entry.window_title!r} process={entry.window_process_name!r} "
            f"current_desktop_only={current_desktop_only} target_desktop={target_desktop_number} "
            f"move_to_target_desktop={move_to_target_desktop} "
            f"allow_terminal_process_fallback={allow_process_fallback_for_terminals}"
        )

    if entry.window_title:
        hwnd = wait_for_window_by_title(
            entry.window_title,
            timeout_seconds=timeout_seconds,
            exact=False,
            current_desktop_only=current_desktop_only,
            excluded_hwnds=excluded_hwnds,
            diagnostic_callback=diagnostic_callback,
        )
        if hwnd is not None:
            return _matched(
                entry,
                hwnd,
                move_to_target_desktop=move_to_target_desktop,
                target_desktop_number=target_desktop_number,
                diagnostic_callback=diagnostic_callback,
            )
        if entry.type == "terminal" and not allow_process_fallback_for_terminals:
            if diagnostic_callback is not None:
                diagnostic_callback(f"Terminal {entry.name} stopped after title polling without process fallback")
            return WindowMatch(hwnd=None)
        if entry.type == "terminal" and diagnostic_callback is not None:
            diagnostic_callback(
                f"Terminal {entry.name} title changed or was unavailable; falling back to process polling"
            )

    if entry.window_process_name:
        hwnd = wait_for_window_by_process(
            entry.window_process_name,
            timeout_seconds=timeout_seconds,
            target_placement=entry.placement,
            current_desktop_only=current_desktop_only,
            target_desktop_number=target_desktop_number,
            excluded_hwnds=excluded_hwnds,
            single_instance=entry.single_instance,
            alive_process_grace_seconds=entry_window_wait_grace_seconds(entry),
            diagnostic_callback=diagnostic_callback,
        )
        if hwnd is not None:
            return _matched(
                entry,
                hwnd,
                move_to_target_desktop=move_to_target_desktop,
                target_desktop_number=target_desktop_number,
                diagnostic_callback=diagnostic_callback,
            )

    title = entry.window_title or entry.name
    hwnd = wait_for_window_by_title(
        title,
        timeout_seconds=timeout_seconds,
        current_desktop_only=current_desktop_only,
        diagnostic_callback=diagnostic_callback,
    )
    if hwnd is not None:
        return _matched(
            entry,
            hwnd,
            move_to_target_desktop=move_to_target_desktop,
            target_desktop_number=target_desktop_number,
            diagnostic_callback=diagnostic_callback,
        )
    if diagnostic_callback is not None:
        diagnostic_callback(f"No window found for entry {entry.name}")
    return WindowMatch(hwnd=None)
