"""Decide which GitHub pull-request tabs to close, and close them.

`plan_prune` is a pure function of (Chrome windows, GitHub resolution): no UIA,
no network. It applies the shared PR-pruning rules per Chrome profile across all
of that profile's windows:

1. **Duplicates** - several tabs for one PR keep one.
2. **Merged or closed** - every tab of a merged or closed PR closes.
3. **Chains** - in a stack or a merge queue only the lowest member that has a
   tab open keeps its tab; every other member's tabs close.
4. **Unknown state** - reported, never closed.

Tabs are identified from their titles alone, because that is all UI Automation
exposes: GitHub titles every PR page `... · Pull Request #<n> · <owner>/<repo>`,
whichever subpage is showing. Titles change when a PR is renamed and a
discarded tab keeps its old one, so tabs are keyed on `owner/repo#n` only.

`apply_plans` executes one or more plans (this one, and `dedupe`'s site plan)
through anything with `close_tab` and `restore_selection` - the UIA adapter in
production, a fake in tests.
"""

from __future__ import annotations

import re
from collections import defaultdict
from collections.abc import Callable, Iterable, Sequence
from dataclasses import dataclass
from typing import Literal, Protocol

from desktop_manager.chrome.tabs import ChromeTab, ChromeWindow
from rig import tracing
from rig.github_prs import PrRef, PrState, RepoChain, Resolution

CloseKind = Literal["merged", "closed", "duplicate", "stack", "queue"]
ReportKind = Literal["unknown", "protected"]

_PR_TITLE = re.compile(r"· Pull Request #(?P<number>\d+) · (?P<owner>[\w.-]+)/(?P<repo>[\w.-]+)(?=\s|$)")


def pr_ref_from_title(title: str) -> PrRef | None:
    """`owner/repo#n` from a GitHub PR page title, or None for any other page.

    Searched rather than anchored, so a title that still carries a Chrome
    suffix, or a PR title that itself contains " - ", still resolves.
    """
    matches = list(_PR_TITLE.finditer(title))
    if not matches:
        return None
    match = matches[-1]
    return PrRef(match.group("owner"), match.group("repo"), int(match.group("number")))


@dataclass(frozen=True)
class PrTab:
    """A tab known to show a PR, with the context the rules need."""

    tab: ChromeTab
    pr: PrRef
    profile: str
    window_age: int
    #: 1-based window number within its profile, oldest first - for human-readable reasons.
    window_number: int

    @property
    def label(self) -> str:
        return str(self.pr)


class PlacedTab(Protocol):
    """A tab with where it sits, as both planners see it."""

    @property
    def tab(self) -> ChromeTab: ...
    @property
    def profile(self) -> str: ...
    @property
    def window_age(self) -> int: ...
    @property
    def window_number(self) -> int: ...
    @property
    def label(self) -> str: ...


class PlannedTabClose(Protocol):
    @property
    def item(self) -> PlacedTab: ...
    @property
    def keeper(self) -> ChromeTab | None: ...


class ClosePlan(Protocol):
    @property
    def closes(self) -> Sequence[PlannedTabClose]: ...


def activations(closes: Iterable[PlannedTabClose]) -> dict[int, ChromeTab]:
    """Window -> tab to select because the window's active tab closed and its keeper is there."""
    chosen: dict[int, ChromeTab] = {}
    for close in closes:
        keeper = close.keeper
        if close.item.tab.selected and keeper is not None and keeper.window_hwnd == close.item.tab.window_hwnd:
            chosen.setdefault(keeper.window_hwnd, keeper)
    return chosen


@dataclass(frozen=True)
class PlannedClose:
    item: PrTab
    kind: CloseKind
    reason: str
    #: The tab that stays for this PR (or for the chain's keeper), if any. Selected in place
    #: of a closed tab that was active, when both are in the same window.
    keeper: ChromeTab | None = None


@dataclass(frozen=True)
class Report:
    item: PrTab
    kind: ReportKind
    detail: str


@dataclass(frozen=True)
class PrunePlan:
    closes: tuple[PlannedClose, ...]
    reports: tuple[Report, ...]
    kept: tuple[PrTab, ...]
    #: Every PR seen in a tab, for callers that report coverage.
    prs: frozenset[PrRef]

    def activations(self) -> dict[int, ChromeTab]:
        return activations(self.closes)


