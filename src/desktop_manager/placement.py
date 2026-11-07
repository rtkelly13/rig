from __future__ import annotations

import os
import sys
from collections.abc import Mapping
from dataclasses import dataclass, replace
from fractions import Fraction
from pathlib import Path

from desktop_manager.models import (
    COMMAND_AT_LOGON_VALUES,
    DesktopPlan,
    LaunchEntry,
    PlacementLayout,
    PlacementSpec,
    Rect,
    WorkspacePlan,
)
from desktop_manager.win32.errors import WIN32_ERRORS


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
class MonitorShortfall:
    required: int
    live: int


@dataclass(slots=True, frozen=True)
class _LayoutMatch:
    layout: PlacementLayout
    score: float


def placement_spec(
    screen: int,
    mode: str,
    slot: int = 1,
    *,
    orientation: str | None = None,
    subdivide_mode: str | None = None,
    subdivide_slot: int = 1,
    subdivide_orientation: str | None = None,
) -> PlacementSpec:
    subdivide = None
    if subdivide_mode is not None:
        subdivide = PlacementLayout(subdivide_mode, subdivide_slot, subdivide_orientation)
    return PlacementSpec(screen=screen, layout=PlacementLayout(mode, slot, orientation), subdivide=subdivide)


def validate_plan(plan: WorkspacePlan, *, require_existing_paths: bool = False) -> list[str]:
    from desktop_manager.win32.geometry import monitor_work_areas

    errors: list[str] = []
    monitor_count = 0
    substitutions = plan.substitutions()
    if not plan.desktops:
        errors.append("Workspace plan must define at least one desktop")
    if sys.platform == "win32":
        try:
            monitor_count = len(monitor_work_areas())
        except WIN32_ERRORS:
            monitor_count = 0

    seen_desktop_names: set[str] = set()
    for desktop in plan.desktops:
        if desktop.name in seen_desktop_names:
            errors.append(f"Duplicate desktop name: {desktop.name}")
        seen_desktop_names.add(desktop.name)

        seen_entry_names: set[str] = set()
        for entry in desktop.launch:
            if entry.name in seen_entry_names:
                errors.append(f"{desktop.name}: duplicate launch entry `{entry.name}`")
            seen_entry_names.add(entry.name)

            if entry.type == "terminal":
                if not entry.command:
                    errors.append(f"{desktop.name}/{entry.name}: terminal missing command")
                if not entry.cwd:
                    errors.append(f"{desktop.name}/{entry.name}: terminal missing cwd")
                elif require_existing_paths:
                    errors.extend(_validate_existing_paths([entry.cwd], substitutions, desktop.name, entry.name, "cwd"))
            elif entry.type == "app" and not entry.path:
                errors.append(f"{desktop.name}/{entry.name}: app missing path")
            elif entry.type == "app" and require_existing_paths:
                errors.extend(_validate_app_paths(entry.path, entry.args, substitutions, desktop.name, entry.name))
            elif entry.type == "command" and not entry.command:
                errors.append(f"{desktop.name}/{entry.name}: command entry missing command")
            elif entry.type == "command" and entry.cwd and require_existing_paths:
                errors.extend(_validate_existing_paths([entry.cwd], substitutions, desktop.name, entry.name, "cwd"))
            errors.extend(_command_at_logon_errors(entry, desktop.name))
            if entry.placement is not None:
                errors.extend(validate_placement_spec(entry.placement, desktop.name, entry.name, monitor_count))
    return errors


def _command_at_logon_errors(entry: LaunchEntry, desktop_name: str) -> list[str]:
    """`command_at_logon` is one of the known modes, and "deferred" only on a Windows Terminal entry."""
    if entry.command_at_logon is None:
        return []
    where = f"{desktop_name}/{entry.name}"
    if entry.command_at_logon not in COMMAND_AT_LOGON_VALUES:
        allowed = ", ".join(sorted(COMMAND_AT_LOGON_VALUES))
        return [f"{where}: command_at_logon must be one of {allowed}, not {entry.command_at_logon!r}"]
    if entry.defers_command_at_logon and (entry.type != "terminal" or entry.shell != "wt-pwsh"):
        return [f'{where}: command_at_logon "deferred" is only for terminal entries with shell wt-pwsh']
    return []


