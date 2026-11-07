"""Unit tests for the pure functions in placement.py and geometry.py.

These tests cover functions that have no win32 or OS dependencies and
run correctly on any platform (Linux CI, macOS, Windows).
"""

from __future__ import annotations

from fractions import Fraction

import pytest

from desktop_manager.geometry import (
    UnitRect,
    _candidate_layouts,
    _candidate_subdivide_layouts,
    _fractional_horizontal_fraction,
    _fractional_horizontal_two_thirds,
    _fractional_quarter_rect,
    _fractional_vertical_fraction,
    _horizontal_fraction,
    _horizontal_two_thirds,
    _quarter_rect,
    _rect_from_layout,
    _vertical_fraction,
    columns_for_width,
    coverage_summary,
    infer_placement,
    normalized_placement_rect,
    placement_rect_in,
    rect_similarity,
)
from desktop_manager.models import (
    DesktopPlan,
    DisplayArea,
    LaunchEntry,
    PlacementLayout,
    PlacementSpec,
    Rect,
    WorkspacePlan,
)
from desktop_manager.placement import (
    _extract_local_path_arg,
    _format_fraction_percent,
    _looks_like_obsidian_vault_arg,
    fit_columns,
    format_placement,
    placement_spec,
    resolve_screen_count,
    validate_placement_spec,
    validate_plan_structure,
)

# ---------------------------------------------------------------------------
# placement_spec factory
# ---------------------------------------------------------------------------


class TestPlacementSpec:
    def test_simple_full(self) -> None:
        spec = placement_spec(screen=1, mode="full")
        assert spec.screen == 1
        assert spec.layout.mode == "full"
        assert spec.layout.slot == 1
        assert spec.subdivide is None

    def test_halves_with_orientation(self) -> None:
        spec = placement_spec(screen=2, mode="halves", slot=1, orientation="vertical")
        assert spec.layout.orientation == "vertical"
        assert spec.screen == 2

    def test_with_subdivide(self) -> None:
        spec = placement_spec(
            screen=1,
            mode="halves",
            slot=1,
            orientation="vertical",
            subdivide_mode="halves",
            subdivide_slot=2,
            subdivide_orientation="horizontal",
        )
        assert spec.subdivide is not None
        assert spec.subdivide.mode == "halves"
        assert spec.subdivide.slot == 2
        assert spec.subdivide.orientation == "horizontal"

    def test_no_subdivide_when_subdivide_mode_none(self) -> None:
        spec = placement_spec(screen=1, mode="full")
        assert spec.subdivide is None


# ---------------------------------------------------------------------------
# validate_placement_spec
# ---------------------------------------------------------------------------


class TestValidatePlacementSpec:
    def test_valid_full_spec(self) -> None:
        spec = PlacementSpec(screen=1, layout=PlacementLayout(mode="full", slot=1))
        errors = validate_placement_spec(spec, "Desktop", "Entry")
        assert errors == []

    def test_screen_zero_is_invalid(self) -> None:
        spec = PlacementSpec(screen=0, layout=PlacementLayout(mode="full", slot=1))
        errors = validate_placement_spec(spec, "Desktop", "Entry")
        assert any("screen must be >= 1" in e for e in errors)

    def test_screen_exceeds_monitor_count(self) -> None:
        spec = PlacementSpec(screen=3, layout=PlacementLayout(mode="full", slot=1))
        errors = validate_placement_spec(spec, "Desk", "Entry", monitor_count=2)
        assert any("exceeds detected monitor count" in e for e in errors)

    def test_invalid_mode(self) -> None:
        spec = PlacementSpec(screen=1, layout=PlacementLayout(mode="bogus", slot=1))
        errors = validate_placement_spec(spec, "Desk", "Entry")
        assert any("invalid layout mode" in e for e in errors)

    def test_halves_invalid_slot(self) -> None:
        spec = PlacementSpec(screen=1, layout=PlacementLayout(mode="halves", slot=3, orientation="vertical"))
        errors = validate_placement_spec(spec, "Desk", "Entry")
        assert any("halves slot must be 1 or 2" in e for e in errors)

    def test_halves_missing_orientation(self) -> None:
        spec = PlacementSpec(screen=1, layout=PlacementLayout(mode="halves", slot=1, orientation=None))
        errors = validate_placement_spec(spec, "Desk", "Entry")
        assert any("halves orientation must be vertical or horizontal" in e for e in errors)

    def test_thirds_invalid_slot(self) -> None:
        spec = PlacementSpec(screen=1, layout=PlacementLayout(mode="thirds", slot=4))
        errors = validate_placement_spec(spec, "Desk", "Entry")
        assert any("thirds slot must be 1, 2, or 3" in e for e in errors)

    def test_quarters_invalid_slot(self) -> None:
        spec = PlacementSpec(screen=1, layout=PlacementLayout(mode="quarters", slot=5))
        errors = validate_placement_spec(spec, "Desk", "Entry")
        assert any("quarters slot must be 1, 2, 3, or 4" in e for e in errors)

    def test_two_thirds_invalid_slot(self) -> None:
        spec = PlacementSpec(screen=1, layout=PlacementLayout(mode="two-thirds", slot=3))
        errors = validate_placement_spec(spec, "Desk", "Entry")
        assert any("two-thirds slot must be 1 or 2" in e for e in errors)

    def test_valid_thirds_all_slots(self) -> None:
        for slot in (1, 2, 3):
            spec = PlacementSpec(screen=1, layout=PlacementLayout(mode="thirds", slot=slot))
            errors = validate_placement_spec(spec, "D", "E")
            assert errors == [], f"slot {slot} should be valid"

    def test_valid_quarters_all_slots(self) -> None:
        for slot in (1, 2, 3, 4):
            spec = PlacementSpec(screen=1, layout=PlacementLayout(mode="quarters", slot=slot))
            errors = validate_placement_spec(spec, "D", "E")
            assert errors == [], f"slot {slot} should be valid"


