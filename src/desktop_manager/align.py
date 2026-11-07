"""Put already-open plan windows back on their planned desktop and slot, without launching anything.

``workspace align`` is the placement-only sibling of restore. It reuses restore's
window matching, decides per plan entry whether the matched window is in place,
needs moving, or must be left alone, and only then touches the machine. Deciding is
pure (:func:`decide_alignment`) so it can be tested without live windows; the
machine is reached only through :class:`AlignPorts`.
"""

from __future__ import annotations

import enum
from collections.abc import Callable, Mapping, Sequence
from dataclasses import dataclass, field, replace
from typing import Protocol

from desktop_manager.desktop_reconcile import (
    DesktopReconciliationResult,
    DesktopRenameAction,
    plan_virtual_desktops,
)
from desktop_manager.display_guard import ShowState
from desktop_manager.models import DesktopPlan, LaunchEntry, LiveWindow, PlacementSpec, Rect, WorkspacePlan
from desktop_manager.placement import format_placement, normalized_placement_rect, rect_similarity
from desktop_manager.restore_planner import resolve_target_desktop_index
from desktop_manager.window_reuse import (
    claim_reusable_window,
    competing_sibling_titles,
    reusable_window_match_score,
    window_identity_match_score,
)

# Same tolerance restore uses to call a window "already placed" (window_matches_placement).
PLACEMENT_SIMILARITY = 0.92
# A title (150+) or Chrome URL hint (170+) match. A process-name match plus the entry
# name in the title only reaches 125, which is too weak to pull a window across desktops.
STRONG_IDENTITY_SCORE = 150


class AlignOutcome(enum.Enum):
    IN_PLACE = "in place"
    MOVE = "to move"
    MOVED = "moved"
    MISSING = "missing"
    SKIPPED = "skipped"
    FAILED = "failed"


class SkipReason(enum.Enum):
    MINIMIZED = "minimized"
    MAXIMIZED = "maximized"
    FOREGROUND = "foreground"
    NO_DESKTOP = "desktop not present"
    CLOSED = "closed"


@dataclass(frozen=True, slots=True)
class EntryMatch:
    """A plan entry, the desktop it belongs on, and the live window matched to it (if any)."""

    desktop_index: int
    desktop: DesktopPlan
    entry: LaunchEntry
    window: LiveWindow | None


@dataclass(frozen=True, slots=True)
class AlignContext:
    """Live facts the decision needs beyond the window inventory."""

    desktop_count: int
    target_rect: Callable[[PlacementSpec], Rect]
    show_states: Mapping[int, ShowState] = field(default_factory=dict[int, ShowState])
    foreground_hwnd: int | None = None


@dataclass(frozen=True, slots=True)
class AlignDecision:
    desktop_index: int
    desktop_name: str
    entry: LaunchEntry
    window: LiveWindow | None
    outcome: AlignOutcome
    move_desktop: bool = False
    move_placement: bool = False
    skip_reason: SkipReason | None = None
    detail: str = ""

    def describe(self) -> str:
        label = f"{self.outcome.value}: {self.desktop_name}/{self.entry.name}"
        notes: list[str] = []
        if self.skip_reason is not None:
            notes.append(self.skip_reason.value)
        if self.move_desktop and self.window is not None:
            notes.append(f"desktop {self.window.desktop_number} -> {self.desktop_index}")
        if self.move_placement and self.entry.placement is not None:
            notes.append(f"-> {format_placement(self.entry.placement)}")
        if self.detail:
            notes.append(self.detail)
        return f"{label} ({'; '.join(notes)})" if notes else label


@dataclass(frozen=True, slots=True)
class AlignReport:
    decisions: list[AlignDecision]
    blocked: str | None = None
    view_restored: bool = False
    desktop_reconciliation: DesktopReconciliationResult | None = None

    def count(self, outcome: AlignOutcome) -> int:
        return sum(1 for decision in self.decisions if decision.outcome is outcome)


