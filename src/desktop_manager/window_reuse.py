"""Matching live windows to plan entries, moving them into place, and confirming they landed."""

from __future__ import annotations

import time
from collections.abc import Sequence
from dataclasses import dataclass
from pathlib import Path
from urllib.parse import urlparse

from desktop_manager.models import (
    LaunchEntry,
    LiveWindow,
    PlacementSpec,
    RestoreRunLogger,
    WindowSnapshot,
    WorkspacePlan,
)
from desktop_manager.placeholders import substitute_placeholders
from desktop_manager.placement import (
    normalized_placement_rect,
    placement_rect_for,
    rect_similarity,
)
from desktop_manager.window_pid_file import pid_verified, window_owner_allowed


@dataclass(frozen=True, slots=True)
class ReuseClaim:
    """The live window claimed for an entry (if any) and the windows still unclaimed."""

    window: LiveWindow | None
    remaining: list[LiveWindow]


@dataclass(frozen=True, slots=True)
class WindowMove:
    moved_to_desktop: bool
    moved_to_placement: bool


def match_reusable_window(
    entry: LaunchEntry,
    windows: list[LiveWindow],
    substitutions: dict[str, str],
    *,
    target_desktop_number: int | None = None,
    competing_titles: frozenset[str] = frozenset(),
) -> LiveWindow | None:
    filtered_windows = windows
    if target_desktop_number is not None:
        filtered_windows = [
            window
            for window in windows
            if window.desktop_number is None
            or window.desktop_number == target_desktop_number
            or pid_verified(entry, window.pid, substitutions)
        ]

    best_window: LiveWindow | None = None
    best_score = 0
    for window in filtered_windows:
        score = reusable_window_match_score(
            entry,
            window,
            substitutions,
            target_desktop_number=target_desktop_number,
            competing_titles=competing_titles,
        )
        if score > best_score:
            best_score = score
            best_window = window
    return best_window


def claim_reusable_window(
    entry: LaunchEntry,
    windows: list[LiveWindow],
    substitutions: dict[str, str],
    *,
    target_desktop_number: int | None = None,
    competing_titles: frozenset[str] = frozenset(),
) -> ReuseClaim:
    matched_window = match_reusable_window(
        entry,
        windows,
        substitutions,
        target_desktop_number=target_desktop_number,
        competing_titles=competing_titles,
    )
    if matched_window is None:
        return ReuseClaim(None, list(windows))
    remaining_windows = [window for window in windows if window.hwnd != matched_window.hwnd]
    return ReuseClaim(matched_window, remaining_windows)


def reusable_window_match_score(
    entry: LaunchEntry,
    window: LiveWindow,
    substitutions: dict[str, str],
    *,
    target_desktop_number: int | None = None,
    competing_titles: frozenset[str] = frozenset(),
) -> int:
    score = window_identity_match_score(entry, window.title, window.process_name, substitutions, competing_titles)
    if score <= 0:
        return 0
    if not window_owner_allowed(entry, window.pid, substitutions):
        return 0

    if (
        target_desktop_number is not None
        and window.desktop_number is not None
        and window.desktop_number != target_desktop_number
        and not pid_verified(entry, window.pid, substitutions)
    ):
        return 0

    on_target_desktop = target_desktop_number is not None and window.desktop_number == target_desktop_number

    # Terminal entries with a configured window_title are uniquely identified by
    # that title.  When the running process changes the title (e.g. OpenCode
    # replaces "Personal Terminal 1" with its own banner), the identity score
    # drops to a process-name-only match (100).  That weak signal is not enough
    # to distinguish one WindowsTerminal.exe from another, so we use location
    # as a tiebreaker.  When the window is confirmed on a *different* desktop
    # we heavily penalise the score so that same-desktop and unknown-desktop
    # candidates are preferred, but the match is still kept as a last resort
    # so the restore can reposition an existing session rather than launching a
    # duplicate.
    has_title_match = _identity_score_includes_title(entry, window.title)
    if entry.type == "terminal" and entry.window_title and not has_title_match and window.desktop_number is not None:
        score = max(score, 1)

    if on_target_desktop:
        score += 120

    if (
        entry.placement is not None
        and window.screen_number is not None
        and entry.placement.screen == window.screen_number
    ):
        score += 80

    if entry.placement is not None and window.inferred_placement is not None:
        if entry.placement.to_dict() == window.inferred_placement.to_dict():
            score += 140
        elif normalized_placement_rect(entry.placement) == normalized_placement_rect(window.inferred_placement):
            score += 90
    elif entry.placement is not None and window.bounds is not None:
        target_bounds = placement_rect_for(entry.placement)
        score += int(rect_similarity(window.bounds, target_bounds) * 60)

    if entry.type == "terminal" and entry.placement is not None:
        if window.screen_number == entry.placement.screen:
            score += 80
        if window.inferred_placement is not None and window.inferred_placement.to_dict() == entry.placement.to_dict():
            score += 180
        elif window.bounds is not None:
            target_bounds = placement_rect_for(entry.placement)
            score += int(rect_similarity(window.bounds, target_bounds) * 140)

    return score