# ---------------------------------------------------------------------------
# normalized_placement_rect
# ---------------------------------------------------------------------------


class TestNormalizedPlacementRect:
    def test_full_covers_entire_screen(self) -> None:
        spec = PlacementSpec(screen=1, layout=PlacementLayout(mode="full", slot=1))
        rect = normalized_placement_rect(spec)
        assert (rect.left, rect.top, rect.width, rect.height) == (Fraction(0), Fraction(0), Fraction(1), Fraction(1))

    def test_halves_vertical_slot1(self) -> None:
        spec = PlacementSpec(screen=1, layout=PlacementLayout(mode="halves", slot=1, orientation="vertical"))
        rect = normalized_placement_rect(spec)
        assert rect.width == Fraction(1, 2)
        assert rect.left == Fraction(0)

    def test_halves_vertical_slot2(self) -> None:
        spec = PlacementSpec(screen=1, layout=PlacementLayout(mode="halves", slot=2, orientation="vertical"))
        rect = normalized_placement_rect(spec)
        assert rect.left == Fraction(1, 2)
        assert rect.width == Fraction(1, 2)

    def test_thirds_slot1(self) -> None:
        spec = PlacementSpec(screen=1, layout=PlacementLayout(mode="thirds", slot=1))
        rect = normalized_placement_rect(spec)
        assert rect.width == Fraction(1, 3)
        assert rect.left == Fraction(0)

    def test_thirds_slots_non_overlapping(self) -> None:
        rects = [
            normalized_placement_rect(PlacementSpec(screen=1, layout=PlacementLayout(mode="thirds", slot=s)))
            for s in (1, 2, 3)
        ]
        coverage = coverage_summary(rects)
        assert coverage.overlap == Fraction(0)

    def test_quarters_four_rects_cover_screen(self) -> None:
        rects = [
            normalized_placement_rect(PlacementSpec(screen=1, layout=PlacementLayout(mode="quarters", slot=s)))
            for s in (1, 2, 3, 4)
        ]
        coverage = coverage_summary(rects)
        assert coverage.empty == Fraction(0)
        assert coverage.overlap == Fraction(0)

    def test_subdivide_narrows_rect(self) -> None:
        spec = PlacementSpec(
            screen=1,
            layout=PlacementLayout(mode="halves", slot=1, orientation="vertical"),
            subdivide=PlacementLayout(mode="halves", slot=2, orientation="vertical"),
        )
        rect = normalized_placement_rect(spec)
        assert rect.width == Fraction(1, 4)
        assert rect.left == Fraction(1, 4)


# ---------------------------------------------------------------------------
# coverage_summary
# ---------------------------------------------------------------------------


