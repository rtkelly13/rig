"""Rank every tab by how much it is worth keeping, and plan closes that fit a memory budget.

`rank_tabs` is pure: (windows, GitHub resolution) -> one `RankedTab` per tab,
keep-worthiest first. A tab is either **protected** (never closed by any rule
here) or carries an integer **score** built from named factors, so every rank
explains itself:

Protected, in order: a video call (by title, so a muted call with the camera
off still counts), audible or live media, pinned, the active tab of its window,
and pages `site_rules` cannot identify (shells, sign-in pages...).

Score factors (higher keeps):

| Factor | Score |
|---|---|
| your review is requested on the PR | +3 |
| your own open PR | +2 |
| ... with checks failing, or changes requested | +1 each |
| a tracker ticket, issue or Google doc | +1 |
| someone else's approved PR not waiting on you | -1 |
| someone else's draft PR | -1 |
| a closed-unmerged PR | -1 |
| a merged PR | -2 |
| a weakly identified page (one-word title, Actions run) | -1 |
| in a minimised window | -1 |

PRs over `OVERSIZED_LINES` changed lines are flagged, which does not change their score.

`plan_memory` closes the lowest-scored loaded tabs first, and the biggest first
within a score, until the tabs' reported memory fits the budget. The budget is
the sum of the per-tab footprints Chrome shows in its tab names; the browser,
GPU and extension processes are not included.
"""

from __future__ import annotations

from collections import defaultdict
from collections.abc import Collection, Sequence
from dataclasses import dataclass
from functools import partial

from desktop_manager.chrome.pr_prune import pr_ref_from_title
from desktop_manager.chrome.site_rules import identify, is_call
from desktop_manager.chrome.tabs import ChromeTab, ChromeWindow
from rig.github_prs import PrDetails, PrRef, PrState, Resolution

OVERSIZED_LINES = 800

_WORK_SITES = frozenset({"youtrack", "jira", "github-issue", "google-docs"})


@dataclass(frozen=True)
class RankedTab:
    tab: ChromeTab
    profile: str
    window_age: int
    window_number: int
    label: str
    #: Why the tab is never closed here, or "" when it can be.
    protected: str
    score: int
    reasons: tuple[str, ...]
    #: Informational marks that do not change the score, such as "oversized".
    flags: tuple[str, ...] = ()
    pr: PrRef | None = None
    details: PrDetails | None = None

    @property
    def memory_mb(self) -> float:
        return self.tab.memory_mb or 0.0


def rank_tabs(windows: Sequence[ChromeWindow], resolution: Resolution | None = None) -> list[RankedTab]:
    """Every tab, protected first, then by score (highest first), then by memory (smallest first)."""
    states = resolution.states if resolution is not None else {}
    numbering: dict[str, int] = defaultdict(int)
    ranked: list[RankedTab] = []
    for window in sorted(windows, key=lambda window: (window.age_rank, window.hwnd)):
        numbering[window.profile_key] += 1
        for tab in window.tabs:
            ranked.append(_rank(tab, window, numbering[window.profile_key], states))
    return sorted(ranked, key=keep_order)


def keep_order(item: RankedTab) -> _KeepOrder:
    return _KeepOrder(not item.protected, -item.score, item.memory_mb, item.window_age, item.tab.index)


@dataclass(slots=True, frozen=True, order=True)
class _KeepOrder:
    """Protected tabs first, then by score, lightest, oldest window, leftmost."""

    unprotected: bool
    negative_score: int
    memory_mb: float
    window_age: int
    index: int


@dataclass(slots=True, frozen=True)
class _Factor:
    """One signed contribution to a tab's keep score, with why."""

    score: int
    why: str


