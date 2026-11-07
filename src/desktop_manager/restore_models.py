from __future__ import annotations

from dataclasses import dataclass, field
from typing import Literal

from desktop_manager.models import DesktopPlan, LaunchEntry, LiveWindow


@dataclass(slots=True)
class PlannedEntryExecution:
    desktop: DesktopPlan
    desktop_index: int
    entry: LaunchEntry
    description: str
    action_kind: Literal["launch", "reuse", "satisfied"]
    reusable_window: LiveWindow | None = None


@dataclass(slots=True)
class PlannedDesktopExecution:
    desktop: DesktopPlan
    desktop_index: int
    close_existing: bool
    entry_steps: list[PlannedEntryExecution] = field(default_factory=list[PlannedEntryExecution])
    orphan_windows: list[LiveWindow] = field(default_factory=list[LiveWindow])
    # Entries the completion check has already confirmed in their planned slot.
    # A retry exists to fix what is still wrong; re-running a satisfied entry
    # relaunches an app that is already correctly placed (a second VS Code window
    # appeared this way on 2026-08-05) and lets it steal the window it was matched
    # against.
    satisfied_entry_names: set[str] = field(default_factory=set[str])
    # Entries this run has already launched. Their process may still be starting,
    # so a retry must wait for that instance rather than spawn another.
    launched_entry_names: set[str] = field(default_factory=set[str])


@dataclass(slots=True)
class RestoreExecutionPlan:
    desktops: list[PlannedDesktopExecution] = field(default_factory=list[PlannedDesktopExecution])
    target_desktop: str | None = None
    target_desktop_index: int | None = None

    def render_actions(self) -> list[str]:
        actions: list[str] = []
        for desktop_step in self.desktops:
            actions.append(f"Desktop {desktop_step.desktop_index}: {desktop_step.desktop.name}")
            if desktop_step.close_existing:
                actions.append("  - close existing windows on this desktop")
            for entry_step in desktop_step.entry_steps:
                if entry_step.action_kind == "satisfied":
                    actions.append(f"  - already in place `{entry_step.entry.name}`")
                    continue
                if entry_step.action_kind == "reuse" and entry_step.reusable_window is not None:
                    reuse_note = f"reuse window `{entry_step.entry.name}`"
                    if entry_step.reusable_window.desktop_number is not None:
                        reuse_note += f" from desktop {entry_step.reusable_window.desktop_number}"
                    actions.append(f"  - {reuse_note}")
                    continue
                actions.append(f"  - {entry_step.description}")
            for orphan_window in desktop_step.orphan_windows:
                label = orphan_window.title
                if orphan_window.process_name:
                    label += f" [{orphan_window.process_name}]"
                orphan_note = f"orphan open window `{label}`"
                if orphan_window.desktop_number is not None:
                    orphan_note += f" on desktop {orphan_window.desktop_number}"
                if orphan_window.screen_number is not None:
                    orphan_note += f" screen {orphan_window.screen_number}"
                actions.append(f"  - {orphan_note}")
        return actions