class TestCoverageSummary:
    def test_single_full_rect_no_empty_no_overlap(self) -> None:
        rects = [UnitRect(Fraction(0), Fraction(0), Fraction(1), Fraction(1))]
        coverage = coverage_summary(rects)
        assert coverage.empty == Fraction(0)
        assert coverage.overlap == Fraction(0)

    def test_two_non_overlapping_halves(self) -> None:
        rects = [
            UnitRect(Fraction(0), Fraction(0), Fraction(1, 2), Fraction(1)),
            UnitRect(Fraction(1, 2), Fraction(0), Fraction(1, 2), Fraction(1)),
        ]
        coverage = coverage_summary(rects)
        assert coverage.empty == Fraction(0)
        assert coverage.overlap == Fraction(0)

    def test_gap_between_thirds_slot1_and_slot3(self) -> None:
        rects = [
            UnitRect(Fraction(0), Fraction(0), Fraction(1, 3), Fraction(1)),
            UnitRect(Fraction(2, 3), Fraction(0), Fraction(1, 3), Fraction(1)),
        ]
        coverage = coverage_summary(rects)
        assert coverage.empty == Fraction(1, 3)
        assert coverage.overlap == Fraction(0)

    def test_two_overlapping_full_rects(self) -> None:
        rects = [
            UnitRect(Fraction(0), Fraction(0), Fraction(1), Fraction(1)),
            UnitRect(Fraction(0), Fraction(0), Fraction(1), Fraction(1)),
        ]
        coverage = coverage_summary(rects)
        assert coverage.empty == Fraction(0)
        assert coverage.overlap == Fraction(1)


# ---------------------------------------------------------------------------
# _format_fraction_percent
# ---------------------------------------------------------------------------


class TestFormatFractionPercent:
    def test_half(self) -> None:
        assert _format_fraction_percent(Fraction(1, 2)) == "50.0%"

    def test_third(self) -> None:
        result = _format_fraction_percent(Fraction(1, 3))
        assert "33.3" in result

    def test_full(self) -> None:
        assert _format_fraction_percent(Fraction(1)) == "100.0%"

    def test_zero(self) -> None:
        assert _format_fraction_percent(Fraction(0)) == "0.0%"


# ---------------------------------------------------------------------------
# Fractional helpers (Fraction-based)
# ---------------------------------------------------------------------------


class TestFractionalHorizontalFraction:
    def test_slot1_of_2(self) -> None:
        span = _fractional_horizontal_fraction(Fraction(0), Fraction(1), 2, 1)
        assert span.start == Fraction(0)
        assert span.length == Fraction(1, 2)

    def test_slot2_of_2(self) -> None:
        span = _fractional_horizontal_fraction(Fraction(0), Fraction(1), 2, 2)
        assert span.start == Fraction(1, 2)
        assert span.length == Fraction(1, 2)

    def test_slot3_of_3(self) -> None:
        span = _fractional_horizontal_fraction(Fraction(0), Fraction(1), 3, 3)
        assert span.start == Fraction(2, 3)
        assert span.length == Fraction(1, 3)

    def test_out_of_range_raises(self) -> None:
        with pytest.raises(ValueError, match="out of range"):
            _fractional_horizontal_fraction(Fraction(0), Fraction(1), 2, 3)


class TestFractionalVerticalFraction:
    def test_slot1_of_2(self) -> None:
        span = _fractional_vertical_fraction(Fraction(0), Fraction(1), 2, 1)
        assert span.start == Fraction(0)
        assert span.length == Fraction(1, 2)

    def test_slot2_of_2(self) -> None:
        span = _fractional_vertical_fraction(Fraction(0), Fraction(1), 2, 2)
        assert span.start == Fraction(1, 2)
        assert span.length == Fraction(1, 2)

    def test_out_of_range_raises(self) -> None:
        with pytest.raises(ValueError, match="out of range"):
            _fractional_vertical_fraction(Fraction(0), Fraction(1), 2, 0)


class TestFractionalHorizontalTwoThirds:
    def test_slot1_is_two_thirds_wide_from_left(self) -> None:
        span = _fractional_horizontal_two_thirds(Fraction(0), Fraction(1), 1)
        assert span.start == Fraction(0)
        assert span.length == Fraction(2, 3)

    def test_slot2_starts_at_one_third(self) -> None:
        span = _fractional_horizontal_two_thirds(Fraction(0), Fraction(1), 2)
        assert span.start == Fraction(1, 3)
        assert span.length == Fraction(2, 3)

    def test_invalid_slot_raises(self) -> None:
        with pytest.raises(ValueError, match="out of range"):
            _fractional_horizontal_two_thirds(Fraction(0), Fraction(1), 3)