def _rank(tab: ChromeTab, window: ChromeWindow, number: int, states: dict[PrRef, PrState]) -> RankedTab:
    pr = pr_ref_from_title(tab.title)
    identity = None if pr else identify(tab.title)
    label = str(pr) if pr else identity.key if identity else tab.title
    make = partial(RankedTab, tab, window.profile_key, window.age_rank, number)

    protected = _protection(tab, identified=pr is not None or identity is not None)
    if protected:
        return make(label=label, protected=protected, score=0, reasons=(protected,), pr=pr)

    factors: list[_Factor] = []
    flags: list[str] = []
    details: PrDetails | None = None
    if pr is not None:
        state = states.get(pr) or PrState("UNKNOWN")
        details = state.details
        factors.extend(_pr_factors(state.state, details))
        if details is not None and details.changed_lines > OVERSIZED_LINES:
            flags.append(f"oversized: +{details.additions}/-{details.deletions} in {details.changed_files} files")
    elif identity is not None:
        if identity.site in _WORK_SITES:
            factors.append(_Factor(1, f"work item ({identity.site})"))
        if not identity.auto:
            factors.append(_Factor(-1, "weakly identified title"))
    if window.minimized:
        factors.append(_Factor(-1, "minimised window"))

    return make(
        label=label,
        protected="",
        score=sum(factor.score for factor in factors),
        reasons=tuple(f"{factor.score:+d} {factor.why}" for factor in factors),
        flags=tuple(flags),
        pr=pr,
        details=details,
    )


def _protection(tab: ChromeTab, *, identified: bool) -> str:
    if is_call(tab.title):
        return "video call"
    if tab.audible:
        return "audible or live media"
    if tab.pinned:
        return "pinned"
    if tab.selected:
        return "active tab"
    if not identified:
        return "unidentified page"
    return ""


def _pr_factors(state: str, details: PrDetails | None) -> list[_Factor]:
    if state == "MERGED":
        return [_Factor(-2, "merged")]
    if state == "CLOSED":
        return [_Factor(-1, "closed without merging")]
    if state != "OPEN" or details is None:
        return []
    factors: list[_Factor] = []
    if details.review_requested:
        factors.append(_Factor(3, "your review is requested"))
    if details.authored_by_viewer:
        factors.append(_Factor(2, "your PR"))
        if details.checks_failing:
            factors.append(_Factor(1, "checks failing"))
        if details.review_decision == "CHANGES_REQUESTED":
            factors.append(_Factor(1, "changes requested"))
    elif not details.review_requested:
        if details.review_decision == "APPROVED":
            factors.append(_Factor(-1, "approved; not waiting on you"))
        if details.draft:
            factors.append(_Factor(-1, "someone else's draft"))
    return factors


# -- memory budget -----------------------------------------------------------------------------


@dataclass(frozen=True)
class MemoryClose:
    item: RankedTab
    reason: str
    keeper: ChromeTab | None = None


@dataclass(frozen=True)
class MemoryPlan:
    limit_mb: float
    #: Reported memory of the tabs that stay open after the other plans, before this one.
    before_mb: float
    after_mb: float
    closes: tuple[MemoryClose, ...]

    @property
    def over_budget(self) -> bool:
        return self.after_mb > self.limit_mb


def plan_memory(
    ranked: Sequence[RankedTab],
    limit_mb: float,
    *,
    already_closing: Collection[tuple[int, ...]] = (),
    held: Collection[tuple[int, ...]] = (),
) -> MemoryPlan:
    """Close the lowest-ranked, biggest loaded tabs until the rest fit `limit_mb`. Pure.

    `already_closing` tabs are left out of the total; `held` tabs (ones another plan
    reported for a person to decide, such as a title variant) count but never close.
    """
    staying = [item for item in ranked if item.tab.runtime_id not in already_closing]
    before = sum(item.memory_mb for item in staying)
    candidates = sorted(
        (item for item in staying if not item.protected and item.memory_mb > 0 and item.tab.runtime_id not in held),
        key=lambda item: (item.score, -item.memory_mb, item.window_age, item.tab.index),
    )
    total = before
    closes: list[MemoryClose] = []
    for item in candidates:
        if total <= limit_mb:
            break
        total -= item.memory_mb
        closes.append(MemoryClose(item, f"frees {format_size(item.memory_mb)} (score {item.score:+d})"))
    return MemoryPlan(limit_mb=limit_mb, before_mb=before, after_mb=total, closes=tuple(closes))


def format_size(mb: float) -> str:
    return f"{mb / 1024:.2f} GB" if mb >= 1024 else f"{mb:.0f} MB"
