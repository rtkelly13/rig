"""Text diff between a workspace plan and the captured live workspace."""

from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path

from desktop_manager.models import (
    DesktopPlan,
    LaunchEntry,
    WindowSnapshot,
    WorkspacePlan,
)
from desktop_manager.window_reuse import captured_window_match_score, competing_sibling_titles


def render_workspace_diff(plan: WorkspacePlan, actual_plan: WorkspacePlan) -> str:
    lines = ["Workspace Diff"]
    for index, planned_desktop in enumerate(plan.desktops):
        actual_desktop = (
            actual_plan.desktops[index]
            if index < len(actual_plan.desktops)
            else DesktopPlan(name=f"Desktop {index + 1}")
        )
        lines.append("")
        lines.append(f"{planned_desktop.name}")
        matches = _match_desktop_entries_to_windows(planned_desktop, actual_desktop, plan.substitutions())
        planned_by_screen = _planned_names_by_screen(planned_desktop)
        actual_by_screen = _actual_names_by_screen(actual_desktop)
        all_screens = sorted(set(planned_by_screen) | set(actual_by_screen))
        for screen_number in all_screens:
            lines.append(f"Screen {screen_number}")
            planned_names = planned_by_screen.get(screen_number, [])
            actual_names = actual_by_screen.get(screen_number, [])
            screen_entry_indexes = [
                entry_index
                for entry_index, entry in enumerate(planned_desktop.launch)
                if not entry.disabled and entry.placement is not None and entry.placement.screen == screen_number
            ]
            screen_window_indexes = [
                window_index
                for window_index, window in enumerate(actual_desktop.captured_windows)
                if window.screen_number == screen_number
            ]
            matched = [
                _format_entry_window_match(
                    planned_desktop.launch[entry_index], actual_desktop.captured_windows[window_index]
                )
                for entry_index in screen_entry_indexes
                for window_index in [matches.by_entry.get(entry_index)]
                if window_index is not None
                and actual_desktop.captured_windows[window_index].screen_number == screen_number
            ]
            misplaced = [
                _format_screen_mismatch(
                    planned_desktop.launch[entry_index], actual_desktop.captured_windows[window_index], screen_number
                )
                for entry_index in screen_entry_indexes
                for window_index in [matches.by_entry.get(entry_index)]
                if window_index is not None
                and actual_desktop.captured_windows[window_index].screen_number != screen_number
            ]
            missing = [
                planned_desktop.launch[entry_index].name
                for entry_index in screen_entry_indexes
                if entry_index not in matches.by_entry
            ]
            borrowed: list[str] = []
            for window_index in screen_window_indexes:
                if window_index not in matches.by_window:
                    continue
                matched_entry = planned_desktop.launch[matches.by_window[window_index]]
                if matched_entry.placement is None or matched_entry.placement.screen == screen_number:
                    continue
                borrowed.append(_format_borrowed_window(actual_desktop.captured_windows[window_index], matched_entry))
            extra = [
                _actual_window_label(actual_desktop.captured_windows[window_index])
                for window_index in screen_window_indexes
                if window_index not in matches.by_window
            ]
            lines.append(f"  planned: {', '.join(planned_names) or '-'}")
            lines.append(f"  actual:  {', '.join(actual_names) or '-'}")
            if matched:
                lines.append(f"  matched: {', '.join(matched)}")
            if misplaced:
                lines.append(f"  misplaced: {', '.join(misplaced)}")
            if borrowed:
                lines.append(f"  borrowed: {', '.join(borrowed)}")
            if not missing and not extra and not misplaced and not borrowed:
                lines.append("  status:  ok")
            else:
                if missing:
                    lines.append(f"  missing: {', '.join(missing)}")
                if extra:
                    lines.append(f"  extra:   {', '.join(extra)}")
    return "\n".join(lines)


def truncate_text(value: str, limit: int) -> str:
    if len(value) <= limit:
        return value
    return value[: limit - 3] + "..."