class TestFractionalQuarterRect:
    def test_slot1_top_left(self) -> None:
        rect = _fractional_quarter_rect(UnitRect(Fraction(0), Fraction(0), Fraction(1), Fraction(1)), 1)
        assert (rect.left, rect.width) == (Fraction(0), Fraction(1, 2))
        assert (rect.top, rect.height) == (Fraction(0), Fraction(1, 2))

    def test_slot2_top_right(self) -> None:
        rect = _fractional_quarter_rect(UnitRect(Fraction(0), Fraction(0), Fraction(1), Fraction(1)), 2)
        assert rect.left == Fraction(1, 2)
        assert rect.top == Fraction(0)

    def test_slot3_bottom_right(self) -> None:
        rect = _fractional_quarter_rect(UnitRect(Fraction(0), Fraction(0), Fraction(1), Fraction(1)), 3)
        assert rect.left == Fraction(1, 2)
        assert rect.top == Fraction(1, 2)

    def test_slot4_bottom_left(self) -> None:
        rect = _fractional_quarter_rect(UnitRect(Fraction(0), Fraction(0), Fraction(1), Fraction(1)), 4)
        assert rect.left == Fraction(0)
        assert rect.top == Fraction(1, 2)

    def test_invalid_slot_raises(self) -> None:
        with pytest.raises(ValueError, match="out of range"):
            _fractional_quarter_rect(UnitRect(Fraction(0), Fraction(0), Fraction(1), Fraction(1)), 5)


# ---------------------------------------------------------------------------
# Integer-based pixel helpers
# ---------------------------------------------------------------------------


class TestHorizontalFraction:
    def test_slot1_of_2_even_width(self) -> None:
        span = _horizontal_fraction(0, 1000, 2, 1)
        assert span.start == 0
        assert span.length == 500

    def test_slot2_of_2_even_width(self) -> None:
        span = _horizontal_fraction(0, 1000, 2, 2)
        assert span.start == 500
        assert span.length == 500

    def test_last_slot_absorbs_remainder(self) -> None:
        # 1001 / 3 = 333 remainder 2; last slot gets remainder
        span = _horizontal_fraction(0, 1001, 3, 3)
        assert span.start + span.length == 1001

    def test_left_offset_applied(self) -> None:
        span = _horizontal_fraction(100, 600, 2, 1)
        assert span.start == 100

    def test_out_of_range_raises(self) -> None:
        with pytest.raises(ValueError):
            _horizontal_fraction(0, 1000, 2, 3)


class TestVerticalFraction:
    def test_slot1_of_2(self) -> None:
        span = _vertical_fraction(0, 800, 2, 1)
        assert span.start == 0
        assert span.length == 400

    def test_slot2_of_2(self) -> None:
        span = _vertical_fraction(0, 800, 2, 2)
        assert span.start == 400
        assert span.length == 400

    def test_out_of_range_raises(self) -> None:
        with pytest.raises(ValueError):
            _vertical_fraction(0, 800, 2, 0)


class TestHorizontalTwoThirds:
    def test_slot1_left_two_thirds(self) -> None:
        span = _horizontal_two_thirds(0, 900, 1)
        assert span.start == 0
        assert span.length == 600

    def test_slot2_right_two_thirds(self) -> None:
        span = _horizontal_two_thirds(0, 900, 2)
        assert span.start == 300
        assert span.length == 600

    def test_slot2_absorbs_remainder(self) -> None:
        span = _horizontal_two_thirds(0, 1001, 2)
        assert span.start + span.length == 1001

    def test_invalid_slot_raises(self) -> None:
        with pytest.raises(ValueError):
            _horizontal_two_thirds(0, 900, 3)


