from __future__ import annotations

from desktop_manager.models import WorkspacePlan


def resolve_target_desktop_index(plan: WorkspacePlan, target_desktop: str | None) -> int | None:
    if target_desktop is None:
        return None
    for idx, desktop in enumerate(plan.desktops, start=1):
        if desktop.name.casefold() == target_desktop.casefold():
            return idx
    available = ", ".join(d.name for d in plan.desktops)
    raise ValueError(f"Desktop '{target_desktop}' not found in plan. Available: {available}")