def _validate_app_paths(
    raw_path: str | None,
    raw_args: list[str],
    substitutions: dict[str, str],
    desktop_name: str,
    entry_name: str,
) -> list[str]:
    from desktop_manager.executable_paths import resolve_versioned_path
    from desktop_manager.placeholders import substitute_placeholders

    if not raw_path:
        return []
    errors: list[str] = []
    try:
        resolved_path = substitute_placeholders(raw_path, substitutions)
    except ValueError as err:
        return [f"{desktop_name}/{entry_name}: {err}"]
    if resolved_path is None:
        return []
    candidate_path = resolve_versioned_path(resolved_path)
    if not Path(os.path.expandvars(candidate_path)).expanduser().exists():
        errors.append(f"{desktop_name}/{entry_name}: app path does not exist: {candidate_path}")
    local_args: list[str] = []
    for raw_arg in raw_args:
        try:
            substituted_arg = substitute_placeholders(raw_arg, substitutions)
        except ValueError as err:
            errors.append(f"{desktop_name}/{entry_name}: {err}")
            continue
        if substituted_arg is None:
            continue
        if _extract_local_path_arg(substituted_arg):
            local_args.append(substituted_arg)
    for arg in local_args:
        candidate = Path(os.path.expandvars(arg)).expanduser()
        if not candidate.exists() and not _looks_like_obsidian_vault_arg(raw_path=resolved_path, arg=arg):
            errors.append(f"{desktop_name}/{entry_name}: arg does not exist: {arg}")
    return errors


def _validate_existing_paths(
    raw_values: list[str],
    substitutions: dict[str, str],
    desktop_name: str,
    entry_name: str,
    field_name: str,
) -> list[str]:
    from desktop_manager.placeholders import substitute_placeholders

    errors: list[str] = []
    for raw_value in raw_values:
        try:
            resolved = substitute_placeholders(raw_value, substitutions)
        except ValueError as err:
            errors.append(f"{desktop_name}/{entry_name}: {err}")
            continue
        if resolved is None:
            continue
        candidate = Path(os.path.expandvars(resolved)).expanduser()
        if not candidate.exists():
            errors.append(f"{desktop_name}/{entry_name}: {field_name} does not exist: {resolved}")
    return errors


def _extract_local_path_arg(arg: str) -> str | None:
    cleaned = arg.strip().strip('"')
    if not cleaned:
        return None
    lowered = cleaned.lower()
    if lowered.startswith(("http://", "https://", "--", "-")):
        return None
    if len(cleaned) >= 3 and cleaned[1] == ":" and cleaned[2] in {"\\", "/"}:
        return cleaned
    if cleaned.startswith(("./", "../", "/", "~/", "~\\")):
        return cleaned
    return None


def _looks_like_obsidian_vault_arg(*, raw_path: str, arg: str) -> bool:
    return raw_path.lower().endswith("obsidian.exe") and arg.lower().endswith("vault")


def _entry_is_active(entry: LaunchEntry, features: dict[str, bool]) -> bool:
    """Whether an entry contributes to screen coverage.

    Mirrors what :func:`desktop_manager.plan.apply_feature_flags` bakes into
    ``disabled``, so validation is correct for a plan read straight off disk as
    well — otherwise two mutually exclusive feature variants sharing a screen
    read as 100% overlap under ``--strict``.
    """
    if entry.disabled:
        return False
    return entry.feature is None or features.get(entry.feature, False)