def match_plan_windows(plan: WorkspacePlan, windows: Sequence[LiveWindow]) -> list[EntryMatch]:
    """Match every enabled entry to at most one live window, in two passes.

    First pass: restore's own matching, desktop by desktop, over windows already on
    that desktop (or whose desktop is unknown). Running it for every desktop before
    any cross-desktop matching means a window sitting on its own desktop is never
    pulled away to fill another desktop's entry.

    Second pass: an entry still unmatched may claim a window from another desktop,
    but only on strong identity (its title or Chrome URL hint), never on process name
    alone -- a stray second Chrome or terminal window the user opened elsewhere must
    not be dragged into a planned slot.
    """
    substitutions = plan.substitutions()
    remaining = list(windows)
    slots: list[EntryMatch] = []

    for desktop_index, desktop in enumerate(plan.desktops, start=1):
        for entry in desktop.launch:
            if entry.disabled:
                continue
            claim = claim_reusable_window(
                entry,
                remaining,
                substitutions,
                target_desktop_number=desktop_index,
                competing_titles=competing_sibling_titles(desktop.launch, entry),
            )
            remaining = claim.remaining
            slots.append(EntryMatch(desktop_index, desktop, entry, claim.window))

    for position, slot in enumerate(slots):
        if slot.window is not None:
            continue
        competing = competing_sibling_titles(slot.desktop.launch, slot.entry)
        best: LiveWindow | None = None
        best_score = 0
        for window in remaining:
            if not _has_strong_identity(slot.entry, window, substitutions, competing):
                continue
            score = reusable_window_match_score(slot.entry, window, substitutions, competing_titles=competing)
            if score > best_score:
                best, best_score = window, score
        if best is not None:
            remaining = [window for window in remaining if window.hwnd != best.hwnd]
            slots[position] = replace(slot, window=best)
    return slots


def _has_strong_identity(
    entry: LaunchEntry, window: LiveWindow, substitutions: dict[str, str], competing: frozenset[str]
) -> bool:
    score = window_identity_match_score(entry, window.title, window.process_name, substitutions, competing)
    if score < STRONG_IDENTITY_SCORE:
        return False
    if entry.type != "terminal":
        return True
    # A terminal's command hint ("claude" in the title) fits every agent terminal on
    # the machine; only its configured window title singles one out.
    own_title = (entry.window_title or "").strip().casefold()
    return bool(own_title) and own_title in window.title.casefold()


def placement_in_place(
    window: LiveWindow, placement: PlacementSpec, target_rect: Callable[[PlacementSpec], Rect]
) -> bool:
    inferred = window.inferred_placement
    if inferred is not None and inferred.to_dict() == placement.to_dict():
        return True
    # Two layouts can name the same slot (e.g. a full screen subdivided into one half).
    if (
        inferred is not None
        and inferred.screen == placement.screen
        and normalized_placement_rect(inferred) == normalized_placement_rect(placement)
    ):
        return True
    if window.bounds is None:
        return False
    return rect_similarity(window.bounds, target_rect(placement)) >= PLACEMENT_SIMILARITY


def decide(match: EntryMatch, context: AlignContext) -> AlignDecision:
    """What align should do with one plan entry's window."""
    base = AlignDecision(match.desktop_index, match.desktop.name, match.entry, match.window, AlignOutcome.MISSING)
    window = match.window
    if window is None:
        return base

    # An unknown desktop (pyvda could not say, e.g. a pinned window) is left where it is.
    move_desktop = window.desktop_number is not None and window.desktop_number != match.desktop_index
    placement = match.entry.placement
    move_placement = placement is not None and not placement_in_place(window, placement, context.target_rect)
    if not move_desktop and not move_placement:
        return replace(base, outcome=AlignOutcome.IN_PLACE)

    moving = replace(base, move_desktop=move_desktop, move_placement=move_placement)
    skip_reason = _skip_reason(window, match.desktop_index, move_desktop=move_desktop, context=context)
    if skip_reason is not None:
        return replace(moving, outcome=AlignOutcome.SKIPPED, skip_reason=skip_reason)
    return replace(moving, outcome=AlignOutcome.MOVE)


