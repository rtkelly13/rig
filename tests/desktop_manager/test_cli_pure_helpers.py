"""Unit tests for pure helper functions in desktop_manager.cli.

These tests cover console_safe_text and _filter_plan_to_screen, which are
platform-agnostic and can run on Linux in CI without Win32 dependencies.
"""

from __future__ import annotations

from desktop_manager.cli import _filter_plan_to_screen
from desktop_manager.cli_common import console_safe_text
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