def pr_tabs(windows: Sequence[ChromeWindow]) -> list[PrTab]:
    """Every tab showing a PR, oldest window first and left to right."""
    numbering: dict[str, int] = defaultdict(int)
    found: list[PrTab] = []
    for window in sorted(windows, key=lambda window: (window.age_rank, window.hwnd)):
        numbering[window.profile_key] += 1
        for tab in window.tabs:
            pr = pr_ref_from_title(tab.title)
            if pr is not None:
                found.append(
                    PrTab(
                        tab=tab,
                        pr=pr,
                        profile=window.profile_key,
                        window_age=window.age_rank,
                        window_number=numbering[window.profile_key],
                    )
                )
    return found


def plan_prune(windows: Sequence[ChromeWindow], resolution: Resolution) -> PrunePlan:
    """Apply the PR rules to every profile's tabs. Pure; nothing is closed here."""
    items = pr_tabs(windows)
    chains = [chain for chain in resolution.chains if chain.slug not in resolution.failed_repos]

    by_profile: dict[str, list[PrTab]] = defaultdict(list)
    for item in items:
        by_profile[item.profile].append(item)

    closes: list[PlannedClose] = []
    reports: list[Report] = []
    kept: list[PrTab] = []
    for profile in sorted(by_profile):
        planned = _plan_profile(by_profile[profile], resolution.states, chains)
        closes.extend(planned.closes)
        reports.extend(planned.reports)
        kept.extend(planned.kept)
    return PrunePlan(
        closes=tuple(closes), reports=tuple(reports), kept=tuple(kept), prs=frozenset(item.pr for item in items)
    )


def _plan_profile(items: list[PrTab], states: dict[PrRef, PrState], chains: list[RepoChain]) -> _ProfilePlan:
    by_pr: dict[PrRef, list[PrTab]] = defaultdict(list)
    for item in items:
        by_pr[item.pr].append(item)

    closes: list[PlannedClose] = []
    reports: list[Report] = []
    kept: list[PrTab] = []
    open_prs: set[PrRef] = set()

    for pr in sorted(by_pr):
        state = states.get(pr) or PrState("UNKNOWN", "not resolved")
        if state.state == "UNKNOWN":
            reports.extend(Report(item, "unknown", state.error or "unknown state") for item in by_pr[pr])
        elif state.state == "CLOSED":
            closes.extend(PlannedClose(item, "closed", "closed without merging") for item in by_pr[pr])
        elif state.state == "MERGED":
            closes.extend(PlannedClose(item, "merged", "merged") for item in by_pr[pr])
        else:
            open_prs.add(pr)

    # Rule 3: PR-level decisions first, so a chain keeper's duplicates are settled by rule 1 below.
    superseded: dict[PrRef, _Superseded] = {}
    for chain in chains:
        with_tabs = [member for member in chain.members if member in open_prs]
        if len(with_tabs) < 2:
            continue
        keeper = with_tabs[0]
        for member in with_tabs[1:]:
            if chain.kind == "stack":
                reason = f"superseded by #{keeper.number} (stack bottom)"
            else:
                reason = f"queued behind #{keeper.number}"
            superseded.setdefault(member, _Superseded(chain.kind, reason, keeper))

    # Rule 1 over every open PR that survives the chains; the chain keeper's kept tab is found here.
    kept_tab: dict[PrRef, PrTab] = {}
    for pr in sorted(open_prs - set(superseded)):
        group = by_pr[pr]
        keeper_item = min(group, key=keeper_preference)
        kept_tab[pr] = keeper_item
        kept.append(keeper_item)
        closes.extend(
            PlannedClose(item, "duplicate", f"duplicate of tab {describe(keeper_item)}", keeper=keeper_item.tab)
            for item in group
            if item is not keeper_item
        )

    for pr in sorted(superseded):
        by = superseded[pr]
        keeper_item = kept_tab.get(by.keeper)
        closes.extend(
            PlannedClose(item, by.kind, by.reason, keeper=keeper_item.tab if keeper_item else None)
            for item in by_pr[pr]
        )

    # Pinned and audible tabs are never closed, whatever the rule said.
    safe: list[PlannedClose] = []
    for close in closes:
        tab = close.item.tab
        if tab.pinned or tab.audible:
            guard = "pinned" if tab.pinned else "audible"
            reports.append(Report(close.item, "protected", f"{guard}; would close as: {close.reason}"))
            kept.append(close.item)
        else:
            safe.append(close)
    return _ProfilePlan(safe, reports, kept)


@dataclass(slots=True, frozen=True)
class _ProfilePlan:
    closes: list[PlannedClose]
    reports: list[Report]
    kept: list[PrTab]


