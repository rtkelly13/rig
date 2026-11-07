"""Plan and carry out deleting closed duplicate saved tab groups."""

from __future__ import annotations

from collections.abc import Sequence
from dataclasses import dataclass
from pathlib import Path
from typing import TYPE_CHECKING

from rich.markup import escape

from desktop_manager.chrome.saved_groups import ConsolidationPlan, SavedGroup
from desktop_manager.chrome.tabs import ChromeWindow

if TYPE_CHECKING:
    from desktop_manager.chrome.driver import ChromeDriver

from desktop_manager.chrome.cli_common import (
    console,
    keep_desktop,
    short,
)


@dataclass(frozen=True, slots=True)
class SavedGroupsPlan:
    """A consolidation plan, with the window whose bookmarks bar each profile's deletes go through."""

    plan: ConsolidationPlan
    fullest: dict[str, int]


@dataclass(frozen=True, slots=True)
class Consolidation:
    """Saved groups deleted, and what failed (None when nothing did)."""

    deleted: int
    stopped: str | None = None


@dataclass(frozen=True, slots=True)
class _Bar:
    """One window's bookmarks bar."""

    hwnd: int
    groups: list[SavedGroup]


def plan_saved_groups(driver: ChromeDriver, windows: Sequence[ChromeWindow]) -> SavedGroupsPlan:
    """The consolidation plan over every profile's fullest bookmarks bar, with the window showing it."""
    from desktop_manager.chrome.saved_groups import plan_consolidation, union_of_bars

    bars: dict[str, list[_Bar]] = {}
    for window in windows:
        bars.setdefault(window.profile_key, []).append(
            _Bar(window.hwnd, driver.saved_groups(window.hwnd, window.profile_key))
        )
    fullest = {profile: max(seen, key=lambda bar: len(bar.groups)).hwnd for profile, seen in bars.items()}
    groups = [group for seen in bars.values() for group in union_of_bars(bar.groups for bar in seen)]
    return SavedGroupsPlan(plan_consolidation(groups), fullest)


def consolidation_marker() -> Path:
    """Lists the profiles an `organise --consolidate-saved` run left saved groups to look at again in."""
    from desktop_manager.chrome.install_location import install_root

    return install_root() / "saved-groups.pending"


def pending_profiles() -> set[str]:
    try:
        text = consolidation_marker().read_text(encoding="utf-8")
    except FileNotFoundError:
        return set()
    return {line for line in text.splitlines() if line}


def set_pending_profiles(profiles: set[str]) -> None:
    marker = consolidation_marker()
    if profiles:
        marker.parent.mkdir(parents=True, exist_ok=True)
        marker.write_text("".join(f"{profile}\n" for profile in sorted(profiles)), encoding="utf-8")
    else:
        marker.unlink(missing_ok=True)


def consolidate_saved_groups(driver: ChromeDriver, planned: SavedGroupsPlan) -> Consolidation:
    """Delete the plan's closed duplicates one chip at a time (deleted, what failed).

    A title that fails is left for the next run; the other titles still go."""
    with keep_desktop(driver.desktops):
        return _consolidate_saved_group_steps(driver, planned.plan, planned.fullest)


def _consolidate_saved_group_steps(
    driver: ChromeDriver, plan: ConsolidationPlan, fullest: dict[str, int]
) -> Consolidation:
    deleted = 0
    failed: list[str] = []
    for delete in plan.deletes:
        for _ in range(delete.count):
            result = driver.delete_saved_group(fullest[delete.profile], delete.profile, delete.title)
            if not result.deleted:
                failed.append(f"{delete.title!r} [{delete.profile}]: {result.detail}")
                console.print(f"  [yellow]kept[/yellow] {escape(delete.title)}: {escape(result.detail)}")
                break
            deleted += 1
            held = f": {', '.join(short(tab, 40) for tab in result.tabs)}" if result.tabs else ""
            console.print(f"  deleted {escape(delete.title)} [dim]({len(result.tabs)} saved tabs{escape(held)})[/dim]")
    return Consolidation(deleted, ("deleting saved groups " + "; ".join(failed)) if failed else None)
