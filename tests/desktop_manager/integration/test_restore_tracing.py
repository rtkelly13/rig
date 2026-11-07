"""The restore pipeline emits one connected trace: CLI root, phases, desktops, entries."""

from __future__ import annotations

import json
import sys
from pathlib import Path
from typing import Any

import pytest

from desktop_manager.models import DesktopPlan, PlacementOutcome, WindowMatch
from desktop_manager.windows_workspace import (
    build_sample_plan,
    create_restore_run_logger,
    restore_workspace_with_options,
)
from rig import tracing


def _spans(trace_dir: Path) -> list[dict[str, Any]]:
    tracing.shutdown_tracing()
    spans: list[dict[str, Any]] = []
    for path in trace_dir.rglob("*.ndjson"):
        for line in path.read_text(encoding="utf-8").splitlines():
            for resource in json.loads(line)["resourceSpans"]:
                for scope in resource["scopeSpans"]:
                    spans.extend(scope["spans"])
    return spans


def _attr(span: dict[str, Any], key: str) -> Any:
    for item in span.get("attributes", []):
        if item["key"] == key:
            return next(iter(item["value"].values()))
    return None


def _restore_with_fakes(monkeypatch: pytest.MonkeyPatch, tmp_path: Path) -> None:
    from desktop_manager.plan import apply_feature_flags

    plan = build_sample_plan(tmp_path, terminal_count=1)
    plan.features = {"personal-desktop": False}
    plan.desktops.append(DesktopPlan(name="Personal", feature="personal-desktop"))
    plan = apply_feature_flags(plan)
    logger = create_restore_run_logger(tmp_path / "workspace.json")

    monkeypatch.setattr("desktop_manager.win32.desktops.get_current_desktop_number", lambda: 1)
    monkeypatch.setattr("desktop_manager.win32.geometry.current_window_handles_to_preserve", set)
    monkeypatch.setattr("desktop_manager.win32.inventory.inventory_reusable_windows", lambda **_kwargs: [])
    monkeypatch.setattr(
        "desktop_manager.restore.current_desktop_status",
        lambda desktop_name=None: DesktopPlan(name=desktop_name or "Work", captured_windows=[]),
    )
    monkeypatch.setattr(
        "desktop_manager.win32.inventory.collect_windows_matching_process", lambda *_args, **_kwargs: []
    )
    monkeypatch.setattr("desktop_manager.launching.launch_entry", lambda *_args, **_kwargs: None)
    monkeypatch.setattr(
        "desktop_manager.win32.entry_windows.wait_for_window_for_entry", lambda *_args, **_kwargs: WindowMatch(hwnd=123)
    )
    monkeypatch.setattr(
        "desktop_manager.win32.entry_windows.wait_for_window_title_change", lambda *_args, **_kwargs: "OpenCode"
    )
    monkeypatch.setattr(
        "desktop_manager.win32.entry_windows.apply_window_placement",
        lambda *_args, **_kwargs: PlacementOutcome(placed=True),
    )

    restore_workspace_with_options(plan, dry_run=False, diff_existing=False, logger=logger)
    log = logger.run_log_path.read_text(encoding="utf-8")
    assert "Trace spans: " in log
    assert "Step timings: settle_app_updates " in log


def test_restore_emits_one_connected_trace(
    monkeypatch: pytest.MonkeyPatch, tmp_path: Path, isolate_trace_output: Path, fake_desktop_navigation
) -> None:
    _restore_with_fakes(monkeypatch, tmp_path)

    spans = _spans(isolate_trace_output)
    by_id = {span["spanId"]: span for span in spans}
    names = [span["name"] for span in spans]

    root = next(span for span in spans if span["name"] == "workspace.restore")
    assert _attr(root, "restore.dry_run") is False
    for phase in ("restore.plan", "restore.ensure_desktop_count", "restore.retire_desktops", "restore.return_to_start"):
        assert phase in names, phase
    # Retiring renumbers desktops, so it and the top-up must finish before windows are planned.
    start = {span["name"]: int(span["startTimeUnixNano"]) for span in spans}
    assert start["restore.retire_desktops"] < start["restore.ensure_desktop_count"] < start["restore.plan"]
    desktops = [span for span in spans if span["name"] == "restore.desktop"]
    assert [_attr(span, "desktop.name") for span in desktops] == ["Work", "Admin"]
    assert {span["parentSpanId"] for span in desktops} == {root["spanId"]}
    assert names.count("restore.rename_desktop") == 2

    # Every entry span hangs off a desktop attempt, which hangs off its desktop.
    entry_spans = [span for span in spans if span["name"].startswith("restore.entry.")]
    assert entry_spans
    for entry_span in entry_spans:
        attempt = by_id[entry_span["parentSpanId"]]
        assert attempt["name"] == "restore.desktop_attempt"
        assert by_id[attempt["parentSpanId"]]["name"] == "restore.desktop"
    assert all(span["traceId"] == root["traceId"] for span in spans)


def test_dry_run_lists_the_desktop_it_would_retire(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    from desktop_manager.plan import apply_feature_flags

    plan = build_sample_plan(tmp_path, terminal_count=1)
    plan.features = {"personal-desktop": False}
    plan.desktops.append(DesktopPlan(name="Personal", feature="personal-desktop"))
    plan = apply_feature_flags(plan)
    monkeypatch.setattr("desktop_manager.win32.geometry.current_window_handles_to_preserve", set)
    monkeypatch.setattr("desktop_manager.win32.inventory.inventory_reusable_windows", lambda **_kwargs: [])

    actions = restore_workspace_with_options(plan, dry_run=True, diff_existing=True)

    assert actions[0] == "Remove switched-off desktop(s), if present: Personal"


def test_cli_main_opens_a_root_span_named_after_the_command(
    monkeypatch: pytest.MonkeyPatch, isolate_trace_output: Path
) -> None:
    from desktop_manager import cli

    monkeypatch.setattr(sys, "argv", ["desktop-manager", "workspace", "--help"])
    with pytest.raises(SystemExit) as exit_info:
        cli.main()
    assert exit_info.value.code == 0

    spans = _spans(isolate_trace_output)
    root = next(span for span in spans if span["name"] == "desktop-manager workspace")
    assert root["parentSpanId"] == ""
    assert root["status"]["code"] == 1
    assert (isolate_trace_output / "desktop-manager").is_dir()