@dataclass(slots=True, frozen=True)
class _Superseded:
    """An open PR whose tabs close because `keeper` heads its chain."""

    kind: CloseKind
    reason: str
    keeper: PrRef


@dataclass(slots=True, frozen=True, order=True)
class KeeperPreference:
    """Ordered so the tab to keep sorts first."""

    unselected: bool
    unloaded: bool
    window_age: int
    index: int


def keeper_preference(item: PlacedTab) -> KeeperPreference:
    """Active tab first, then a loaded one, then the leftmost in the oldest window."""
    return KeeperPreference(not item.tab.selected, not item.tab.loaded, item.window_age, item.tab.index)


def describe(item: PlacedTab) -> str:
    return f"{item.tab.index + 1} in window {item.window_number}"


# --- execution -----------------------------------------------------------------------------------


class CloseResult(Protocol):
    @property
    def closed(self) -> bool: ...
    @property
    def detail(self) -> str: ...
    @property
    def seconds(self) -> float: ...


class TabActions(Protocol):
    def close_tab(self, tab: ChromeTab) -> CloseResult: ...

    def restore_selection(
        self, before: Iterable[ChromeWindow], *, fallbacks: dict[int, ChromeTab] | None = None
    ) -> dict[int, ChromeTab | None]: ...


@dataclass(frozen=True)
class AppliedClose:
    planned: PlannedTabClose
    closed: bool
    detail: str
    seconds: float


def apply_plans(
    plans: Sequence[ClosePlan],
    windows: Sequence[ChromeWindow],
    actions: TabActions,
    *,
    order: Sequence[int] = (),
    after_window: Callable[[ChromeWindow, list[AppliedClose]], None] | None = None,
) -> list[AppliedClose]:
    """Close every planned tab, one window at a time, and put that window's selection
    back before moving to the next.

    Closing selects each tab first, so working window by window means each window
    changes once instead of flickering as closes hop between windows. Windows go
    in `order` (hwnds), then any others in plan order. `after_window` runs once per
    window after its selection is restored, for a caller that does more per window.

    Each close re-finds its tab by RuntimeId (inside `close_tab`), so the index
    shifts that every close causes cannot redirect a later close onto a keeper.
    """
    by_window: dict[int, list[PlannedTabClose]] = {}
    for close in (close for plan in plans for close in plan.closes):
        by_window.setdefault(close.item.tab.window_hwnd, []).append(close)
    known = {window.hwnd: window for window in windows}
    ranked = [hwnd for hwnd in order if hwnd in known] + [hwnd for hwnd in known if hwnd not in order]
    applied: list[AppliedClose] = []
    for hwnd in ranked:
        closes = by_window.get(hwnd, [])
        results: list[AppliedClose] = []
        if closes:
            window = known[hwnd]
            attrs = {"window.profile": window.profile_name or window.profile_key, "close.planned": len(closes)}
            with tracing.span("chrome_tabs.close_window", scope="desktop_manager.chrome", **attrs) as span:
                for close in closes:
                    result = actions.close_tab(close.item.tab)
                    results.append(AppliedClose(close, result.closed, result.detail, result.seconds))
                actions.restore_selection([window], fallbacks=activations(closes))
                span.set(**{"close.closed": sum(result.closed for result in results)})
        applied.extend(results)
        if after_window is not None:
            after_window(known[hwnd], results)
    return applied


def apply_plan(plan: ClosePlan, windows: Sequence[ChromeWindow], actions: TabActions) -> list[AppliedClose]:
    return apply_plans([plan], windows, actions)


@dataclass(frozen=True)
class HeldPlan:
    """What is left of the plans once `hold_unattended` has taken out what nobody watched."""

    closes: tuple[PlannedTabClose, ...]
    #: The closes taken out, left for an attended run.
    held: tuple[PlannedTabClose, ...] = ()


def hold_unattended(plans: Sequence[ClosePlan], *, foreground_hwnd: int | None) -> HeldPlan:
    """Split planned closes into those safe to run with nobody watching and those held back.

    A scheduled run holds back every tab that is some window's active tab - it is
    what the user last looked at there - and everything in the foreground window,
    where closing selects each tab first and would flash under the user's hands.
    """
    kept: list[PlannedTabClose] = []
    held: list[PlannedTabClose] = []
    for close in (close for plan in plans for close in plan.closes):
        tab = close.item.tab
        (held if tab.selected or tab.window_hwnd == foreground_hwnd else kept).append(close)
    return HeldPlan(tuple(kept), tuple(held))