def _identity_score_includes_title(entry: LaunchEntry, window_title: str) -> bool:
    """Check whether the entry's configured title matches the window title.

    Returns ``True`` when an exact or partial title match exists — i.e. the
    identity score includes a title component, not just a process-name match.
    """
    target_title = (entry.window_title or "").strip().casefold()
    if not target_title:
        return False
    normalized_window_title = window_title.casefold()
    return normalized_window_title == target_title or target_title in normalized_window_title


def move_existing_window_to_entry(window: LiveWindow, desktop_index: int, entry: LaunchEntry) -> WindowMove:
    from desktop_manager.win32.desktops import (
        get_window_desktop_number,
        move_window_to_desktop_number,
    )
    from desktop_manager.win32.geometry import move_window

    current_desktop = get_window_desktop_number(window.hwnd)
    moved_to_desktop = False
    if current_desktop != desktop_index:
        move_window_to_desktop_number(window.hwnd, desktop_index)
        moved_to_desktop = True
    moved_to_placement = False
    if entry.placement:
        already_matches = False
        if window.inferred_placement is not None:
            already_matches = window.inferred_placement.to_dict() == entry.placement.to_dict()
        elif window.bounds is not None:
            target_bounds = placement_rect_for(entry.placement)
            already_matches = rect_similarity(window.bounds, target_bounds) >= 0.92

        if not already_matches and not window_matches_placement(window.hwnd, entry.placement):
            move_window(window.hwnd, placement_rect_for(entry.placement))
            moved_to_placement = True
    return WindowMove(moved_to_desktop, moved_to_placement)


def window_matches_placement(hwnd: int, placement: PlacementSpec, *, minimum_similarity: float = 0.92) -> bool:
    from desktop_manager.win32.geometry import window_bounds

    current_bounds = window_bounds(hwnd)
    if current_bounds is None:
        return False
    target_bounds = placement_rect_for(placement)
    return rect_similarity(current_bounds, target_bounds) >= minimum_similarity