def validate_plan_structure(
    plan: WorkspacePlan, expected_screen_count: int | None = None, *, strict: bool = False
) -> list[str]:
    issues: list[str] = []
    screen_count = resolve_screen_count(plan, expected_screen_count)

    for desktop in plan.desktops:
        if not desktop.launch:
            issues.append(f"{desktop.name}: virtual desktop is empty")

        placed_entries = [
            (entry, normalized_placement_rect(entry.placement))
            for entry in desktop.launch
            if entry.placement and _entry_is_active(entry, plan.features)
        ]
        if not placed_entries:
            continue

        referenced_screens = sorted({entry.placement.screen for entry, _ in placed_entries if entry.placement})
        if screen_count:
            missing_screens = [screen for screen in range(1, screen_count + 1) if screen not in referenced_screens]
            for screen_number in missing_screens:
                issues.append(f"{desktop.name}: screen {screen_number} is empty")
        for screen_number in referenced_screens:
            if screen_count and screen_number > screen_count:
                issues.append(f"{desktop.name}: screen {screen_number} exceeds expected screen count {screen_count}")
                continue
            screen_rects = [
                rect for entry, rect in placed_entries if entry.placement and entry.placement.screen == screen_number
            ]
            if not screen_rects:
                issues.append(f"{desktop.name}: screen {screen_number} is empty")
                continue

            coverage = coverage_summary(screen_rects)
            if coverage.empty > 0:
                issues.append(
                    f"{desktop.name}: screen {screen_number} leaves {_format_fraction_percent(coverage.empty)} empty"
                )
            if strict and coverage.overlap > 0:
                issues.append(
                    f"{desktop.name}: screen {screen_number} overlaps {_format_fraction_percent(coverage.overlap)}"
                )

    return issues


def monitor_shortfall(plan: WorkspacePlan) -> MonitorShortfall | None:
    """The shortfall when fewer monitors are live than the plan needs.

    ``required`` is the highest screen index referenced by any placement -- the same
    set :func:`validate_plan` checks against the detected monitor count. ``live`` is
    that detected count. Returns ``None`` when the environment can satisfy the plan,
    or when detection is unavailable (non-Windows / query failure), so the caller
    falls through to normal validation rather than skipping.
    """
    from desktop_manager.win32.geometry import monitor_work_areas

    required = max(
        (entry.placement.screen for desktop in plan.desktops for entry in desktop.launch if entry.placement),
        default=0,
    )
    if required <= 1 or sys.platform != "win32":
        return None
    try:
        live = len(monitor_work_areas())
    except WIN32_ERRORS:
        return None
    if 0 < live < required:
        return MonitorShortfall(required, live)
    return None


def validate_placement_spec(
    placement: PlacementSpec, desktop_name: str, entry_name: str, monitor_count: int = 0
) -> list[str]:
    prefix = f"{desktop_name}/{entry_name}"
    errors: list[str] = []
    if placement.screen < 1:
        errors.append(f"{prefix}: placement screen must be >= 1")
    if monitor_count and placement.screen > monitor_count:
        errors.append(f"{prefix}: placement screen {placement.screen} exceeds detected monitor count {monitor_count}")
    errors.extend(_validate_layout(placement.layout, prefix, allow_orientation=True))
    if placement.subdivide is not None:
        errors.extend(_validate_layout(placement.subdivide, prefix, allow_orientation=True))
        if placement.subdivide.mode == "columns":
            errors.append(f"{prefix}: columns applies to the screen, not a subdivide")
    return errors


def _validate_layout(layout: PlacementLayout, prefix: str, allow_orientation: bool) -> list[str]:
    errors: list[str] = []
    valid_modes = {"full", "halves", "thirds", "two-thirds", "quarters", "columns"}
    if layout.mode not in valid_modes:
        errors.append(f"{prefix}: invalid layout mode `{layout.mode}`")
        return errors
    if layout.min_width is not None and layout.mode != "columns":
        errors.append(f"{prefix}: min_width only applies to columns")
    if layout.mode == "full":
        return errors
    if layout.mode == "halves":
        if layout.slot not in {1, 2}:
            errors.append(f"{prefix}: halves slot must be 1 or 2")
        if layout.orientation not in {"vertical", "horizontal"}:
            errors.append(f"{prefix}: halves orientation must be vertical or horizontal")
        return errors
    if layout.orientation is not None and allow_orientation:
        errors.append(f"{prefix}: orientation only applies to halves")
    if layout.mode == "columns":
        if layout.slot < 1:
            errors.append(f"{prefix}: columns slot must be >= 1")
        if layout.min_width is not None and layout.min_width < 1:
            errors.append(f"{prefix}: columns min_width must be >= 1")
    if layout.mode == "thirds" and layout.slot not in {1, 2, 3}:
        errors.append(f"{prefix}: thirds slot must be 1, 2, or 3")
    if layout.mode == "two-thirds" and layout.slot not in {1, 2}:
        errors.append(f"{prefix}: two-thirds slot must be 1 or 2")
    if layout.mode == "quarters" and layout.slot not in {1, 2, 3, 4}:
        errors.append(f"{prefix}: quarters slot must be 1, 2, 3, or 4")
    return errors


