"""Unit tests for pure helper functions in desktop_manager.cli.

These tests cover console_safe_text, _wait_diff_sections,
wait_diff_for_console, and _filter_plan_to_screen — all of which are
platform-agnostic and can run on Linux in CI without Win32 dependencies.
"""

from __future__ import annotations

from desktop_manager.cli import _filter_plan_to_screen
from desktop_manager.cli_common import _DiffSection, _wait_diff_sections, console_safe_text, wait_diff_for_console
from desktop_manager.models import DesktopPlan, LaunchEntry, LiveWindow, PlacementLayout, PlacementSpec, WorkspacePlan

# ---------------------------------------------------------------------------
# console_safe_text
# ---------------------------------------------------------------------------


class TestConsoleSafeText:
    def test_ascii_passes_through_unchanged(self) -> None:
        assert console_safe_text("Hello World") == "Hello World"

    def test_empty_string_returns_empty(self) -> None:
        assert console_safe_text("") == ""

    def test_cp1252_chars_pass_through(self) -> None:
        # Smart quotes (U+2018/U+2019) are valid cp1252 (0x91/0x92)
        result = console_safe_text("\u2018smart\u2019")
        assert result == "\u2018smart\u2019"

    def test_non_cp1252_chars_replaced(self) -> None:
        # Verify snowman character is replaced in cp1252 encoding
        result = console_safe_text("Hello \u2603")
        assert result == "Hello ?"

    def test_mixed_cp1252_and_non_encodable(self) -> None:
        result = console_safe_text("caf\xe9 \u2603")
        assert result == "caf\xe9 ?"


# ---------------------------------------------------------------------------
# _wait_diff_sections
# ---------------------------------------------------------------------------


class TestWaitDiffSections:
    def test_empty_string_returns_empty_list(self) -> None:
        assert _wait_diff_sections("") == []

    def test_workspace_diff_header_is_skipped(self) -> None:
        assert _wait_diff_sections("Workspace Diff") == []

    def test_single_desktop_single_screen_with_lines(self) -> None:
        diff = "Work\nScreen 1\n  - entry1\n  - entry2"
        sections = _wait_diff_sections(diff)
        assert sections == [_DiffSection("Work", "Screen 1", ("  - entry1", "  - entry2"))]

    def test_multiple_desktops_multiple_screens(self) -> None:
        diff = "Work\nScreen 1\n  - app1\nAdmin\nScreen 2\n  - app2"
        sections = _wait_diff_sections(diff)
        assert len(sections) == 2
        assert sections[0] == _DiffSection("Work", "Screen 1", ("  - app1",))
        assert sections[1] == _DiffSection("Admin", "Screen 2", ("  - app2",))

    def test_desktop_with_multiple_screens(self) -> None:
        diff = "Work\nScreen 1\n  - app1\nScreen 2\n  - app2"
        sections = _wait_diff_sections(diff)
        assert len(sections) == 2
        assert sections[0] == _DiffSection("Work", "Screen 1", ("  - app1",))
        assert sections[1] == _DiffSection("Work", "Screen 2", ("  - app2",))

    def test_lines_before_first_screen_are_dropped(self) -> None:
        # Lines belonging to a desktop but before any Screen header are dropped
        diff = "Work\n  - orphan\nScreen 1\n  - entry1"
        sections = _wait_diff_sections(diff)
        assert sections == [_DiffSection("Work", "Screen 1", ("  - entry1",))]

    def test_empty_lines_are_not_included_in_content(self) -> None:
        diff = "Work\nScreen 1\n  - entry1\n\n  - entry2"
        sections = _wait_diff_sections(diff)
        # Empty line is skipped, not included in tuple
        assert "  - entry1" in sections[0].lines
        assert "" not in sections[0].lines

    def test_workspace_diff_header_inline_is_skipped(self) -> None:
        diff = "Workspace Diff\nWork\nScreen 1\n  - entry1"
        sections = _wait_diff_sections(diff)
        assert sections == [_DiffSection("Work", "Screen 1", ("  - entry1",))]

    def test_trailing_whitespace_stripped_from_lines(self) -> None:
        diff = "Work\nScreen 1\n  - entry1   "
        sections = _wait_diff_sections(diff)
        assert sections[0].lines == ("  - entry1",)


# ---------------------------------------------------------------------------
# wait_diff_for_console
# ---------------------------------------------------------------------------