class TestQuarterRect:
    def test_slot1_top_left(self) -> None:
        rect = _quarter_rect(Rect(left=0, top=0, width=1000, height=800), 1)
        assert rect.left == 0
        assert rect.top == 0

    def test_slot2_top_right(self) -> None:
        rect = _quarter_rect(Rect(left=0, top=0, width=1000, height=800), 2)
        assert rect.left == 500
        assert rect.top == 0

    def test_slot3_bottom_right(self) -> None:
        rect = _quarter_rect(Rect(left=0, top=0, width=1000, height=800), 3)
        assert rect.left == 500
        assert rect.top == 400

    def test_slot4_bottom_left(self) -> None:
        rect = _quarter_rect(Rect(left=0, top=0, width=1000, height=800), 4)
        assert rect.left == 0
        assert rect.top == 400

    def test_invalid_slot_raises(self) -> None:
        with pytest.raises(ValueError):
            _quarter_rect(Rect(left=0, top=0, width=1000, height=800), 0)


# ---------------------------------------------------------------------------
# rect_similarity
# ---------------------------------------------------------------------------


class TestRectSimilarity:
    def test_identical_rects_give_1(self) -> None:
        r = Rect(left=0, top=0, width=100, height=100)
        assert rect_similarity(r, r) == 1.0

    def test_non_overlapping_rects_give_0(self) -> None:
        a = Rect(left=0, top=0, width=100, height=100)
        b = Rect(left=200, top=0, width=100, height=100)
        assert rect_similarity(a, b) == 0.0

    def test_half_overlap(self) -> None:
        a = Rect(left=0, top=0, width=100, height=100)
        b = Rect(left=50, top=0, width=100, height=100)
        score = rect_similarity(a, b)
        assert 0 < score < 1

    def test_score_symmetric(self) -> None:
        a = Rect(left=0, top=0, width=100, height=200)
        b = Rect(left=20, top=10, width=80, height=150)
        assert rect_similarity(a, b) == pytest.approx(rect_similarity(b, a))

    def test_zero_area_rects_give_0(self) -> None:
        a = Rect(left=0, top=0, width=0, height=100)
        b = Rect(left=0, top=0, width=0, height=100)
        assert rect_similarity(a, b) == 0.0


# ---------------------------------------------------------------------------
# format_placement
# ---------------------------------------------------------------------------


class TestFormatPlacement:
    def test_full_no_subdivide(self) -> None:
        spec = PlacementSpec(screen=1, layout=PlacementLayout(mode="full", slot=1))
        assert format_placement(spec) == "screen 1: full#1"

    def test_halves_with_orientation(self) -> None:
        spec = PlacementSpec(screen=2, layout=PlacementLayout(mode="halves", slot=1, orientation="vertical"))
        result = format_placement(spec)
        assert "screen 2" in result
        assert "vertical" in result

    def test_with_subdivide(self) -> None:
        spec = PlacementSpec(
            screen=1,
            layout=PlacementLayout(mode="halves", slot=1, orientation="vertical"),
            subdivide=PlacementLayout(mode="halves", slot=2, orientation="horizontal"),
        )
        result = format_placement(spec)
        assert "->" in result
        assert "horizontal" in result

    def test_no_orientation_not_included(self) -> None:
        spec = PlacementSpec(screen=1, layout=PlacementLayout(mode="thirds", slot=2))
        result = format_placement(spec)
        assert "/" not in result


# ---------------------------------------------------------------------------
# _extract_local_path_arg
# ---------------------------------------------------------------------------


class TestExtractLocalPathArg:
    def test_url_returns_none(self) -> None:
        assert _extract_local_path_arg("https://example.com") is None

    def test_flag_returns_none(self) -> None:
        assert _extract_local_path_arg("--verbose") is None
        assert _extract_local_path_arg("-v") is None

    def test_absolute_unix_path(self) -> None:
        assert _extract_local_path_arg("/home/user/file.txt") == "/home/user/file.txt"

    def test_tilde_path(self) -> None:
        assert _extract_local_path_arg("~/Documents/notes.md") == "~/Documents/notes.md"

    def test_relative_path(self) -> None:
        assert _extract_local_path_arg("./script.sh") == "./script.sh"

    def test_parent_relative_path(self) -> None:
        assert _extract_local_path_arg("../sibling/file") == "../sibling/file"

    def test_windows_drive_path(self) -> None:
        assert _extract_local_path_arg("C:\\Users\\name") == "C:\\Users\\name"

    def test_empty_string_returns_none(self) -> None:
        assert _extract_local_path_arg("") is None

    def test_quoted_path_stripped(self) -> None:
        result = _extract_local_path_arg('"/home/user/path"')
        assert result == "/home/user/path"

    def test_plain_name_returns_none(self) -> None:
        assert _extract_local_path_arg("myarg") is None


