"""Placement geometry: where a slot sits on a screen, and how well a window fills it.

Pure functions over ``Rect`` and fractions of a screen. Nothing here touches the
machine; ``win32.geometry`` reads the monitors and windows and calls in here.
"""

from __future__ import annotations

from collections.abc import Iterable
from dataclasses import dataclass, replace
from fractions import Fraction

from desktop_manager.models import DisplayArea, PlacementLayout, PlacementSpec, Rect


@dataclass(slots=True, frozen=True)
class Span[N: (int, Fraction)]:
    """One axis of a slot: where it starts and how long it is."""

    start: N
    length: N


@dataclass(slots=True, frozen=True)
class UnitRect:
    """A placement as fractions of its screen's work area."""

    left: Fraction
    top: Fraction
    width: Fraction
    height: Fraction

    @property
    def right(self) -> Fraction:
        return self.left + self.width

    @property
    def bottom(self) -> Fraction:
        return self.top + self.height

    def with_columns(self, span: Span[Fraction]) -> UnitRect:
        return replace(self, left=span.start, width=span.length)

    def with_rows(self, span: Span[Fraction]) -> UnitRect:
        return replace(self, top=span.start, height=span.length)


_FULL_SCREEN = UnitRect(Fraction(0), Fraction(0), Fraction(1), Fraction(1))

#: Quarter slots run clockwise from the top left.
_QUARTER_COLUMN = {1: 1, 2: 2, 3: 2, 4: 1}
_QUARTER_ROW = {1: 1, 2: 1, 3: 2, 4: 2}


@dataclass(slots=True, frozen=True)
class Coverage:
    """How much of a screen no window covers, and how much more than one does."""

    empty: Fraction
    overlap: Fraction


@dataclass(slots=True, frozen=True)
class _LayoutMatch:
    layout: PlacementLayout
    score: float


def normalized_placement_rect(placement: PlacementSpec) -> UnitRect:
    rect = _normalized_rect_from_layout(_FULL_SCREEN, placement.layout)
    if placement.subdivide is not None:
        rect = _normalized_rect_from_layout(rect, placement.subdivide)
    return rect


def coverage_summary(rects: list[UnitRect]) -> Coverage:
    xs = {Fraction(0), Fraction(1)}
    ys = {Fraction(0), Fraction(1)}
    for rect in rects:
        xs.add(rect.left)
        xs.add(rect.right)
        ys.add(rect.top)
        ys.add(rect.bottom)

    sorted_xs = sorted(xs)
    sorted_ys = sorted(ys)
    empty = Fraction(0)
    overlap = Fraction(0)

    for x0, x1 in zip(sorted_xs, sorted_xs[1:], strict=False):
        for y0, y1 in zip(sorted_ys, sorted_ys[1:], strict=False):
            area = (x1 - x0) * (y1 - y0)
            if area <= 0:
                continue
            coverage_count = sum(
                1 for rect in rects if rect.left <= x0 and rect.top <= y0 and rect.right >= x1 and rect.bottom >= y1
            )
            if coverage_count == 0:
                empty += area
            elif coverage_count > 1:
                overlap += area

    return Coverage(empty, overlap)


def _normalized_rect_from_layout(rect: UnitRect, layout: PlacementLayout) -> UnitRect:
    if layout.mode == "full":
        return rect
    if layout.mode == "halves":
        return _normalized_rect_for_halves(rect, layout)
    if layout.mode == "thirds":
        return rect.with_columns(_fractional_horizontal_fraction(rect.left, rect.width, 3, layout.slot))
    if layout.mode == "two-thirds":
        return rect.with_columns(_fractional_horizontal_two_thirds(rect.left, rect.width, layout.slot))
    if layout.mode == "quarters":
        return _fractional_quarter_rect(rect, layout.slot)
    if layout.mode == "columns":
        return rect.with_columns(_fractional_horizontal_fraction(rect.left, rect.width, _count(layout), layout.slot))
    raise ValueError(f"Unknown layout mode: {layout.mode}")


def _normalized_rect_for_halves(rect: UnitRect, layout: PlacementLayout) -> UnitRect:
    orientation = layout.orientation or "vertical"
    if orientation == "vertical":
        return rect.with_columns(_fractional_horizontal_fraction(rect.left, rect.width, 2, layout.slot))
    if orientation == "horizontal":
        return rect.with_rows(_fractional_vertical_fraction(rect.top, rect.height, 2, layout.slot))
    raise ValueError(f"Unknown halves orientation: {orientation}")


