from __future__ import annotations

import os
import sys
from collections.abc import Mapping
from dataclasses import dataclass, replace
from fractions import Fraction
from pathlib import Path

from desktop_manager.geometry import columns_for_width, coverage_summary, normalized_placement_rect
from desktop_manager.models import (
    COMMAND_AT_LOGON_VALUES,
    DesktopPlan,
    LaunchEntry,
    PlacementLayout,
    PlacementSpec,
    WorkspacePlan,
)
from desktop_manager.win32.errors import WIN32_ERRORS


@dataclass(slots=True, frozen=True)
class MonitorShortfall:
    required: int
    live: int


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


def _format_fraction_percent(value: Fraction) -> str:
    return f"{float(value * 100):.1f}%"


def format_placement(placement: PlacementSpec) -> str:
    text = f"screen {placement.screen}: {placement.layout.mode}#{placement.layout.slot}"
    if placement.layout.orientation:
        text += f"/{placement.layout.orientation}"
    if placement.subdivide is not None:
        text += f" -> {placement.subdivide.mode}#{placement.subdivide.slot}"
        if placement.subdivide.orientation:
            text += f"/{placement.subdivide.orientation}"
    return text


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