def _skip_reason(
    window: LiveWindow, desktop_index: int, *, move_desktop: bool, context: AlignContext
) -> SkipReason | None:
    show_state = context.show_states.get(window.hwnd, ShowState.NORMAL)
    # Minimized or maximized is the user's own choice for that window right now.
    if show_state is ShowState.MINIMIZED:
        return SkipReason.MINIMIZED
    if show_state is ShowState.MAXIMIZED:
        return SkipReason.MAXIMIZED
    if context.foreground_hwnd is not None and window.hwnd == context.foreground_hwnd:
        return SkipReason.FOREGROUND
    if move_desktop and desktop_index > context.desktop_count:
        return SkipReason.NO_DESKTOP
    return None


def decide_alignment(
    plan: WorkspacePlan,
    windows: Sequence[LiveWindow],
    context: AlignContext,
    *,
    target_desktop_index: int | None = None,
) -> list[AlignDecision]:
    """Decide every enabled entry, optionally keeping only one desktop's decisions.

    Matching always covers the whole plan, so a one-desktop run still cannot steal a
    window that another desktop's entry owns.
    """
    return [
        decide(match, context)
        for match in match_plan_windows(plan, windows)
        if target_desktop_index is None or match.desktop_index == target_desktop_index
    ]


def desktop_name_mismatch(plan: WorkspacePlan, live_names: Sequence[str]) -> str | None:
    """Why the live desktops cannot be trusted to line up with the plan, or ``None``.

    Desktops are addressed by position, so a desktop left over from a switched-off
    feature (or a user-inserted one) shifts every later one. An unnamed live desktop
    is not a mismatch: Windows drops names, and restore puts them back.
    """
    for index, (desktop, live_name) in enumerate(zip(plan.desktops, live_names, strict=False), start=1):
        if live_name and live_name.casefold() != desktop.name.casefold():
            return f"desktop {index} is named '{live_name}' but the plan expects '{desktop.name}'"
    return None


def summary_line(report: AlignReport, *, dry_run: bool) -> str:
    prefix = "align (dry run)" if dry_run else "align"
    if report.blocked is not None:
        return f"{prefix}: skipped ({report.blocked})"
    parts: list[str] = []
    if report.desktop_reconciliation is not None:
        rec = report.desktop_reconciliation
        action_parts: list[str] = []
        if dry_run:
            if rec.plan.to_retire:
                action_parts.append(f"{len(rec.plan.to_retire)} to retire")
            if rec.plan.to_create:
                action_parts.append(f"{len(rec.plan.to_create)} to create")
            if rec.plan.to_rename:
                action_parts.append(f"{len(rec.plan.to_rename)} to rename")
        else:
            if rec.retired:
                action_parts.append(f"{len(rec.retired)} retired")
            if rec.created:
                action_parts.append(f"{len(rec.created)} created")
            if rec.renamed:
                action_parts.append(f"{len(rec.renamed)} renamed")
        if action_parts:
            parts.append(f"desktops: {', '.join(action_parts)}")
    moved = report.count(AlignOutcome.MOVE) if dry_run else report.count(AlignOutcome.MOVED)
    parts.extend([f"{moved} {'to move' if dry_run else 'moved'}", f"{report.count(AlignOutcome.IN_PLACE)} in place"])
    missing = report.count(AlignOutcome.MISSING)
    if missing:
        parts.append(f"{missing} missing")
    skipped = [decision.skip_reason for decision in report.decisions if decision.skip_reason is not None]
    if skipped:
        reasons = sorted({reason.value for reason in skipped})
        if len(reasons) == 1:
            parts.append(f"{len(skipped)} skipped ({reasons[0]})")
        else:
            counts = ", ".join(f"{sum(1 for reason in skipped if reason.value == value)} {value}" for value in reasons)
            parts.append(f"{len(skipped)} skipped ({counts})")
    failed = report.count(AlignOutcome.FAILED)
    if failed:
        parts.append(f"{failed} failed")
    return f"{prefix}: {', '.join(parts)}"