# ---------------------------------------------------------------------------
# _looks_like_obsidian_vault_arg
# ---------------------------------------------------------------------------


class TestLooksLikeObsidianVaultArg:
    def test_obsidian_exe_with_vault_suffix(self) -> None:
        assert _looks_like_obsidian_vault_arg(raw_path="C:\\Programs\\Obsidian.exe", arg="MyVault") is True

    def test_obsidian_exe_case_insensitive(self) -> None:
        assert _looks_like_obsidian_vault_arg(raw_path="C:\\obsidian.EXE", arg="Notes vault") is True

    def test_non_obsidian_path(self) -> None:
        assert _looks_like_obsidian_vault_arg(raw_path="C:\\Code.exe", arg="myvault") is False

    def test_obsidian_but_no_vault_suffix(self) -> None:
        assert _looks_like_obsidian_vault_arg(raw_path="C:\\Obsidian.exe", arg="--no-sandbox") is False


# ---------------------------------------------------------------------------
# _candidate_layouts and _candidate_subdivide_layouts
# ---------------------------------------------------------------------------


class TestCandidateLayouts:
    def test_all_valid_modes_present(self) -> None:
        layouts = _candidate_layouts()
        modes = {lay.mode for lay in layouts}
        assert modes == {"full", "halves", "thirds", "two-thirds", "quarters"}

    def test_halves_both_orientations_present(self) -> None:
        layouts = _candidate_layouts()
        halves = [lay for lay in layouts if lay.mode == "halves"]
        orientations = {lay.orientation for lay in halves}
        assert "vertical" in orientations
        assert "horizontal" in orientations

    def test_quarters_has_four_slots(self) -> None:
        layouts = _candidate_layouts()
        quarter_slots = {lay.slot for lay in layouts if lay.mode == "quarters"}
        assert quarter_slots == {1, 2, 3, 4}


class TestCandidateSubdivideLayouts:
    def test_returns_four_halves_layouts(self) -> None:
        layouts = _candidate_subdivide_layouts()
        assert len(layouts) == 4
        assert all(lay.mode == "halves" for lay in layouts)

    def test_both_orientations_both_slots(self) -> None:
        layouts = _candidate_subdivide_layouts()
        combos = {(lay.orientation, lay.slot) for lay in layouts}
        assert ("vertical", 1) in combos
        assert ("vertical", 2) in combos
        assert ("horizontal", 1) in combos
        assert ("horizontal", 2) in combos


# ---------------------------------------------------------------------------
# resolve_screen_count (non-Windows path)
# ---------------------------------------------------------------------------


class TestResolveScreenCount:
    def test_explicit_expected_count_returned(self) -> None:
        plan = WorkspacePlan(desktops=[])
        assert resolve_screen_count(plan, expected_screen_count=3) == 3

    def test_zero_clamps_to_zero(self) -> None:
        plan = WorkspacePlan(desktops=[])
        assert resolve_screen_count(plan, expected_screen_count=0) == 0

    def test_negative_clamps_to_zero(self) -> None:
        plan = WorkspacePlan(desktops=[])
        assert resolve_screen_count(plan, expected_screen_count=-1) == 0

    def test_inferred_from_plan_entries(self, monkeypatch: pytest.MonkeyPatch) -> None:
        monkeypatch.setattr("sys.platform", "linux")
        entry = LaunchEntry(
            name="App",
            type="app",
            path="/usr/bin/app",
            placement=PlacementSpec(screen=2, layout=PlacementLayout(mode="full", slot=1)),
        )
        plan = WorkspacePlan(desktops=[DesktopPlan(name="Work", launch=[entry])])
        # On non-Windows, falls back to max referenced screen
        result = resolve_screen_count(plan)
        assert result == 2

    def test_empty_plan_returns_zero(self, monkeypatch: pytest.MonkeyPatch) -> None:
        monkeypatch.setattr("sys.platform", "linux")
        plan = WorkspacePlan(desktops=[])
        assert resolve_screen_count(plan) == 0


# ---------------------------------------------------------------------------
# validate_plan_structure
# ---------------------------------------------------------------------------


