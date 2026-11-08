"""Text diff between a workspace plan and the captured live workspace."""

from __future__ import annotations

from collections.abc import Iterator
from dataclasses import dataclass
from pathlib import Path

from desktop_manager.models import (
    DesktopPlan,
    LaunchEntry,
    WindowSnapshot,
    WorkspacePlan,
)
from desktop_manager.window_reuse import captured_window_match_score, competing_sibling_titles


@dataclass(frozen=True, slots=True)
class ScreenDiff:
    """One screen of one desktop: what the plan puts there, what is there, and how they pair up.

    ``missing`` and ``misplaced`` are planned entries another restore attempt can fix.
    ``extra`` (a window no entry claims) and ``borrowed`` (a window matched to an entry
    planned for another screen) are not: a retry cannot close a stray window.
    """

    screen_number: int
    planned: tuple[str, ...] = ()
    actual: tuple[str, ...] = ()
    matched: tuple[str, ...] = ()
    misplaced: tuple[str, ...] = ()
    borrowed: tuple[str, ...] = ()
    missing: tuple[str, ...] = ()
    extra: tuple[str, ...] = ()

    @property
    def is_ok(self) -> bool:
        return not (self.missing or self.extra or self.misplaced or self.borrowed)

    @property
    def has_fixable_drift(self) -> bool:
        return bool(self.missing or self.misplaced)

    def render_lines(self) -> list[str]:
        lines = [
            f"Screen {self.screen_number}",
            f"  planned: {', '.join(self.planned) or '-'}",
            f"  actual:  {', '.join(self.actual) or '-'}",
        ]
        if self.matched:
            lines.append(f"  matched: {', '.join(self.matched)}")
        if self.misplaced:
            lines.append(f"  misplaced: {', '.join(self.misplaced)}")
        if self.borrowed:
            lines.append(f"  borrowed: {', '.join(self.borrowed)}")
        if self.is_ok:
            lines.append("  status:  ok")
        else:
            if self.missing:
                lines.append(f"  missing: {', '.join(self.missing)}")
            if self.extra:
                lines.append(f"  extra:   {', '.join(self.extra)}")
        return lines


@dataclass(frozen=True, slots=True)
class DesktopDiff:
    """One planned desktop, screen by screen."""

    name: str
    screens: tuple[ScreenDiff, ...] = ()


@dataclass(frozen=True, slots=True)
class WorkspaceDiff:
    """The plan against the live workspace. Decisions read the fields; people read :meth:`render`."""

    desktops: tuple[DesktopDiff, ...] = ()

    def screens(self) -> Iterator[ScreenDiff]:
        for desktop in self.desktops:
            yield from desktop.screens

    @property
    def is_clean(self) -> bool:
        """Nothing missing, misplaced, borrowed or extra anywhere."""
        return all(screen.is_ok for screen in self.screens())

    @property
    def has_fixable_drift(self) -> bool:
        """Some planned entry is missing or on the wrong screen, which another attempt can fix."""
        return any(screen.has_fixable_drift for screen in self.screens())

    def changed_since(self, previous: WorkspaceDiff | None) -> WorkspaceDiff:
        """Only the screens that differ from ``previous``; the whole diff when there is none or nothing changed."""
        if previous is None:
            return self
        before = {
            (desktop.name, screen.screen_number): screen for desktop in previous.desktops for screen in desktop.screens
        }
        changed = tuple(
            DesktopDiff(desktop.name, screens)
            for desktop in self.desktops
            if (screens := tuple(s for s in desktop.screens if before.get((desktop.name, s.screen_number)) != s))
        )
        return WorkspaceDiff(changed) if changed else self

    def render(self) -> str:
        lines = ["Workspace Diff"]
        for desktop in self.desktops:
            lines.append("")
            lines.append(desktop.name)
            for screen in desktop.screens:
                lines.extend(screen.render_lines())
        return "\n".join(lines)


def render_workspace_diff(plan: WorkspacePlan, actual_plan: WorkspacePlan) -> str:
    return diff_workspace(plan, actual_plan).render()


def diff_workspace(plan: WorkspacePlan, actual_plan: WorkspacePlan) -> WorkspaceDiff:
    desktops: list[DesktopDiff] = []
    for index, planned_desktop in enumerate(plan.desktops):
        actual_desktop = (
            actual_plan.desktops[index]
            if index < len(actual_plan.desktops)
            else DesktopPlan(name=f"Desktop {index + 1}")
        )
        matches = _match_desktop_entries_to_windows(planned_desktop, actual_desktop, plan.substitutions())
        planned_by_screen = _planned_names_by_screen(planned_desktop)
        actual_by_screen = _actual_names_by_screen(actual_desktop)
        all_screens = sorted(set(planned_by_screen) | set(actual_by_screen))
        screens: list[ScreenDiff] = []
        for screen_number in all_screens:
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
            screens.append(
                ScreenDiff(
                    screen_number,
                    planned=tuple(planned_names),
                    actual=tuple(actual_names),
                    matched=tuple(matched),
                    misplaced=tuple(misplaced),
                    borrowed=tuple(borrowed),
                    missing=tuple(missing),
                    extra=tuple(extra),
                )
            )
        desktops.append(DesktopDiff(planned_desktop.name, tuple(screens)))
    return WorkspaceDiff(tuple(desktops))


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