class AlignPorts(Protocol):
    """The machine as align sees it. Every write must leave focus and the current desktop alone."""

    def desktop_names(self) -> list[str]: ...
    def desktop_count(self) -> int: ...
    def current_desktop(self) -> int | None: ...
    def foreground_hwnd(self) -> int | None: ...
    def console_hwnd(self) -> int | None: ...
    def inventory(self, *, excluded_hwnds: set[int]) -> list[LiveWindow]: ...
    def show_state(self, hwnd: int) -> ShowState: ...
    def target_rect(self, placement: PlacementSpec) -> Rect: ...
    def is_window(self, hwnd: int) -> bool: ...
    def move_to_desktop(self, hwnd: int, desktop_index: int) -> str | None: ...
    def place(self, hwnd: int, rect: Rect) -> str | None: ...
    def restore_view(self, *, desktop: int | None, foreground: int | None, moved_hwnds: set[int]) -> bool: ...
    def ensure_desktop_count(self, count: int, settle_seconds: float = 0.65) -> None: ...
    def rename_desktop(self, desktop_index: int, name: str) -> bool: ...
    def retire_desktops(self, keep_count: int, retired_names: Sequence[str]) -> list[str]: ...


def run_alignment(
    plan: WorkspacePlan,
    ports: AlignPorts,
    *,
    target_desktop: str | None = None,
    dry_run: bool = False,
    reconcile_desktops: bool = False,
    log: Callable[[str], None] = lambda _message: None,
) -> AlignReport:
    """Decide, then (unless ``dry_run``) move the out-of-place windows.

    When ``reconcile_desktops=True``, ensures virtual desktops exist and have the right
    names (IaC reconciliation) instead of blocking when names drift.

    Raises ``ValueError`` for a ``target_desktop`` the plan does not have.
    """
    target_index = resolve_target_desktop_index(plan, target_desktop)

    desktop_result: DesktopReconciliationResult | None = None
    if reconcile_desktops:
        target_names = [d.name for d in plan.desktops]
        desktop_plan = plan_virtual_desktops(
            target_names,
            retired_names=plan.retired_desktop_names,
            current_names=ports.desktop_names(),
        )
        if desktop_plan.has_drift:
            if dry_run:
                desktop_result = DesktopReconciliationResult(
                    plan=desktop_plan,
                    retired=[],
                    created=[],
                    renamed=[],
                    failed_renames=[],
                    unchanged=desktop_plan.unchanged,
                    unmanaged=desktop_plan.unmanaged,
                    dry_run=True,
                )
                log(
                    f"desktop drift (dry run): {len(desktop_plan.to_create)} to create, "
                    f"{len(desktop_plan.to_rename)} to rename, {len(desktop_plan.to_retire)} to retire"
                )
            else:
                retired: list[str] = []
                if plan.retired_desktop_names:
                    retired = ports.retire_desktops(len(target_names), plan.retired_desktop_names)
                if desktop_plan.to_create:
                    ports.ensure_desktop_count(len(target_names))
                renamed: list[DesktopRenameAction] = []
                failed_renames: list[DesktopRenameAction] = []
                for action in desktop_plan.to_rename:
                    ok = ports.rename_desktop(action.index, action.target_name)
                    res_action = DesktopRenameAction(
                        index=action.index,
                        current_name=action.current_name,
                        target_name=action.target_name,
                        success=ok,
                        error=None if ok else "Rename failed",
                    )
                    renamed.append(res_action)
                    if not ok:
                        failed_renames.append(res_action)
                desktop_result = DesktopReconciliationResult(
                    plan=desktop_plan,
                    retired=retired,
                    created=desktop_plan.to_create,
                    renamed=renamed,
                    failed_renames=failed_renames,
                    unchanged=desktop_plan.unchanged,
                    unmanaged=desktop_plan.unmanaged,
                    dry_run=False,
                )
                log(
                    f"reconciled virtual desktops: {len(desktop_plan.to_create)} created, "
                    f"{len(renamed)} renamed, {len(retired)} retired"
                )
    else:
        mismatch = desktop_name_mismatch(plan, ports.desktop_names())
        if mismatch is not None:
            return AlignReport(decisions=[], blocked=f"{mismatch}; run workspace restore")

    console = ports.console_hwnd()
    windows = ports.inventory(excluded_hwnds={console} if console is not None else set())
    foreground = ports.foreground_hwnd()
    context = AlignContext(
        desktop_count=ports.desktop_count(),
        target_rect=ports.target_rect,
        show_states={window.hwnd: ports.show_state(window.hwnd) for window in windows},
        foreground_hwnd=foreground,
    )
    decisions = decide_alignment(plan, windows, context, target_desktop_index=target_index)
    if dry_run or not any(decision.outcome is AlignOutcome.MOVE for decision in decisions):
        return AlignReport(decisions=decisions, desktop_reconciliation=desktop_result)

    starting_desktop = ports.current_desktop()
    applied = [
        _apply(decision, ports, log) if decision.outcome is AlignOutcome.MOVE else decision for decision in decisions
    ]
    moved_hwnds = {
        decision.window.hwnd
        for decision in applied
        if decision.outcome is AlignOutcome.MOVED and decision.window is not None
    }
    view_restored = bool(moved_hwnds) and ports.restore_view(
        desktop=starting_desktop, foreground=foreground, moved_hwnds=moved_hwnds
    )
    if view_restored:
        log("Put the user's desktop or foreground window back after moving windows")
    return AlignReport(decisions=applied, view_restored=view_restored, desktop_reconciliation=desktop_result)