class TestValidatePlanStructure:
    def _entry(self, name: str, screen: int, mode: str = "full", slot: int = 1) -> LaunchEntry:
        return LaunchEntry(
            name=name,
            type="app",
            path="/usr/bin/app",
            placement=PlacementSpec(screen=screen, layout=PlacementLayout(mode=mode, slot=slot)),
        )

    def test_empty_desktop_flagged(self) -> None:
        plan = WorkspacePlan(desktops=[DesktopPlan(name="Empty")])
        issues = validate_plan_structure(plan, expected_screen_count=1)
        assert any("empty" in i for i in issues)

    def test_full_coverage_one_screen_no_issues(self) -> None:
        plan = WorkspacePlan(desktops=[DesktopPlan(name="Work", launch=[self._entry("App", screen=1, mode="full")])])
        issues = validate_plan_structure(plan, expected_screen_count=1)
        assert issues == []

    def test_screen_left_empty_reported(self) -> None:
        # Screen 1 has two thirds only → 1/3 is empty
        plan = WorkspacePlan(
            desktops=[
                DesktopPlan(
                    name="Work",
                    launch=[
                        self._entry("A", screen=1, mode="thirds", slot=1),
                        self._entry("B", screen=1, mode="thirds", slot=2),
                    ],
                )
            ]
        )
        issues = validate_plan_structure(plan, expected_screen_count=1)
        assert any("empty" in i for i in issues)

    def test_screen_number_exceeds_expected_count_reported(self) -> None:
        plan = WorkspacePlan(desktops=[DesktopPlan(name="Work", launch=[self._entry("App", screen=3)])])
        issues = validate_plan_structure(plan, expected_screen_count=2)
        assert any("exceeds expected screen count" in i for i in issues)

    def test_strict_mode_reports_overlap(self) -> None:
        plan = WorkspacePlan(
            desktops=[
                DesktopPlan(
                    name="Work",
                    launch=[
                        self._entry("A", screen=1, mode="full"),
                        self._entry("B", screen=1, mode="full"),
                    ],
                )
            ]
        )
        issues = validate_plan_structure(plan, expected_screen_count=1, strict=True)
        assert any("overlap" in i for i in issues)

    def test_strict_mode_overlap_not_reported_in_non_strict(self) -> None:
        plan = WorkspacePlan(
            desktops=[
                DesktopPlan(
                    name="Work",
                    launch=[
                        self._entry("A", screen=1, mode="full"),
                        self._entry("B", screen=1, mode="full"),
                    ],
                )
            ]
        )
        issues = validate_plan_structure(plan, expected_screen_count=1, strict=False)
        assert not any("overlap" in i for i in issues)


