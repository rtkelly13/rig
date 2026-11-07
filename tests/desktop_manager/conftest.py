from __future__ import annotations

import pytest

from desktop_manager.models import Rect


@pytest.fixture(autouse=True)
def three_monitor_desk(monkeypatch: pytest.MonkeyPatch) -> None:
    """Give plan validation a fixed three-monitor desk instead of the host's displays.

    ``validate_plan`` rejects a placement on a screen the machine does not have, and
    on Windows it counts the real monitors. The bundled and sample plans use screens
    2 and 3, so they validated on a three-monitor workstation and failed on a
    single-display CI runner or laptop. Tests that care about the monitor count
    patch ``monitor_work_areas`` themselves.
    """
    monkeypatch.setattr(
        "desktop_manager.win32.geometry.monitor_work_areas",
        lambda: [Rect(0, 0, 1920, 1080), Rect(1920, 0, 1920, 1080), Rect(3840, 0, 1920, 1080)],
    )