def _apply(decision: AlignDecision, ports: AlignPorts, log: Callable[[str], None]) -> AlignDecision:
    window = decision.window
    if window is None:  # pragma: no cover - MOVE decisions always carry a window
        return decision
    hwnd = window.hwnd
    # The user may have touched the window since the inventory; check again right before moving.
    if not ports.is_window(hwnd):
        return replace(decision, outcome=AlignOutcome.SKIPPED, skip_reason=SkipReason.CLOSED)
    if ports.foreground_hwnd() == hwnd:
        return replace(decision, outcome=AlignOutcome.SKIPPED, skip_reason=SkipReason.FOREGROUND)
    show_state = ports.show_state(hwnd)
    if show_state is not ShowState.NORMAL:
        reason = SkipReason.MINIMIZED if show_state is ShowState.MINIMIZED else SkipReason.MAXIMIZED
        return replace(decision, outcome=AlignOutcome.SKIPPED, skip_reason=reason)

    if decision.move_desktop:
        error = ports.move_to_desktop(hwnd, decision.desktop_index)
        if error is not None:
            log(f"FAILED desktop move {decision.desktop_name}/{decision.entry.name} [{hwnd}]: {error}")
            return replace(decision, outcome=AlignOutcome.FAILED, detail=error)
    if decision.move_placement and decision.entry.placement is not None:
        error = ports.place(hwnd, ports.target_rect(decision.entry.placement))
        if error is not None:
            log(f"FAILED placement {decision.desktop_name}/{decision.entry.name} [{hwnd}]: {error}")
            return replace(decision, outcome=AlignOutcome.FAILED, detail=error)
    return replace(decision, outcome=AlignOutcome.MOVED)