def resolve_screen_count(plan: WorkspacePlan, expected_screen_count: int | None = None) -> int:
    from desktop_manager.win32.geometry import monitor_work_areas

    if expected_screen_count is not None:
        return max(0, expected_screen_count)

    if sys.platform == "win32":
        try:
            detected_count = len(monitor_work_areas())
            if detected_count > 0:
                return detected_count
        except WIN32_ERRORS:
            pass

    referenced_screens = [
        entry.placement.screen for desktop in plan.desktops for entry in desktop.launch if entry.placement
    ]
    return max(referenced_screens, default=0)


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


def _format_fraction_percent(value: Fraction) -> str:
    return f"{float(value * 100):.1f}%"


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


def placement_rect_for(placement: PlacementSpec) -> Rect:
    from desktop_manager.win32.geometry import monitor_work_area_by_number

    rect = monitor_work_area_by_number(placement.screen)
    rect = _rect_from_layout(rect, placement.layout)
    if placement.subdivide:
        rect = _rect_from_layout(rect, placement.subdivide)
    return rect


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


def infer_placement_for_window(hwnd: int) -> PlacementSpec | None:
    from desktop_manager.win32.geometry import monitor_display_areas, window_bounds

    rect = window_bounds(hwnd)
    if rect is None:
        return None
    best_match: PlacementSpec | None = None
    best_score = 0.0

    for screen in monitor_display_areas():
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

    return best_match if best_score >= 0.82 else None


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


def format_placement(placement: PlacementSpec) -> str:
    text = f"screen {placement.screen}: {placement.layout.mode}#{placement.layout.slot}"
    if placement.layout.orientation:
        text += f"/{placement.layout.orientation}"
    if placement.subdivide is not None:
        text += f" -> {placement.subdivide.mode}#{placement.subdivide.slot}"
        if placement.subdivide.orientation:
            text += f"/{placement.subdivide.orientation}"
    return text


DEFAULT_COLUMN_MIN_WIDTH = 800


def columns_for_width(width: int, min_width: int | None) -> int:
    """How many columns at least `min_width` pixels wide fit across `width`; always at least one."""
    return max(1, width // (min_width or DEFAULT_COLUMN_MIN_WIDTH))


def _count(layout: PlacementLayout) -> int:
    return max(layout.count or 0, layout.slot)


def live_screen_widths() -> dict[int, int]:
    """Each live screen's work-area width by screen number; empty off Windows or when it cannot be read."""
    if sys.platform != "win32":
        return {}
    from desktop_manager.win32.geometry import monitor_display_areas

    try:
        return {area.screen_number: area.bounds.width for area in monitor_display_areas()}
    except WIN32_ERRORS:
        return {}


def fit_columns(plan: WorkspacePlan, widths: Mapping[int, int]) -> WorkspacePlan:
    """Settle each `columns` placement's count from its screen's width, and switch off entries that do not fit.

    A wide screen holds more columns than a narrow one, so the same plan opens a third
    terminal on a 2560px screen and leaves it out on a 1920px one. A screen whose width
    is not in `widths` (not detected, or not on Windows) keeps the plan as written: as
    many columns as the highest slot any entry on that desktop and screen asks for.
    """
    desktops: list[DesktopPlan] = []
    for desktop in plan.desktops:
        written: dict[int, int] = {}
        for entry in desktop.launch:
            if entry.placement is not None and entry.placement.layout.mode == "columns":
                screen = entry.placement.screen
                written[screen] = max(written.get(screen, 0), entry.placement.layout.slot)
        if not written:
            desktops.append(desktop)
            continue
        launch: list[LaunchEntry] = []
        for entry in desktop.launch:
            placement = entry.placement
            if placement is None or placement.layout.mode != "columns":
                launch.append(entry)
                continue
            width = widths.get(placement.screen)
            count = (
                columns_for_width(width, placement.layout.min_width) if width is not None else written[placement.screen]
            )
            fitted = replace(placement, layout=replace(placement.layout, count=count))
            launch.append(replace(entry, placement=fitted, disabled=entry.disabled or placement.layout.slot > count))
        desktops.append(replace(desktop, launch=launch))
    return replace(plan, desktops=desktops)
