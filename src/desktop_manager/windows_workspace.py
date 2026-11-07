"""Re-export facade for backward compatibility.

All implementation has been split into submodules:
  - models.py       : dataclasses, constants, coercion helpers
  - geometry.py     : pure slot geometry and inference
  - placement.py    : plan validation
  - win32/          : Win32 API wrappers
  - plan.py         : IO, templates
  - placeholders.py : placeholder substitution
  - visualization.py: plan-vs-live workspace diff
  - restore.py          : orchestration, capture, status
  - workspace_restore.py: restore_workspace_with_options entry point
  - launching.py        : launching entries and tracking their processes
  - terminal_init.py    : one-off CLI self-updates before terminals launch
  - executable_paths.py : resolving executables (version wildcards, PATH, AppX)
  - window_reuse.py     : scoring, matching, claiming and moving existing windows
  - restore_timing.py   : launch settle delays
  - restore_readiness.py: waiting for a launched entry to be ready
  - restore_fallbacks.py: missing-path and monitor-shortfall notes

This module re-exports every public name so that existing imports from
``desktop_manager.windows_workspace`` continue to work unchanged.
"""

from __future__ import annotations

import time

from desktop_manager.desktop_reconcile import (
    DesktopCreationAction,
    DesktopItem,
    DesktopReconciliationPlan,
    DesktopReconciliationResult,
    DesktopRenameAction,
    plan_virtual_desktops,
    reconcile_virtual_desktops,
)
from desktop_manager.executable_paths import resolve_versioned_path
from desktop_manager.geometry import coverage_summary, normalized_placement_rect, rect_similarity
from desktop_manager.launching import (
    LaunchedProcess,
    build_terminal_command,
    find_windows_terminal_executable,
    launch_entry,
    terminate_launched_process,
    track_launched_processes,
)
from desktop_manager.models import (
    IGNORED_PROCESS_NAMES,
    IGNORED_WINDOW_TITLES,
    MACHINE_PLAN_TEMPLATE_PATH,
    PLAN_VERSION,
    SAMPLE_PLAN_SCREEN_COUNT,
    SAMPLE_PLAN_TEMPLATE_PATH,
    WINDOWS_TERMINAL_PROCESSES,
    DesktopPlan,
    DisplayArea,
    LaunchEntry,
    LiveWindow,
    PlacementLayout,
    PlacementSpec,
    Rect,
    RestoreRunLogger,
    WindowHandleCollector,
    WindowSnapshot,
    WorkspacePlan,
    decode_plan_entry,
)
from desktop_manager.placeholders import substitute_placeholders
from desktop_manager.placement import (
    format_placement,
    placement_spec,
    resolve_screen_count,
    validate_placement_spec,
    validate_plan,
    validate_plan_structure,
)
from desktop_manager.plan import (
    UnknownFeatureError,
    apply_feature_flags,
    build_bespoke_machine_plan,
    build_launch_entries_for_desktop,
    build_sample_plan,
    create_restore_run_logger,
    default_plan_path,
    load_machine_plan_template,
    load_plan,
    load_plan_context,
    load_plan_template,
    load_sample_plan_template,
    parse_plan_json,
    resolve_feature_flags,
    restore_log_paths,
    save_plan,
    save_plan_bundle,
    substitute_workspace,
    verify_plan,
    workspace_logs_dir,
)
from desktop_manager.restore import (
    capture_current_desktop_plan,
    capture_workspace_plan,
    current_desktop_status,
    current_workspace_status,
    current_workspace_status_for_desktop_indexes,
    find_launch_entries_by_name,
    find_launch_entry,
    launch_single_entry,
    restart_app_entries,
)
from desktop_manager.visualization import (
    WorkspaceDiff,
    diff_workspace,
    render_workspace_diff,
    truncate_text,
)
from desktop_manager.win32 import ensure_windows
from desktop_manager.win32.desktops import (
    clear_virtual_desktop_event_callback,
    ensure_current_desktop_number,
    ensure_desktop_count,
    get_current_desktop_number,
    get_virtual_desktop_count,
    get_window_desktop_number,
    go_to_desktop_number,
    is_window_on_current_desktop,
    move_right,
    move_window_to_desktop_number,
    rename_desktop,
    retire_switched_off_desktops,
    send_hotkey,
    set_virtual_desktop_event_callback,
    switch_leftmost,
    virtual_desktop_names,
)
from desktop_manager.win32.entry_windows import (
    apply_window_placement,
    choose_best_window_handle,
    wait_for_window_by_process,
    wait_for_window_by_title,
    wait_for_window_for_entry,
    wait_for_window_title_change,
)
from desktop_manager.win32.geometry import (
    adjust_rect_for_window_frame,
    current_window_handles_to_preserve,
    extended_frame_bounds,
    infer_placement_for_window,
    monitor_display_areas,
    monitor_work_area_by_number,
    monitor_work_area_by_slot,
    monitor_work_areas,
    move_window,
    placement_rect_for,
    primary_monitor_work_area,
    request_close_window,
    screen_number_for_window,
    window_bounds,
    window_title,
)
from desktop_manager.win32.inventory import (
    close_current_desktop_windows,
    collect_matching_windows,
    collect_windows_matching_process,
    get_process_details,
    inventory_reusable_windows,
    list_current_desktop_window_handles,
    list_current_desktop_windows,
)
from desktop_manager.window_reuse import (
    apply_second_pass_placements,
    captured_window_match_score,
    describe_launch,
    expected_process_name,
    is_entry_target_confirmed,
    is_window_target_confirmed,
    match_reusable_window,
    move_existing_window_to_entry,
    reusable_window_match_score,
    window_identity_match_score,
    window_matches_placement,
)
from desktop_manager.workspace_restore import restore_workspace_with_options

