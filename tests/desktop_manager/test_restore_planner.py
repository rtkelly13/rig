from __future__ import annotations

from desktop_manager.models import DesktopPlan, WorkspacePlan
from desktop_manager.restore_planner import resolve_target_desktop_index


def test_resolve_target_desktop_index_returns_matching_desktop() -> None:
    # Arrange
    plan = WorkspacePlan(desktops=[DesktopPlan(name="Work"), DesktopPlan(name="Admin")])

    # Act
    result = resolve_target_desktop_index(plan, "Admin")

    # Assert
    assert result == 2