def apply_second_pass_placements(
    plan: WorkspacePlan,
    logger: RestoreRunLogger | None = None,
    *,
    entries_by_desktop: dict[int, list[LaunchEntry]] | None = None,
) -> list[str]:
    from desktop_manager.win32 import ensure_windows
    from desktop_manager.win32.desktops import (
        ensure_current_desktop_number,
        move_right,
        switch_leftmost,
    )
    from desktop_manager.win32.entry_windows import (
        apply_window_placement,
        wait_for_window_by_process,
        wait_for_window_by_title,
    )

    actions: list[str] = []
    ensure_windows()
    switch_leftmost(len(plan.desktops) + 2)
    time.sleep(plan.switch_delay_ms / 1000)
    ensure_current_desktop_number(1, settle_seconds=plan.switch_delay_ms / 1000)

    for desktop_index, desktop in enumerate(plan.desktops, start=1):
        if desktop_index > 1:
            move_right(1)
            time.sleep(plan.switch_delay_ms / 1000)
        confirmed = ensure_current_desktop_number(desktop_index, settle_seconds=plan.switch_delay_ms / 1000)
        if logger is not None:
            logger.log(
                f"Desktop {desktop_index} second-pass activation {'confirmed' if confirmed else 'best-effort only'}"
            )
        desktop_entries = (
            entries_by_desktop.get(desktop_index, []) if entries_by_desktop is not None else desktop.launch
        )
        if not desktop_entries:
            continue
        actions.append(f"Desktop {desktop_index}: second-pass placement")
        for entry in desktop_entries:
            if entry.disabled:
                continue
            if entry.placement is None:
                continue
            if entry.type == "terminal":
                if logger is not None:
                    logger.log(f"Skipped second-pass placement for terminal {entry.name}")
                continue
            try:
                existing_hwnd = None
                if entry.window_title:
                    existing_hwnd = wait_for_window_by_title(
                        entry.window_title,
                        timeout_seconds=0.2,
                        current_desktop_only=False,
                    )
                if existing_hwnd is None and entry.window_process_name:
                    existing_hwnd = wait_for_window_by_process(
                        entry.window_process_name,
                        timeout_seconds=0.2,
                        current_desktop_only=False,
                        target_desktop_number=desktop_index,
                    )
                if existing_hwnd is not None and window_matches_placement(existing_hwnd, entry.placement):
                    continue
                outcome = apply_window_placement(
                    entry,
                    timeout_seconds=1.0,
                    target_desktop_number=desktop_index,
                )
                if logger is not None and outcome.moved_to_target_desktop:
                    logger.log(
                        f"Moved launched window for {entry.name} onto desktop {desktop_index} during second pass"
                    )
            except Exception as err:
                if logger is not None:
                    logger.exception(f"FAILED second-pass placement {entry.name}: {err}")
                continue
            if outcome.placed:
                actions.append(f"  - reposition `{entry.name}`")
                if logger is not None:
                    logger.log(f"Second-pass placement applied to {entry.name}")

    switch_leftmost(len(plan.desktops) + 2)
    time.sleep(plan.switch_delay_ms / 1000)
    ensure_current_desktop_number(1, settle_seconds=plan.switch_delay_ms / 1000)
    return actions


def is_entry_target_confirmed(entry: LaunchEntry, desktop_index: int, *, timeout_seconds: float = 0.35) -> bool:
    from desktop_manager.win32.entry_windows import wait_for_window_for_entry

    hwnd = wait_for_window_for_entry(
        entry,
        timeout_seconds=timeout_seconds,
        current_desktop_only=False,
        target_desktop_number=desktop_index,
    ).hwnd
    if hwnd is None:
        return False
    return is_window_target_confirmed(hwnd, desktop_index, entry)


def is_window_target_confirmed(hwnd: int, desktop_index: int, entry: LaunchEntry) -> bool:
    from desktop_manager.win32.desktops import get_window_desktop_number

    current_desktop = get_window_desktop_number(hwnd)
    if current_desktop is not None and current_desktop != desktop_index:
        return False
    if entry.placement is None:
        return True
    return window_matches_placement(hwnd, entry.placement)


def describe_launch(entry: LaunchEntry, substitutions: dict[str, str]) -> str:
    if entry.type == "terminal":
        cwd = substitute_placeholders(entry.cwd, substitutions)
        command = substitute_placeholders(entry.command, substitutions) or ""
        return f"terminal `{entry.name}` -> {command} (cwd: {cwd})"
    if entry.type == "app":
        path = substitute_placeholders(entry.path, substitutions) or "<missing path>"
        args = " ".join(filter(None, (substitute_placeholders(arg, substitutions) for arg in entry.args)))
        return f"app `{entry.name}` -> {path}{(' ' + args) if args else ''}"
    command = substitute_placeholders(entry.command, substitutions) or "<missing command>"
    cwd = substitute_placeholders(entry.cwd, substitutions)
    if cwd:
        return f"command `{entry.name}` -> {command} (cwd: {cwd})"
    return f"command `{entry.name}` -> {command}"