__all__ = [
    # models
    "IGNORED_PROCESS_NAMES",
    "IGNORED_WINDOW_TITLES",
    "MACHINE_PLAN_TEMPLATE_PATH",
    "PLAN_VERSION",
    "SAMPLE_PLAN_SCREEN_COUNT",
    "SAMPLE_PLAN_TEMPLATE_PATH",
    "WINDOWS_TERMINAL_PROCESSES",
    "DesktopPlan",
    "DisplayArea",
    "LaunchEntry",
    "LiveWindow",
    "PlacementLayout",
    "PlacementSpec",
    "Rect",
    "RestoreRunLogger",
    "WindowHandleCollector",
    "WindowSnapshot",
    "WorkspacePlan",
    "decode_plan_entry",
    # placement
    "coverage_summary",
    "format_placement",
    "infer_placement_for_window",
    "normalized_placement_rect",
    "placement_rect_for",
    "placement_spec",
    "rect_similarity",
    "resolve_screen_count",
    "validate_placement_spec",
    "validate_plan",
    "validate_plan_structure",
    # plan
    "UnknownFeatureError",
    "apply_feature_flags",
    "build_bespoke_machine_plan",
    "build_launch_entries_for_desktop",
    "build_sample_plan",
    "create_restore_run_logger",
    "default_plan_path",
    "load_machine_plan_template",
    "load_plan",
    "load_plan_context",
    "load_plan_template",
    "load_sample_plan_template",
    "parse_plan_json",
    "resolve_feature_flags",
    "restore_log_paths",
    "save_plan",
    "save_plan_bundle",
    "substitute_placeholders",
    "substitute_workspace",
    "verify_plan",
    "workspace_logs_dir",
    # restore
    "apply_second_pass_placements",
    "build_terminal_command",
    "capture_current_desktop_plan",
    "capture_workspace_plan",
    "current_desktop_status",
    "current_workspace_status",
    "current_workspace_status_for_desktop_indexes",
    "describe_launch",
    "find_launch_entries_by_name",
    "find_launch_entry",
    "find_windows_terminal_executable",
    "is_entry_target_confirmed",
    "is_window_target_confirmed",
    "LaunchedProcess",
    "launch_entry",
    "launch_single_entry",
    "terminate_launched_process",
    "track_launched_processes",
    "match_reusable_window",
    "move_existing_window_to_entry",
    "resolve_versioned_path",
    "restart_app_entries",
    "restore_workspace_with_options",
    "reusable_window_match_score",
    "window_matches_placement",
    # visualization
    "captured_window_match_score",
    "WorkspaceDiff",
    "diff_workspace",
    "expected_process_name",
    "render_workspace_diff",
    "truncate_text",
    "window_identity_match_score",
    # win32
    "DesktopCreationAction",
    "DesktopItem",
    "DesktopReconciliationPlan",
    "DesktopReconciliationResult",
    "DesktopRenameAction",
    "adjust_rect_for_window_frame",
    "apply_window_placement",
    "choose_best_window_handle",
    "clear_virtual_desktop_event_callback",
    "close_current_desktop_windows",
    "collect_matching_windows",
    "collect_windows_matching_process",
    "current_window_handles_to_preserve",
    "ensure_current_desktop_number",
    "ensure_desktop_count",
    "ensure_windows",
    "extended_frame_bounds",
    "get_current_desktop_number",
    "get_process_details",
    "get_virtual_desktop_count",
    "get_window_desktop_number",
    "go_to_desktop_number",
    "inventory_reusable_windows",
    "is_window_on_current_desktop",
    "list_current_desktop_window_handles",
    "list_current_desktop_windows",
    "monitor_display_areas",
    "monitor_work_area_by_number",
    "monitor_work_area_by_slot",
    "monitor_work_areas",
    "move_right",
    "move_window",
    "move_window_to_desktop_number",
    "plan_virtual_desktops",
    "primary_monitor_work_area",
    "reconcile_virtual_desktops",
    "rename_desktop",
    "request_close_window",
    "retire_switched_off_desktops",
    "screen_number_for_window",
    "send_hotkey",
    "set_virtual_desktop_event_callback",
    "switch_leftmost",
    "virtual_desktop_names",
    "wait_for_window_by_process",
    "wait_for_window_by_title",
    "wait_for_window_for_entry",
    "wait_for_window_title_change",
    "window_bounds",
    "window_title",
    # time
    "time",
]