def _fractional_horizontal_fraction(left: Fraction, width: Fraction, denominator: int, index: int) -> Span[Fraction]:
    if index < 1 or index > denominator:
        raise ValueError(f"Horizontal fraction index out of range: {index}/{denominator}")
    slice_width = width / denominator
    return Span(left + slice_width * (index - 1), slice_width)


def _fractional_vertical_fraction(top: Fraction, height: Fraction, denominator: int, index: int) -> Span[Fraction]:
    if index < 1 or index > denominator:
        raise ValueError(f"Vertical fraction index out of range: {index}/{denominator}")
    slice_height = height / denominator
    return Span(top + slice_height * (index - 1), slice_height)


def _fractional_horizontal_two_thirds(left: Fraction, width: Fraction, index: int) -> Span[Fraction]:
    third_width = width / 3
    if index == 1:
        return Span(left, third_width * 2)
    if index == 2:
        return Span(left + third_width, third_width * 2)
    raise ValueError(f"Two-thirds index out of range: {index}")


def _fractional_quarter_rect(rect: UnitRect, index: int) -> UnitRect:
    if index not in _QUARTER_COLUMN:
        raise ValueError(f"Quarter index out of range: {index}")
    column, row = _QUARTER_COLUMN[index], _QUARTER_ROW[index]
    return rect.with_columns(_fractional_horizontal_fraction(rect.left, rect.width, 2, column)).with_rows(
        _fractional_vertical_fraction(rect.top, rect.height, 2, row)
    )


def _with_columns(rect: Rect, span: Span[int]) -> Rect:
    return Rect(left=span.start, top=rect.top, width=span.length, height=rect.height)


def _with_rows(rect: Rect, span: Span[int]) -> Rect:
    return Rect(left=rect.left, top=span.start, width=rect.width, height=span.length)


def _rect_from_layout(rect: Rect, layout: PlacementLayout) -> Rect:
    if layout.mode == "full":
        return rect
    if layout.mode == "halves":
        return _rect_for_halves(rect, layout)
    if layout.mode == "thirds":
        return _with_columns(rect, _horizontal_fraction(rect.left, rect.width, 3, layout.slot))
    if layout.mode == "two-thirds":
        return _with_columns(rect, _horizontal_two_thirds(rect.left, rect.width, layout.slot))
    if layout.mode == "quarters":
        return _quarter_rect(rect, layout.slot)
    if layout.mode == "columns":
        count = layout.count or columns_for_width(rect.width, layout.min_width)
        return _with_columns(rect, _horizontal_fraction(rect.left, rect.width, max(count, layout.slot), layout.slot))
    raise ValueError(f"Unknown layout mode: {layout.mode}")


def _rect_for_halves(rect: Rect, layout: PlacementLayout) -> Rect:
    orientation = layout.orientation or "vertical"
    if orientation == "vertical":
        return _with_columns(rect, _horizontal_fraction(rect.left, rect.width, 2, layout.slot))
    if orientation == "horizontal":
        return _with_rows(rect, _vertical_fraction(rect.top, rect.height, 2, layout.slot))
    raise ValueError(f"Unknown halves orientation: {orientation}")


