"""Integration tests for desktop-aware placement and navigation functions.

These tests exercise ``apply_second_pass_placements``,
``is_window_target_confirmed``, and ``ensure_current_desktop_number`` — all of
which require the win32 desktop-switching layer to be faked out.
"""

from __future__ import annotations

from typing import Any, cast

from desktop_manager.models import PlacementOutcome
from desktop_manager.windows_workspace import (
    DesktopPlan,
    LaunchEntry,
    PlacementLayout,
    PlacementSpec,
    WorkspacePlan,
    apply_second_pass_placements,
    ensure_current_desktop_number,
    is_window_target_confirmed,
)


def test_apply_second_pass_placements_skips_terminal_entries(
    monkeypatch, fake_desktop_navigation: dict[str, list[Any]]
):
    plan = WorkspacePlan(
        desktops=[
            DesktopPlan(
                name="Work",
                launch=[
                    LaunchEntry(
                        name="Workspace Terminal 1",
                        type="terminal",
                        command="opencode",
                        cwd="{workspace}",
                        placement=PlacementSpec(screen=1, layout=PlacementLayout(mode="full", slot=1)),
                        window_title="Workspace Terminal 1",
                        window_process_name="WindowsTerminal.exe",
                    )
                ],
            )
        ]
    )
    logs: list[str] = []

    monkeypatch.setattr(
        "desktop_manager.win32.entry_windows.apply_window_placement",
        lambda *_args, **_kwargs: PlacementOutcome(placed=True),
    )

    class _Logger:
        def log(self, message: str) -> None:
            logs.append(message)

    actions = apply_second_pass_placements(plan, logger=cast(Any, _Logger()))

    assert actions == ["Desktop 1: second-pass placement"]
    assert any("Skipped second-pass placement for terminal Workspace Terminal 1" in message for message in logs)


def test_is_window_target_confirmed_checks_desktop_and_placement(monkeypatch):
    entry = LaunchEntry(
        name="Chrome - Work Profile",
        type="app",
        path=r"C:\Program Files\Google\Chrome\Application\chrome.exe",
        placement=PlacementSpec(screen=3, layout=PlacementLayout(mode="halves", slot=1, orientation="vertical")),
    )

    monkeypatch.setattr("desktop_manager.win32.desktops.get_window_desktop_number", lambda _hwnd: 2)
    monkeypatch.setattr("desktop_manager.window_reuse.window_matches_placement", lambda _hwnd, _placement: True)
    assert is_window_target_confirmed(123, 3, entry) is False

    monkeypatch.setattr("desktop_manager.win32.desktops.get_window_desktop_number", lambda _hwnd: 3)
    monkeypatch.setattr("desktop_manager.window_reuse.window_matches_placement", lambda _hwnd, _placement: False)
    assert is_window_target_confirmed(123, 3, entry) is False

    monkeypatch.setattr("desktop_manager.window_reuse.window_matches_placement", lambda _hwnd, _placement: True)
    assert is_window_target_confirmed(123, 3, entry) is True


def test_ensure_current_desktop_number_uses_pyvda_current(monkeypatch, fake_desktop_navigation: dict[str, list[Any]]):
    current = {"value": 2}

    monkeypatch.setattr("desktop_manager.win32.desktops.get_current_desktop_number", lambda: current["value"])

    def fake_go_to_desktop_number(desktop_index: int) -> None:
        fake_desktop_navigation["go_to_desktop"].append(desktop_index)
        current["value"] = desktop_index

    monkeypatch.setattr("desktop_manager.win32.desktops.go_to_desktop_number", fake_go_to_desktop_number)

    assert ensure_current_desktop_number(4, settle_seconds=0.1) is True
    assert fake_desktop_navigation["go_to_desktop"] == [4]


def test_ensure_current_desktop_number_falls_back_to_leftmost_anchor_when_pyvda_stalls(
    monkeypatch, fake_desktop_navigation: dict[str, list[Any]]
):
    current = {"value": 2}

    monkeypatch.setattr("desktop_manager.win32.desktops.get_current_desktop_number", lambda: current["value"])

    def fake_go_to_desktop_number(desktop_index: int) -> None:
        fake_desktop_navigation["go_to_desktop"].append(desktop_index)
        assert desktop_index == 4

    def fake_switch_leftmost(steps: int) -> None:
        fake_desktop_navigation["switch_leftmost"].append(steps)
        current["value"] = 1

    def fake_move_right(steps: int) -> None:
        fake_desktop_navigation["move_right"].append(steps)
        current["value"] = 1 + steps

    monkeypatch.setattr("desktop_manager.win32.desktops.go_to_desktop_number", fake_go_to_desktop_number)
    monkeypatch.setattr("desktop_manager.win32.desktops.switch_leftmost", fake_switch_leftmost)
    monkeypatch.setattr("desktop_manager.win32.desktops.move_right", fake_move_right)
    monkeypatch.setattr("desktop_manager.win32.desktops._send_virtual_desktop_hotkey", lambda *_args, **_kwargs: None)

    assert ensure_current_desktop_number(4, settle_seconds=0.1, max_attempts=1) is True
    assert fake_desktop_navigation["go_to_desktop"] == [4]
    assert fake_desktop_navigation["switch_leftmost"] == [6]
    assert fake_desktop_navigation["move_right"] == [3]