class TestColumns:
    @staticmethod
    def _plan(*slots: int) -> WorkspacePlan:
        launch = [
            LaunchEntry(
                name=f"T{slot}",
                type="terminal",
                command="claude",
                cwd="/w",
                placement=PlacementSpec(screen=1, layout=PlacementLayout(mode="columns", slot=slot, min_width=800)),
            )
            for slot in slots
        ]
        return WorkspacePlan(desktops=[DesktopPlan(name="Work", launch=launch)])

    def test_column_count_follows_the_screen_width(self) -> None:
        assert columns_for_width(2560, 800) == 3
        assert columns_for_width(1920, 800) == 2
        assert columns_for_width(500, 800) == 1
        assert columns_for_width(2560, None) == 3

    def test_a_wide_screen_takes_every_column(self) -> None:
        fitted = fit_columns(self._plan(1, 2, 3), {1: 2560})

        entries = fitted.desktops[0].launch
        assert [e.disabled for e in entries] == [False, False, False]
        assert [e.placement.layout.count for e in entries if e.placement] == [3, 3, 3]

    def test_a_narrow_screen_switches_off_the_column_that_does_not_fit(self) -> None:
        fitted = fit_columns(self._plan(1, 2, 3), {1: 1920})

        assert [e.disabled for e in fitted.desktops[0].launch] == [False, False, True]
        first = fitted.desktops[0].launch[0].placement
        assert first is not None
        assert normalized_placement_rect(first) == UnitRect(Fraction(0), Fraction(0), Fraction(1, 2), Fraction(1))

    def test_an_unknown_width_keeps_the_plan_as_written(self) -> None:
        fitted = fit_columns(self._plan(1, 2, 3), {})

        assert [e.disabled for e in fitted.desktops[0].launch] == [False, False, False]
        third = fitted.desktops[0].launch[2].placement
        assert third is not None
        assert normalized_placement_rect(third).left == Fraction(2, 3)

    def test_pixel_rect_splits_the_work_area(self) -> None:
        layout = PlacementLayout(mode="columns", slot=3, min_width=800)

        assert _rect_from_layout(Rect(0, 0, 2560, 1392), layout) == Rect(1706, 0, 854, 1392)

    def test_validation(self) -> None:
        ok = PlacementSpec(screen=1, layout=PlacementLayout(mode="columns", slot=4, min_width=640))
        bad = PlacementSpec(screen=1, layout=PlacementLayout(mode="columns", slot=0, min_width=0))
        stray = PlacementSpec(screen=1, layout=PlacementLayout(mode="thirds", slot=1, min_width=800))

        assert validate_placement_spec(ok, "D", "E") == []
        assert len(validate_placement_spec(bad, "D", "E")) == 2
        assert validate_placement_spec(stray, "D", "E") == ["D/E: min_width only applies to columns"]
        for mode, orientation in (("full", None), ("halves", "vertical")):
            spec = PlacementSpec(
                screen=1, layout=PlacementLayout(mode=mode, slot=1, orientation=orientation, min_width=800)
            )
            assert validate_placement_spec(spec, "D", "E") == ["D/E: min_width only applies to columns"]

    def test_columns_is_rejected_in_a_subdivide(self) -> None:
        spec = PlacementSpec(
            screen=1,
            layout=PlacementLayout(mode="full"),
            subdivide=PlacementLayout(mode="columns", slot=3, min_width=800),
        )

        assert validate_placement_spec(spec, "D", "E") == ["D/E: columns applies to the screen, not a subdivide"]

    def test_min_width_round_trips_and_count_is_not_saved(self) -> None:
        layout = PlacementLayout(mode="columns", slot=2, min_width=800, count=3)

        assert layout.to_dict() == {"mode": "columns", "slot": 2, "min_width": 800}
        assert PlacementLayout.from_dict(layout.to_dict()) == PlacementLayout(mode="columns", slot=2, min_width=800)


# ---------------------------------------------------------------------------
# placement_rect_in and infer_placement: the machine-free halves of the win32 wrappers
# ---------------------------------------------------------------------------

_LEFT_SCREEN = DisplayArea(screen_number=1, bounds=Rect(0, 0, 300, 120))
_RIGHT_SCREEN = DisplayArea(screen_number=2, bounds=Rect(300, 0, 400, 200))


@pytest.mark.parametrize(
    ("placement", "expected"),
    [
        (PlacementSpec(screen=2, layout=PlacementLayout(mode="full")), Rect(300, 0, 400, 200)),
        (PlacementSpec(screen=2, layout=PlacementLayout(mode="thirds", slot=3)), Rect(566, 0, 134, 200)),
        (
            PlacementSpec(
                screen=2,
                layout=PlacementLayout(mode="halves", slot=2, orientation="vertical"),
                subdivide=PlacementLayout(mode="halves", slot=1, orientation="horizontal"),
            ),
            Rect(500, 0, 200, 100),
        ),
    ],
)
def test_placement_rect_in_lays_the_slot_out_on_the_given_screen(placement: PlacementSpec, expected: Rect) -> None:
    assert placement_rect_in(_RIGHT_SCREEN.bounds, placement) == expected


@pytest.mark.parametrize(
    ("window", "expected"),
    [
        (Rect(100, 0, 100, 120), PlacementSpec(screen=1, layout=PlacementLayout(mode="thirds", slot=2))),
        (Rect(300, 0, 400, 200), PlacementSpec(screen=2, layout=PlacementLayout(mode="full"))),
        # A half of a half ties with a quarter; the plainer quarter wins.
        (Rect(500, 100, 200, 100), PlacementSpec(screen=2, layout=PlacementLayout(mode="quarters", slot=3))),
        (Rect(10, 10, 20, 20), None),
    ],
)
def test_infer_placement_names_the_slot_a_window_fills(window: Rect, expected: PlacementSpec | None) -> None:
    assert infer_placement(window, [_LEFT_SCREEN, _RIGHT_SCREEN]) == expected