class TestWaitDiffForConsole:
    def test_no_previous_diff_returns_diff_unchanged(self) -> None:
        diff = "Work\nScreen 1\n  - app1"
        assert wait_diff_for_console(diff, None) == diff

    def test_previous_diff_same_returns_original_diff(self) -> None:
        diff = "Work\nScreen 1\n  - app1"
        result = wait_diff_for_console(diff, diff)
        assert result == diff

    def test_all_sections_changed_returns_all(self) -> None:
        old = "Work\nScreen 1\n  - app1"
        new = "Work\nScreen 1\n  - app2"
        result = wait_diff_for_console(new, old)
        assert "Work" in result
        assert "Screen 1" in result
        assert "  - app2" in result

    def test_partial_change_returns_only_changed_sections(self) -> None:
        old = "Work\nScreen 1\n  - app1\nAdmin\nScreen 2\n  - admin1"
        new = "Work\nScreen 1\n  - app1\nAdmin\nScreen 2\n  - admin2"
        result = wait_diff_for_console(new, old)
        # Only the Admin/Screen 2 section changed
        assert "Admin" in result
        assert "  - admin2" in result
        # Work/Screen 1 did NOT change — should not appear in the filtered output
        assert "Screen 1" not in result
        assert "app1" not in result

    def test_no_changes_returns_original_diff_unchanged(self) -> None:
        diff = "Work\nScreen 1\n  - app1"
        # Same content → no changed sections → returns original diff
        result = wait_diff_for_console(diff, diff)
        assert result == diff

    def test_empty_previous_diff_returns_new_diff_sections(self) -> None:
        new = "Work\nScreen 1\n  - app1"
        result = wait_diff_for_console(new, "")
        # No sections in previous → all are "changed"
        assert "Work" in result
        assert "  - app1" in result


# ---------------------------------------------------------------------------
# _filter_plan_to_screen
# ---------------------------------------------------------------------------


def _make_entry(name: str, screen: int) -> LaunchEntry:
    return LaunchEntry(
        name=name,
        type="app",
        path=rf"C:\{name}.exe",
        placement=PlacementSpec(screen=screen, layout=PlacementLayout(mode="full", slot=1)),
    )


def _make_window(title: str, screen: int) -> LiveWindow:
    return LiveWindow(hwnd=hash(title) & 0xFFFF, title=title, screen_number=screen)


class TestFilterPlanToScreen:
    def test_filters_entries_to_matching_screen(self) -> None:
        entry_s1 = _make_entry("App1", screen=1)
        entry_s2 = _make_entry("App2", screen=2)
        plan = WorkspacePlan(desktops=[DesktopPlan(name="Work", launch=[entry_s1, entry_s2])])

        filtered = _filter_plan_to_screen(plan, desktop=None, screen=1)

        assert len(filtered.desktops) == 1
        assert len(filtered.desktops[0].launch) == 1
        assert filtered.desktops[0].launch[0].name == "App1"

    def test_filters_captured_windows_to_matching_screen(self) -> None:
        win_s1 = _make_window("Win1", screen=1)
        win_s2 = _make_window("Win2", screen=2)
        plan = WorkspacePlan(desktops=[DesktopPlan(name="Work", captured_windows=[win_s1, win_s2])])

        filtered = _filter_plan_to_screen(plan, desktop=None, screen=2)

        assert len(filtered.desktops[0].captured_windows) == 1
        assert filtered.desktops[0].captured_windows[0].title == "Win2"

    def test_desktop_none_includes_all_desktops(self) -> None:
        entry1 = _make_entry("App1", screen=1)
        entry2 = _make_entry("App2", screen=1)
        plan = WorkspacePlan(
            desktops=[
                DesktopPlan(name="Work", launch=[entry1]),
                DesktopPlan(name="Admin", launch=[entry2]),
            ]
        )

        filtered = _filter_plan_to_screen(plan, desktop=None, screen=1)

        assert len(filtered.desktops) == 2

    def test_desktop_filter_excludes_other_desktops(self) -> None:
        entry1 = _make_entry("App1", screen=1)
        entry2 = _make_entry("App2", screen=1)
        plan = WorkspacePlan(
            desktops=[
                DesktopPlan(name="Work", launch=[entry1]),
                DesktopPlan(name="Admin", launch=[entry2]),
            ]
        )

        filtered = _filter_plan_to_screen(plan, desktop="Work", screen=1)

        assert len(filtered.desktops) == 1
        assert filtered.desktops[0].name == "Work"

    def test_preserves_plan_metadata(self) -> None:
        plan = WorkspacePlan(
            version="2",
            workspace="MyWorkspace",
            desktops=[DesktopPlan(name="Work")],
        )

        filtered = _filter_plan_to_screen(plan, desktop=None, screen=1)

        assert filtered.version == "2"
        assert filtered.workspace == "MyWorkspace"

    def test_no_matching_entries_returns_empty_launch(self) -> None:
        entry = _make_entry("App2", screen=2)
        plan = WorkspacePlan(desktops=[DesktopPlan(name="Work", launch=[entry])])

        filtered = _filter_plan_to_screen(plan, desktop=None, screen=1)

        assert filtered.desktops[0].launch == []

    def test_entry_without_placement_is_excluded(self) -> None:
        entry_no_placement = LaunchEntry(name="NoPlacement", type="app", path=r"C:\App.exe")
        plan = WorkspacePlan(desktops=[DesktopPlan(name="Work", launch=[entry_no_placement])])

        filtered = _filter_plan_to_screen(plan, desktop=None, screen=1)

        assert filtered.desktops[0].launch == []