def _planned_names_by_screen(desktop: DesktopPlan) -> dict[int, list[str]]:
    result: dict[int, list[str]] = {}
    for entry in desktop.launch:
        if entry.disabled or entry.placement is None:
            continue
        result.setdefault(entry.placement.screen, []).append(entry.name)
    return result


def _actual_names_by_screen(desktop: DesktopPlan) -> dict[int, list[str]]:
    result: dict[int, list[str]] = {}
    for window in desktop.captured_windows:
        if window.screen_number is None:
            continue
        result.setdefault(window.screen_number, []).append(_actual_window_label(window))
    return result


def _actual_window_label(window: WindowSnapshot) -> str:
    process_label = Path(window.process_name).stem if window.process_name else None
    pid_label = f" pid={window.pid}" if window.pid is not None else ""
    if window.title and process_label:
        return truncate_text(f"{window.title} [{process_label}{pid_label}]", 64)
    if window.title:
        return truncate_text(f"{window.title}{pid_label}", 64)
    if process_label:
        return f"{process_label}{pid_label}"
    return "<unknown window>"


@dataclass(frozen=True, slots=True, order=True)
class _ScoredMatch:
    score: int
    entry_index: int
    window_index: int


@dataclass(frozen=True, slots=True)
class _EntryWindowMatches:
    by_entry: dict[int, int]
    by_window: dict[int, int]


def _match_desktop_entries_to_windows(
    planned_desktop: DesktopPlan,
    actual_desktop: DesktopPlan,
    substitutions: dict[str, str],
) -> _EntryWindowMatches:
    scored_matches: list[_ScoredMatch] = []
    for entry_index, entry in enumerate(planned_desktop.launch):
        if entry.disabled:
            continue
        competing_titles = competing_sibling_titles(planned_desktop.launch, entry)
        for window_index, window in enumerate(actual_desktop.captured_windows):
            score = captured_window_match_score(entry, window, substitutions, competing_titles)
            if score > 0:
                scored_matches.append(_ScoredMatch(score, entry_index, window_index))

    scored_matches.sort(reverse=True)
    matches = _EntryWindowMatches({}, {})
    for scored in scored_matches:
        if scored.entry_index in matches.by_entry or scored.window_index in matches.by_window:
            continue
        matches.by_entry[scored.entry_index] = scored.window_index
        matches.by_window[scored.window_index] = scored.entry_index
    return matches


def entries_matched_on_planned_screen(
    planned_desktop: DesktopPlan,
    actual_desktop: DesktopPlan,
    substitutions: dict[str, str],
) -> set[str]:
    """Names of planned entries already matched to a window on their planned screen.

    This is the structured form of the `status: ok` / `matched:` lines the diff
    renders, so a retry can skip what is already correct instead of relaunching
    every entry on the desktop. Entries whose match sits on the wrong screen are
    excluded -- those are the `misplaced:` cases a retry can still fix.
    """
    matches_by_entry = _match_desktop_entries_to_windows(planned_desktop, actual_desktop, substitutions).by_entry
    satisfied: set[str] = set()
    for entry_index, window_index in matches_by_entry.items():
        entry = planned_desktop.launch[entry_index]
        if entry.placement is None:
            continue
        window = actual_desktop.captured_windows[window_index]
        if window.screen_number == entry.placement.screen:
            satisfied.add(entry.name)
    return satisfied


def _format_entry_window_match(entry: LaunchEntry, window: WindowSnapshot) -> str:
    return f"{entry.name} <- {_actual_window_label(window)}"


def _format_screen_mismatch(entry: LaunchEntry, window: WindowSnapshot, planned_screen: int) -> str:
    actual_screen = window.screen_number if window.screen_number is not None else "?"
    return f"{entry.name} -> {_actual_window_label(window)} on screen {actual_screen} (planned {planned_screen})"


def _format_borrowed_window(window: WindowSnapshot, entry: LaunchEntry) -> str:
    planned_screen = entry.placement.screen if entry.placement is not None else "?"
    return f"{_actual_window_label(window)} -> {entry.name} on screen {planned_screen}"