def captured_window_match_score(
    entry: LaunchEntry,
    window: WindowSnapshot,
    substitutions: dict[str, str],
    competing_titles: frozenset[str] = frozenset(),
) -> int:
    score = window_identity_match_score(entry, window.title, window.process_name, substitutions, competing_titles)
    if score <= 0:
        return 0
    if not window_owner_allowed(entry, window.pid, substitutions):
        return 0

    if (
        entry.placement is not None
        and window.screen_number is not None
        and entry.placement.screen == window.screen_number
    ):
        score += 80

    if entry.placement is not None and window.inferred_placement is not None:
        if entry.placement.to_dict() == window.inferred_placement.to_dict():
            score += 120
        elif normalized_placement_rect(entry.placement) == normalized_placement_rect(window.inferred_placement):
            score += 80

    return score


def competing_sibling_titles(entries: Sequence[LaunchEntry], current: LaunchEntry) -> frozenset[str]:
    """Casefolded window_titles of sibling entries, excluding the current entry's own title.

    Used to stop a title-less (or differently-titled) entry from claiming a window that
    plainly belongs to a sibling by title -- e.g. a VS Code "Workspace" entry grabbing a
    "prs-to-review.md" window purely on a Code.exe process-name match.

    Only siblings that could actually own the window count. A sibling running a
    different process never can, and treating it as competition disqualifies
    legitimate matches: on the Personal desktop the Warp entry's title
    "shared-utilities" appears in the VS Code window title
    "...design.md - shared-utilities - Visual Studio Code", which made the
    title-less `VS Code (shared-utilities)` entry score 0 against its own window
    and report as missing while sitting on screen 3.
    """
    own_title = (current.window_title or "").strip().casefold()
    own_process = (current.window_process_name or "").strip().casefold()
    result: set[str] = set()
    for other in entries:
        if other is current:
            continue
        other_process = (other.window_process_name or "").strip().casefold()
        # Fall back to treating the sibling as competition when either process is
        # unknown -- the guard should stay conservative when it cannot tell.
        if own_process and other_process and own_process != other_process:
            continue
        title = (other.window_title or "").strip().casefold()
        if title and title != own_title:
            result.add(title)
    return frozenset(result)


def window_identity_match_score(
    entry: LaunchEntry,
    window_title_value: str,
    window_process_name: str | None,
    substitutions: dict[str, str],
    competing_titles: frozenset[str] = frozenset(),
) -> int:
    score = 0
    target_title = (entry.window_title or "").strip().casefold()
    window_title = window_title_value.casefold()

    declared_process = (entry.window_process_name or "").strip().casefold()
    if declared_process and window_process_name and declared_process != window_process_name.casefold():
        # An explicit `window_process_name` is the author asserting which process owns
        # this window, so a mismatch disqualifies outright rather than merely losing
        # points. Title evidence alone used to win: a title substring scores 150 while
        # the process match below is only worth 100, so `Personal Warp`
        # (window_title "shared-utilities", warp.exe) claimed the VS Code window titled
        # "...- shared-utilities - Visual Studio Code" and left the real
        # `VS Code (shared-utilities)` entry to launch a duplicate.
        #
        # Only the *declared* name gates. `expected_process_name` otherwise falls back
        # to the launch path's basename, which is a guess -- a launcher that hands off
        # to a differently-named process would be disqualified wrongly -- so that
        # fallback stays advisory. A window whose own process is unknown also stays
        # eligible; the guard should not punish a window it cannot identify.
        return 0

    if competing_titles and window_title:
        matches_own = bool(target_title) and target_title in window_title
        matches_competing = any(sibling in window_title for sibling in competing_titles)
        if matches_competing and not matches_own:
            return 0

    chrome_hint_score = 0
    if target_title:
        if window_title == target_title:
            score += 200
        elif target_title in window_title:
            score += 150

    if _is_chrome_entry(entry, substitutions):
        chrome_hint_score = _chrome_title_hint_score(entry, window_title, substitutions)
        score += chrome_hint_score

    process_target = expected_process_name(entry, substitutions)
    process_matched = bool(process_target and window_process_name and process_target == window_process_name.casefold())
    if process_matched:
        score += 100

    score += _terminal_command_hint_score(entry, window_title)

    entry_name = entry.name.casefold()
    if (
        entry_name
        and entry_name in window_title
        and not (window_process_name and window_process_name.casefold() == "chrome.exe")
    ):
        score += 25

    if _chrome_entry_requires_title_hint(entry, substitutions) and chrome_hint_score <= 0 and not target_title:
        # The launch URL is the only strong identity signal for a Chrome entry, but
        # titles drift as the window gets used: the Gmail window opened for
        # `Admin Browser` reads "New Tab" once the tab is closed. Returning 0 there
        # reports the window as missing even though it is sitting in its planned
        # slot, and the next restore attempt launches a duplicate. Fall back to a
        # deliberately weak score so placement and screen bonuses decide it; any
        # sibling whose URL hint does match still scores 170+ and outranks this.
        return 1 if process_matched else 0

    return score


