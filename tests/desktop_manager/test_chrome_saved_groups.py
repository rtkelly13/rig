from __future__ import annotations

import pytest

from desktop_manager.chrome.render import render_saved_groups
from desktop_manager.chrome.saved_groups import (
    ChipName,
    OpenDuplicate,
    SavedGroup,
    parse_chip_name,
    plan_consolidation,
    union_of_bars,
)


def bar(*chips: str, profile: str = "Default") -> list[SavedGroup]:
    """Chips as `+title` (open) or `-title` (closed), left to right."""
    return [SavedGroup(profile, chip[1:], chip[0] == "+", position) for position, chip in enumerate(chips)]


class TestParseChipName:
    @pytest.mark.parametrize(
        ("name", "expected"),
        [
            (" Google group – Opened", ChipName("Google", True)),
            (" data-platform group – Closed", ChipName("data-platform", False)),
            ("Release group 2 group – Closed", ChipName("Release group 2", False)),
        ],
    )
    def test_reads_title_and_state(self, name: str, expected: ChipName) -> None:
        assert parse_chip_name(name) == expected

    @pytest.mark.parametrize("name", ["Tab groups", "Google", " Google group - Opened", ""])
    def test_ignores_anything_else_on_the_bar(self, name: str) -> None:
        assert parse_chip_name(name) is None


class TestUnionOfBars:
    def test_takes_the_window_showing_the_most_chips(self) -> None:
        narrow = bar("+Google", "-Claude")
        wide = bar("+Google", "-Claude", "-infrastructure")
        assert union_of_bars([narrow, wide]) == wide

    def test_no_windows_means_no_groups(self) -> None:
        assert union_of_bars([]) == []


class TestPlanConsolidation:
    def test_deletes_every_closed_copy_of_an_open_title(self) -> None:
        plan = plan_consolidation(bar("+Google", "-Google", "-Google"))
        assert [(d.title, d.count, d.open_copies, d.closed_copies) for d in plan.deletes] == [("Google", 2, 1, 2)]
        assert plan.total == 2

    def test_keeps_one_closed_copy_when_none_is_open(self) -> None:
        plan = plan_consolidation(bar("-YouTrack", "-YouTrack", "-YouTrack"))
        assert [(d.title, d.count) for d in plan.deletes] == [("YouTrack", 2)]

    def test_leaves_titles_with_a_single_copy(self) -> None:
        plan = plan_consolidation(bar("+Google", "-playbook", "-shared-utilities"))
        assert plan.deletes == ()
        assert plan.total == 0

    def test_never_deletes_an_open_copy_and_reports_open_duplicates(self) -> None:
        plan = plan_consolidation(bar("+Claude", "+Claude"))
        assert plan.deletes == ()
        assert plan.open_duplicates == (OpenDuplicate("Default", "Claude", 2),)

    def test_titles_are_matched_per_profile(self) -> None:
        plan = plan_consolidation([*bar("+Google"), *bar("-Google", profile="Profile 4")])
        assert plan.deletes == ()

    def test_the_live_session(self) -> None:
        plan = plan_consolidation(
            bar(
                "+Google",
                "+Claude",
                "+data-platform",
                "-Claude",
                "-data-platform",
                "-Google",
                "-YouTrack",
                "-playbook",
                "-shared-utilities",
            )
        )
        assert sorted((d.title, d.count) for d in plan.deletes) == [("Claude", 1), ("Google", 1), ("data-platform", 1)]


def test_render_lists_the_bar_then_the_deletes() -> None:
    lines = render_saved_groups(plan_consolidation(bar("+Google", "-Google", "-YouTrack", "-YouTrack")))
    assert lines == [
        "Saved groups [Default] (4)",
        "    1  open    Google",
        "    2  closed  Google",
        "    3  closed  YouTrack",
        "    4  closed  YouTrack",
        "Delete: closed duplicates (2)",
        "  1 x Google                   [Default] of 2 saved, keeping the open copy",
        "  1 x YouTrack                 [Default] of 2 saved, keeping the leftmost closed copy",
        "2 saved groups to delete, 4 seen",
    ]
