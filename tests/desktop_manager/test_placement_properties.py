"""Property-based tests for placement geometry.

The example tests in ``test_placement_pure.py`` pin individual slots; these state the
rules every slot must obey, for any screen size and any layout:

- the slots of one layout tile their parent exactly: no gap, no overlap;
- a subdivided slot stays inside the slot it subdivides;
- the pixel layout and the unit (fractional) layout agree whenever the screen divides evenly;
- ``coverage_summary`` matches a brute-force cell count;
- ``rect_similarity`` behaves like intersection-over-union.
"""

from __future__ import annotations

from fractions import Fraction
from math import lcm

import pytest
from hypothesis import assume, given
from hypothesis import strategies as st

from desktop_manager.geometry import (
    Coverage,
    UnitRect,
    _horizontal_fraction,
    _rect_from_layout,
    columns_for_width,
    coverage_summary,
    normalized_placement_rect,
    rect_similarity,
)
from desktop_manager.models import PlacementLayout, PlacementSpec, Rect

pytestmark = pytest.mark.ci


# ---------------------------------------------------------------------------
# Strategies
# ---------------------------------------------------------------------------


def _tiling(mode: str, *, orientation: str | None = None, count: int | None = None) -> list[PlacementLayout]:
    """Every slot of one layout: together they should cover their parent exactly once."""
    slots = {"full": 1, "halves": 2, "thirds": 3, "quarters": 4}.get(mode, count or 0)
    return [PlacementLayout(mode=mode, slot=slot, orientation=orientation, count=count) for slot in range(1, slots + 1)]


@st.composite
def tilings(draw: st.DrawFn) -> list[PlacementLayout]:
    """A complete set of slots for a randomly chosen layout (two-thirds overlaps by design, so is excluded)."""
    choice = draw(st.sampled_from(["full", "halves-v", "halves-h", "thirds", "quarters", "columns"]))
    if choice == "halves-v":
        return _tiling("halves", orientation="vertical")
    if choice == "halves-h":
        return _tiling("halves", orientation="horizontal")
    if choice == "columns":
        return _tiling("columns", count=draw(st.integers(min_value=1, max_value=8)))
    return _tiling(choice)


def _denominator(layouts: list[PlacementLayout]) -> int:
    """The smallest pixel count every slot of these layouts divides evenly."""
    return lcm(*(len(_tiling(lay.mode, orientation=lay.orientation, count=lay.count)) for lay in layouts), 2)


screen_rects = st.builds(
    Rect,
    left=st.integers(min_value=-8000, max_value=8000),
    top=st.integers(min_value=-8000, max_value=8000),
    width=st.integers(min_value=1, max_value=8000),
    height=st.integers(min_value=1, max_value=8000),
)

# Rectangles on a 12x12 grid of the unit square, so a brute-force cell count is exact.
GRID = 12
grid_rects = st.builds(
    lambda x, y, w, h: UnitRect(Fraction(x, GRID), Fraction(y, GRID), Fraction(w, GRID), Fraction(h, GRID)),
    st.integers(0, GRID - 1),
    st.integers(0, GRID - 1),
    st.integers(1, GRID),
    st.integers(1, GRID),
).filter(lambda r: r.right <= 1 and r.bottom <= 1)


def _area(rect: UnitRect | Rect) -> Fraction:
    return Fraction(rect.width) * Fraction(rect.height)


def _contains(outer: UnitRect | Rect, inner: UnitRect | Rect) -> bool:
    return (
        outer.left <= inner.left
        and outer.top <= inner.top
        and inner.right <= outer.right
        and inner.bottom <= outer.bottom
    )


def _rescale(rect: UnitRect, parent: UnitRect) -> UnitRect:
    """Express ``rect`` (which lies inside ``parent``) in ``parent``'s own unit coordinates."""
    return UnitRect(
        (rect.left - parent.left) / parent.width,
        (rect.top - parent.top) / parent.height,
        rect.width / parent.width,
        rect.height / parent.height,
    )


# ---------------------------------------------------------------------------
# Unit (fractional) layouts
# ---------------------------------------------------------------------------


@given(tilings())
def test_every_layout_tiles_the_screen_exactly(slots: list[PlacementLayout]) -> None:
    rects = [normalized_placement_rect(PlacementSpec(screen=1, layout=slot)) for slot in slots]

    assert coverage_summary(rects) == Coverage(empty=Fraction(0), overlap=Fraction(0))
    assert sum(map(_area, rects)) == 1


@given(outer=tilings().flatmap(st.sampled_from), inner_slots=tilings())
def test_subdividing_a_slot_tiles_that_slot_exactly(outer: PlacementLayout, inner_slots: list[PlacementLayout]) -> None:
    parent = normalized_placement_rect(PlacementSpec(screen=1, layout=outer))
    children = [
        normalized_placement_rect(PlacementSpec(screen=1, layout=outer, subdivide=inner)) for inner in inner_slots
    ]

    assert all(_contains(parent, child) for child in children)
    rescaled = [_rescale(child, parent) for child in children]
    assert coverage_summary(rescaled) == Coverage(empty=Fraction(0), overlap=Fraction(0))


def test_two_thirds_slots_cover_the_screen_and_share_the_middle_third() -> None:
    left, right = (
        normalized_placement_rect(PlacementSpec(screen=1, layout=PlacementLayout(mode="two-thirds", slot=slot)))
        for slot in (1, 2)
    )

    assert coverage_summary([left, right]) == Coverage(empty=Fraction(0), overlap=Fraction(1, 3))


# ---------------------------------------------------------------------------
# Pixel layouts
# ---------------------------------------------------------------------------