def _terminal_command_hint_score(entry: LaunchEntry, window_title: str) -> int:
    if entry.type != "terminal" or not entry.command:
        return 0

    command = entry.command.casefold()
    if "copilot" in command:
        return 180 if "copilot" in window_title else 0
    if "claude" in command:
        if "claude" in window_title:
            return 180
        if window_title.startswith("⠐ "):
            return 160
        return 0
    if "opencode" in command:
        if "opencode" in window_title:
            return 180
        if window_title.startswith("oc |"):
            return 160
    return 0


def expected_process_name(entry: LaunchEntry, substitutions: dict[str, str]) -> str | None:
    if entry.window_process_name:
        return entry.window_process_name.casefold()
    resolved_path = substitute_placeholders(entry.path, substitutions)
    if resolved_path:
        return Path(resolved_path).name.casefold()
    return None


def _is_chrome_entry(entry: LaunchEntry, substitutions: dict[str, str]) -> bool:
    process_name = expected_process_name(entry, substitutions)
    if process_name == "chrome.exe":
        return True
    resolved_path = substitute_placeholders(entry.path, substitutions)
    return bool(resolved_path and resolved_path.casefold().endswith("chrome.exe"))


def _chrome_entry_requires_title_hint(entry: LaunchEntry, substitutions: dict[str, str]) -> bool:
    return bool(_chrome_title_hints(entry, substitutions))


def _chrome_title_hint_score(entry: LaunchEntry, window_title: str, substitutions: dict[str, str]) -> int:
    best_score = 0
    for hint in _chrome_title_hints(entry, substitutions):
        normalized = hint.casefold()
        if not normalized:
            continue
        if window_title == normalized:
            best_score = max(best_score, 220)
        elif normalized in window_title:
            best_score = max(best_score, 170)
    return best_score


def _chrome_title_hints(entry: LaunchEntry, substitutions: dict[str, str]) -> list[str]:
    hints: list[str] = []
    for raw_arg in entry.args:
        resolved_arg = substitute_placeholders(raw_arg, substitutions)
        if not resolved_arg or not resolved_arg.startswith(("http://", "https://")):
            continue
        hints.extend(_chrome_hints_for_url(resolved_arg))
    return list(dict.fromkeys(hints))


def _chrome_hints_for_url(url: str) -> list[str]:
    parsed = urlparse(url)
    host = parsed.netloc.casefold()
    path = parsed.path.casefold()
    if "meet.google.com" in host:
        return ["google meet", "meet"]
    if "calendar.google.com" in host:
        return ["google calendar", "calendar"]
    if "mail.google.com" in host or host == "gmail.com":
        return ["gmail", "mail"]
    if "docs.google.com" in host:
        return ["google docs", "docs"]
    if host:
        labels = [part for part in host.split(".") if part not in {"www", "com"}]
        hints = [" ".join(labels[:2]).strip(), labels[0]] if labels else []
        if path and path != "/":
            hints.append(path.strip("/").split("/")[0])
        return [hint for hint in hints if hint]
    return []
