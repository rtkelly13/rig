"""The `chrome-tabs` plans as plain lines. Pure, so what each command prints is testable."""

from __future__ import annotations

from collections.abc import Sequence
from typing import TYPE_CHECKING

from desktop_manager.chrome.dedupe import DedupePlan, SiteReportKind
from desktop_manager.chrome.grouping import GroupMove, GroupPlan
from desktop_manager.chrome.pr_prune import CloseKind, PrunePlan, ReportKind
from desktop_manager.chrome.rank import MemoryPlan, RankedTab, format_size
from desktop_manager.chrome.saved_groups import ConsolidationPlan

if TYPE_CHECKING:
    pass

from desktop_manager.chrome.cli_common import (
    plan_line,
    short,
)

_CLOSE_HEADINGS: dict[CloseKind, str] = {
    "merged": "Close: merged",
    "closed": "Close: closed without merging",
    "duplicate": "Close: duplicate PR tabs",
    "stack": "Close: superseded by a lower stack member",
    "queue": "Close: queued behind a lower queue member",
}
_REPORT_HEADINGS: dict[ReportKind, str] = {
    "unknown": "Report only: unknown state (never closed)",
    "protected": "Report only: pinned or audible (never closed)",
}
_SITE_REPORT_HEADINGS: dict[SiteReportKind, str] = {
    "variant": "Report only: title variant of a kept tab (never closed)",
    "report_only": "Report only: same title, rule too weak to close on",
    "protected": "Report only: pinned or audible duplicate (never closed)",
    "other_page": "Report only: same title, different URL (never closed)",
}


def render_plan(plan: PrunePlan) -> list[str]:
    """The plan as plain lines, grouped by reason. Pure, so the grouping is testable."""
    lines: list[str] = []
    for kind, heading in _CLOSE_HEADINGS.items():
        closes = [close for close in plan.closes if close.kind == kind]
        if closes:
            lines.append(f"{heading} ({len(closes)})")
            lines.extend(plan_line(close.item, close.reason) for close in closes)
    for kind, heading in _REPORT_HEADINGS.items():
        reports = [report for report in plan.reports if report.kind == kind]
        if reports:
            lines.append(f"{heading} ({len(reports)})")
            lines.extend(plan_line(report.item, report.detail) for report in reports)
    lines.append(
        f"{len(plan.closes)} to close, {len(plan.kept)} PR tabs kept, "
        f"{len(plan.reports)} reported, {len(plan.prs)} PRs seen"
    )
    return lines


def render_site_plan(plan: DedupePlan) -> list[str]:
    """The site-dedupe plan as plain lines, closes then reports."""
    lines: list[str] = []
    if plan.closes:
        lines.append(f"Close: duplicate pages ({len(plan.closes)})")
        lines.extend(plan_line(close.item, close.reason) for close in plan.closes)
    for kind, heading in _SITE_REPORT_HEADINGS.items():
        reports = [report for report in plan.reports if report.kind == kind]
        if reports:
            lines.append(f"{heading} ({len(reports)})")
            lines.extend(plan_line(report.item, report.detail) for report in reports)
    lines.append(f"{len(plan.closes)} to close, {len(plan.kept)} duplicated pages kept, {len(plan.reports)} reported")
    return lines


def render_memory_plan(plan: MemoryPlan) -> list[str]:
    """The memory-budget plan as plain lines."""
    lines: list[str] = []
    if plan.closes:
        lines.append(f"Close: over the {format_size(plan.limit_mb)} memory budget ({len(plan.closes)})")
        lines.extend(plan_line(close.item, close.reason) for close in plan.closes)
    verdict = (
        "still over budget: every remaining tab is protected or held for review"
        if plan.over_budget
        else "within budget"
    )
    lines.append(
        f"Memory: {format_size(plan.before_mb)} -> {format_size(plan.after_mb)} "
        f"of {format_size(plan.limit_mb)}, {verdict}"
    )
    return lines


def render_rank(ranked: Sequence[RankedTab]) -> list[str]:
    """One line per tab, keep-worthiest first: rank, score or protection, memory, key, title, reasons."""
    lines: list[str] = []
    for position, item in enumerate(ranked, start=1):
        standing = "keep" if item.protected else f"{item.score:+d}"
        memory = format_size(item.memory_mb) if item.tab.memory_mb is not None else "-"
        why = "; ".join((*item.reasons, *item.flags)) or "no signals"
        where = f"{item.profile} w{item.window_number} t{item.tab.index + 1}"
        lines.append(f"  {position:>3} {standing:>4} {memory:>8}  {short(item.label, 48):<48}  [{where}]  {why}")
    return lines


def render_group_plan(plan: GroupPlan) -> list[str]:
    """The grouping plan as plain lines, one block per window in the order they run."""
    lines: list[str] = []
    if plan.ungroups:
        lines.append(f"Ungroup first ({len(plan.ungroups)})")
        for ungroup in plan.ungroups:
            lines.append(
                f"  {'ungroup':<9} {ungroup.group.title:<24} {len(ungroup.tabs)} tabs in window {ungroup.window_number}"
            )
    for number in plan.window_numbers():
        moves = [move for move in plan.moves if move.window_number == number]
        lines.append(f"Window {number} [{moves[0].profile}] ({len(moves)})")
        for move in moves:
            lines.append(f"  {_verb(move):<9} {move.title:<24} tab {move.tab.index + 1:>3}  | {short(move.tab.title)}")
    lines.extend(f"  Skipped window {skip.window_number} [{skip.profile}]: {skip.reason}" for skip in plan.skipped)
    lines.extend(f"  Left {block.title} [{block.profile}]: {block.reason}" for block in plan.blocked)
    groups = sum(1 for move in plan.moves if move.new)
    crossing = sum(1 for move in plan.moves if move.crosses)
    lines.append(
        f"{len(plan.moves)} tabs to move ({crossing} across windows), {groups} new groups, "
        f"{len(plan.ungroups)} duplicates merged, {plan.already_grouped} already grouped, "
        f"{plan.elsewhere} left in other groups"
    )
    return lines


def _verb(move: GroupMove) -> str:
    if move.new:
        return f"new in w{move.home_number}" if move.crosses else "new group"
    return f"to w{move.home_number}" if move.crosses else "join"


def render_saved_groups(plan: ConsolidationPlan) -> list[str]:
    """Each profile's saved groups as its bookmarks bar shows them, then what consolidating deletes."""
    lines: list[str] = []
    for profile in dict.fromkeys(group.profile for group in plan.groups):
        groups = [group for group in plan.groups if group.profile == profile]
        lines.append(f"Saved groups [{profile}] ({len(groups)})")
        lines.extend(
            f"  {group.position + 1:>3}  {'open' if group.opened else 'closed':<6}  {group.title}" for group in groups
        )
    if plan.deletes:
        lines.append(f"Delete: closed duplicates ({plan.total})")
        for delete in plan.deletes:
            kept = "the open copy" if delete.open_copies else "the leftmost closed copy"
            lines.append(
                f"  {delete.count} x {delete.title:<24} [{delete.profile}] "
                f"of {delete.open_copies + delete.closed_copies} saved, keeping {kept}"
            )
    lines.extend(
        f"  Report only: {duplicate.title} [{duplicate.profile}] is open {duplicate.count} times (never deleted)"
        for duplicate in plan.open_duplicates
    )
    lines.append(f"{plan.total} saved groups to delete, {len(plan.groups)} seen")
    return lines