@given(screen=screen_rects, slots=tilings())
def test_pixel_slots_partition_the_screen_with_no_gap_or_overlap(screen: Rect, slots: list[PlacementLayout]) -> None:
    assume(screen.width >= len(slots) and screen.height >= len(slots))
    rects = [_rect_from_layout(screen, slot) for slot in slots]

    # Win32 positions windows in whole pixels: a float here means a division slipped from // to /.
    assert all(type(v) is int for r in rects for v in (r.left, r.top, r.width, r.height))
    assert all(r.width > 0 and r.height > 0 for r in rects)
    assert all(_contains(screen, r) for r in rects)
    assert sum(map(_area, rects)) == _area(screen)
    # Pairwise disjoint: with the area sum equal to the screen's, this rules out both gaps and overlaps.
    assert all(rect_similarity(a, b) == 0.0 for i, a in enumerate(rects) for b in rects[i + 1 :])


@given(slots=tilings(), multiple=st.integers(min_value=1, max_value=40), origin=st.integers(-4000, 4000))
def test_pixel_and_unit_layouts_agree_when_the_screen_divides_evenly(
    slots: list[PlacementLayout], multiple: int, origin: int
) -> None:
    side = _denominator(slots) * multiple
    screen = Rect(left=origin, top=-origin, width=side, height=side)

    for slot in slots:
        pixel = _rect_from_layout(screen, slot)
        unit = normalized_placement_rect(PlacementSpec(screen=1, layout=slot))
        assert pixel == Rect(
            left=origin + int(unit.left * side),
            top=-origin + int(unit.top * side),
            width=int(unit.width * side),
            height=int(unit.height * side),
        )


@pytest.mark.xfail(
    strict=True,
    reason="Known defect: a slot narrower than one pixel per column yields a negative width. "
    "Strict, so fixing it turns this red and prompts promoting it to a plain property.",
)
def test_last_column_never_has_negative_width_on_a_tiny_parent() -> None:
    span = _horizontal_fraction(left=0, width=1, denominator=3, index=3)

    assert span.length >= 0


# ---------------------------------------------------------------------------
# coverage_summary against a brute-force oracle
# ---------------------------------------------------------------------------


def _cell_count_oracle(rects: list[UnitRect]) -> Coverage:
    empty = overlap = 0
    for cx in range(GRID):
        for cy in range(GRID):
            x, y = Fraction(cx, GRID), Fraction(cy, GRID)
            hits = sum(1 for r in rects if r.left <= x < r.right and r.top <= y < r.bottom)
            empty += hits == 0
            overlap += hits > 1
    return Coverage(empty=Fraction(empty, GRID * GRID), overlap=Fraction(overlap, GRID * GRID))


@given(st.lists(grid_rects, max_size=6))
def test_coverage_summary_matches_a_brute_force_cell_count(rects: list[UnitRect]) -> None:
    assert coverage_summary(rects) == _cell_count_oracle(rects)


@given(st.lists(grid_rects, min_size=1, max_size=6), st.randoms(use_true_random=False))
def test_coverage_summary_ignores_window_order(rects: list[UnitRect], rng) -> None:
    shuffled = list(rects)
    rng.shuffle(shuffled)

    assert coverage_summary(shuffled) == coverage_summary(rects)


# ---------------------------------------------------------------------------
# rect_similarity is intersection-over-union
# ---------------------------------------------------------------------------


@given(screen_rects, screen_rects)
def test_similarity_is_symmetric_and_bounded(a: Rect, b: Rect) -> None:
    score = rect_similarity(a, b)

    assert 0.0 <= score <= 1.0
    assert score == rect_similarity(b, a)


@given(screen_rects)
def test_a_rect_is_perfectly_similar_to_itself(a: Rect) -> None:
    assert rect_similarity(a, a) == 1.0


@given(screen_rects, screen_rects, st.integers(-5000, 5000), st.integers(-5000, 5000))
def test_similarity_is_unchanged_by_moving_both_rects_together(a: Rect, b: Rect, dx: int, dy: int) -> None:
    def moved(r: Rect) -> Rect:
        return Rect(left=r.left + dx, top=r.top + dy, width=r.width, height=r.height)

    assert rect_similarity(moved(a), moved(b)) == pytest.approx(rect_similarity(a, b))


@given(screen_rects, screen_rects)
def test_similarity_is_one_only_for_identical_rects(a: Rect, b: Rect) -> None:
    assume(a != b)

    assert rect_similarity(a, b) < 1.0


@given(screen_rects, st.integers(min_value=0, max_value=4000))
def test_rects_that_merely_touch_are_not_similar(a: Rect, gap: int) -> None:
    beside = Rect(left=a.right + gap, top=a.top, width=a.width, height=a.height)

    assert rect_similarity(a, beside) == 0.0


# ---------------------------------------------------------------------------
# columns_for_width
# ---------------------------------------------------------------------------


@given(width=st.integers(0, 20_000), min_width=st.one_of(st.none(), st.integers(1, 5000)))
def test_columns_are_the_most_that_fit_and_never_fewer_than_one(width: int, min_width: int | None) -> None:
    columns = columns_for_width(width, min_width)
    floor = min_width or 800

    assert columns >= 1
    if width >= floor:
        assert columns * floor <= width < (columns + 1) * floor
    else:
        assert columns == 1


@given(st.integers(0, 20_000), st.integers(0, 20_000), st.integers(1, 5000))
def test_wider_screens_never_hold_fewer_columns(a: int, b: int, min_width: int) -> None:
    narrow, wide = sorted((a, b))

    assert columns_for_width(narrow, min_width) <= columns_for_width(wide, min_width)