def _horizontal_fraction(left: int, width: int, denominator: int, index: int) -> Span[int]:
    if index < 1 or index > denominator:
        raise ValueError(f"Horizontal fraction index out of range: {index}/{denominator}")
    slice_width = max(1, width // denominator)
    x = left + slice_width * (index - 1)
    if index == denominator:
        return Span(x, width - slice_width * (denominator - 1))
    return Span(x, slice_width)


def _vertical_fraction(top: int, height: int, denominator: int, index: int) -> Span[int]:
    if index < 1 or index > denominator:
        raise ValueError(f"Vertical fraction index out of range: {index}/{denominator}")
    slice_height = max(1, height // denominator)
    y = top + slice_height * (index - 1)
    if index == denominator:
        return Span(y, height - slice_height * (denominator - 1))
    return Span(y, slice_height)


def _horizontal_two_thirds(left: int, width: int, index: int) -> Span[int]:
    third_width = max(1, width // 3)
    if index == 1:
        return Span(left, third_width * 2)
    if index == 2:
        return Span(left + third_width, width - third_width)
    raise ValueError(f"Two-thirds index out of range: {index}")


def _quarter_rect(rect: Rect, index: int) -> Rect:
    if index not in _QUARTER_COLUMN:
        raise ValueError(f"Quarter index out of range: {index}")
    column, row = _QUARTER_COLUMN[index], _QUARTER_ROW[index]
    columns = _horizontal_fraction(rect.left, rect.width, 2, column)
    rows = _vertical_fraction(rect.top, rect.height, 2, row)
    return Rect(left=columns.start, top=rows.start, width=columns.length, height=rows.length)


def _infer_layout_for_rect(rect: Rect, screen_rect: Rect) -> _LayoutMatch | None:
    best_layout: PlacementLayout | None = None
    best_score = 0.0
    for layout in _candidate_layouts():
        candidate_rect = _rect_from_layout(screen_rect, layout)
        score = rect_similarity(rect, candidate_rect)
        if score > best_score:
            best_score = score
            best_layout = layout
    if best_layout is None:
        return None
    return _LayoutMatch(best_layout, best_score)


def _candidate_layouts() -> list[PlacementLayout]:
    layouts = [PlacementLayout(mode="full", slot=1)]
    layouts.extend(PlacementLayout(mode="halves", slot=slot, orientation="vertical") for slot in (1, 2))
    layouts.extend(PlacementLayout(mode="halves", slot=slot, orientation="horizontal") for slot in (1, 2))
    layouts.extend(PlacementLayout(mode="thirds", slot=slot) for slot in (1, 2, 3))
    layouts.extend(PlacementLayout(mode="two-thirds", slot=slot) for slot in (1, 2))
    layouts.extend(PlacementLayout(mode="quarters", slot=slot) for slot in (1, 2, 3, 4))
    return layouts


def _candidate_subdivide_layouts() -> list[PlacementLayout]:
    return [
        PlacementLayout(mode="halves", slot=1, orientation="horizontal"),
        PlacementLayout(mode="halves", slot=2, orientation="horizontal"),
        PlacementLayout(mode="halves", slot=1, orientation="vertical"),
        PlacementLayout(mode="halves", slot=2, orientation="vertical"),
    ]


def rect_similarity(left: Rect, right: Rect) -> float:
    inter_left = max(left.left, right.left)
    inter_top = max(left.top, right.top)
    inter_right = min(left.right, right.right)
    inter_bottom = min(left.bottom, right.bottom)
    if inter_right <= inter_left or inter_bottom <= inter_top:
        return 0.0
    intersection = (inter_right - inter_left) * (inter_bottom - inter_top)
    union = left.width * left.height + right.width * right.height - intersection
    if union <= 0:
        return 0.0
    return intersection / union


DEFAULT_COLUMN_MIN_WIDTH = 800


def columns_for_width(width: int, min_width: int | None) -> int:
    """How many columns at least `min_width` pixels wide fit across `width`; always at least one."""
    return max(1, width // (min_width or DEFAULT_COLUMN_MIN_WIDTH))


def _count(layout: PlacementLayout) -> int:
    return max(layout.count or 0, layout.slot)


#: How closely a window must fill a slot before it counts as placed there.
INFERRED_PLACEMENT_SIMILARITY = 0.82


def placement_rect_in(screen: Rect, placement: PlacementSpec) -> Rect:
    """The pixel rectangle ``placement`` takes up on a screen whose work area is ``screen``."""
    rect = _rect_from_layout(screen, placement.layout)
    if placement.subdivide:
        rect = _rect_from_layout(rect, placement.subdivide)
    return rect


def infer_placement(rect: Rect, screens: Iterable[DisplayArea]) -> PlacementSpec | None:
    """The placement a window at ``rect`` most closely fills, or ``None`` when none is close enough."""
    best_match: PlacementSpec | None = None
    best_score = 0.0

    for screen in screens:
        screen_match = _infer_layout_for_rect(rect, screen.bounds)
        if screen_match is None:
            continue
        placement = screen_match.layout
        if screen_match.score > best_score:
            best_score = screen_match.score
            best_match = PlacementSpec(screen=screen.screen_number, layout=placement)

        for subdivide in _candidate_subdivide_layouts():
            base_rect = _rect_from_layout(screen.bounds, placement)
            inferred_rect = _rect_from_layout(base_rect, subdivide)
            score = rect_similarity(rect, inferred_rect)
            if score > best_score:
                best_score = score
                best_match = PlacementSpec(screen=screen.screen_number, layout=placement, subdivide=subdivide)

    return best_match if best_score >= INFERRED_PLACEMENT_SIMILARITY else None
