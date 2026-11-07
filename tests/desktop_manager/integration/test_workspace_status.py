"""Integration tests for workspace status collection across virtual desktops."""

from __future__ import annotations

from desktop_manager.windows_workspace import (
    LiveWindow,
    PlacementLayout,
    PlacementSpec,
    current_workspace_status,
    current_workspace_status_for_desktop_indexes,
)


def test_current_workspace_status_collects_windows_without_switching(monkeypatch):
    windows = [
        LiveWindow(
            hwnd=10,
            title="Chrome",
            process_name="chrome.exe",
            desktop_number=1,
            screen_number=2,
            inferred_placement=PlacementSpec(screen=2, layout=PlacementLayout(mode="halves", slot=1)),
        ),
        LiveWindow(
            hwnd=20,
            title="Slack",
            process_name="slack.exe",
            desktop_number=2,
            screen_number=1,
            inferred_placement=PlacementSpec(screen=1, layout=PlacementLayout(mode="thirds", slot=1)),
        ),
    ]

    monkeypatch.setattr(
        "desktop_manager.win32.inventory.inventory_reusable_windows", lambda excluded_hwnds=None: windows
    )

    plan = current_workspace_status(desktop_count=2)
    expected = {
        "desktops": [
            {
                "name": "Desktop 1",
                "launch": [],
                "captured_windows": [
                    {
                        "title": "Chrome",
                        "process_name": "chrome.exe",
                        "screen_number": 2,
                        "inferred_placement": {"screen": 2, "layout": {"mode": "halves", "slot": 1}},
                    }
                ],
            },
            {
                "name": "Desktop 2",
                "launch": [],
                "captured_windows": [
                    {
                        "title": "Slack",
                        "process_name": "slack.exe",
                        "screen_number": 1,
                        "inferred_placement": {"screen": 1, "layout": {"mode": "thirds", "slot": 1}},
                    }
                ],
            },
        ]
    }

    assert [desktop.to_dict() for desktop in plan.desktops] == expected["desktops"]


def test_current_workspace_status_for_desktop_indexes_uses_cross_desktop_inventory(monkeypatch):
    windows = [
        LiveWindow(hwnd=10, title="Chrome", process_name="chrome.exe", desktop_number=1, screen_number=2),
        LiveWindow(hwnd=20, title="Slack", process_name="slack.exe", desktop_number=2, screen_number=1),
        LiveWindow(hwnd=30, title="Obsidian", process_name="Obsidian.exe", desktop_number=3, screen_number=1),
    ]

    monkeypatch.setattr("desktop_manager.win32.ensure_windows", lambda: None)
    monkeypatch.setattr(
        "desktop_manager.win32.inventory.inventory_reusable_windows", lambda excluded_hwnds=None: windows
    )

    plan = current_workspace_status_for_desktop_indexes([3, 1])

    assert [desktop.name for desktop in plan.desktops] == ["Desktop 3", "Desktop 1"]
    assert [window.title for window in plan.desktops[0].captured_windows] == ["Obsidian"]
    assert [window.title for window in plan.desktops[1].captured_windows] == ["Chrome"]
