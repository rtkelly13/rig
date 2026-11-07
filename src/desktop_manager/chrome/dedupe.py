"""Decide which non-PR duplicate tabs to close, using the per-site title rules.

`plan_dedupe` is pure, like `pr_prune.plan_prune`, and shares its keeper choice,
guards and executor. Per Chrome profile, tabs whose titles `site_rules.identify`
maps to one key are one page: the kept tab is the active one, then a loaded one,
then the leftmost in the oldest window, and the rest close. What is never
closed:

- tabs no rule keys (`site_rules.NEVER`: shells, sign-in pages, live calls...);
- a title variant under an `exact_only` rule (`X - Claude` beside `X`) - reported;
- any match of a report-only rule (one-word titles, Actions runs) - reported;
- a title match whose known URLs name different pages (same title, other
  document) - reported. Without a URL on both tabs, the title decides;
- pinned and audible tabs - reported;
- GitHub PR tabs, which `pr_prune` owns.
"""

from __future__ import annotations

from collections import defaultdict
from collections.abc import Sequence
from dataclasses import dataclass
from typing import Literal
from urllib.parse import urlsplit

from desktop_manager.chrome.pr_prune import activations, describe, keeper_preference, pr_ref_from_title
from desktop_manager.chrome.site_rules import TabIdentity, identify
from desktop_manager.chrome.tabs import ChromeTab, ChromeWindow


@dataclass(slots=True, frozen=True, order=True)
class _PageKey:
    """One page within one profile; ordered so profiles are planned in a stable order."""

    profile: str
    key: str


SiteReportKind = Literal["variant", "report_only", "protected", "other_page"]


@dataclass(frozen=True)
class SiteTab:
    tab: ChromeTab
    identity: TabIdentity
    profile: str
    window_age: int
    window_number: int

    @property
    def label(self) -> str:
        return self.identity.key


@dataclass(frozen=True)
class SiteClose:
    item: SiteTab
    reason: str
    keeper: ChromeTab | None = None


@dataclass(frozen=True)
class SiteReport:
    item: SiteTab
    kind: SiteReportKind
    detail: str


@dataclass(frozen=True)
class DedupePlan:
    closes: tuple[SiteClose, ...]
    reports: tuple[SiteReport, ...]
    #: One kept tab per duplicated key (singletons are not listed).
    kept: tuple[SiteTab, ...]

    def activations(self) -> dict[int, ChromeTab]:
        return activations(self.closes)


def site_tabs(windows: Sequence[ChromeWindow]) -> list[SiteTab]:
    """Every keyed, non-PR tab, oldest window first and left to right."""
    numbering: dict[str, int] = defaultdict(int)
    found: list[SiteTab] = []
    for window in sorted(windows, key=lambda window: (window.age_rank, window.hwnd)):
        numbering[window.profile_key] += 1
        for tab in window.tabs:
            if pr_ref_from_title(tab.title) is not None:
                continue
            identity = identify(tab.title)
            if identity is not None:
                found.append(SiteTab(tab, identity, window.profile_key, window.age_rank, numbering[window.profile_key]))
    return found


def plan_dedupe(windows: Sequence[ChromeWindow]) -> DedupePlan:
    """Apply the site rules to every profile's tabs. Pure; nothing is closed here."""
    groups: dict[_PageKey, list[SiteTab]] = defaultdict(list)
    for item in site_tabs(windows):
        groups[_PageKey(item.profile, item.identity.key)].append(item)

    closes: list[SiteClose] = []
    reports: list[SiteReport] = []
    kept: list[SiteTab] = []
    for group_key in sorted(groups):
        group = groups[group_key]
        if len(group) < 2:
            continue
        keeper = min(group, key=keeper_preference)
        kept.append(keeper)
        # An exact-only key closes within each exact title; across titles it only reports.
        variants: dict[str, list[SiteTab]] = defaultdict(list)
        for item in group:
            variants[item.tab.title if item.identity.exact_only else ""].append(item)
        for members in variants.values():
            local = keeper if keeper in members else min(members, key=keeper_preference)
            if local is not keeper:
                reports.append(SiteReport(local, "variant", f"possibly the same page as tab {describe(keeper)}"))
            for item in members:
                if item is not local:
                    _decide(item, local, closes, reports)
    return DedupePlan(closes=tuple(closes), reports=tuple(reports), kept=tuple(kept))


def _decide(item: SiteTab, keeper: SiteTab, closes: list[SiteClose], reports: list[SiteReport]) -> None:
    where = describe(keeper)
    if not _same_page(item.tab.url, keeper.tab.url):
        reports.append(SiteReport(item, "other_page", f"same title as tab {where}, different URL"))
    elif not item.identity.auto:
        reports.append(SiteReport(item, "report_only", f"same {item.identity.site} key as tab {where}"))
    elif item.tab.pinned or item.tab.audible:
        guard = "pinned" if item.tab.pinned else "audible"
        reports.append(SiteReport(item, "protected", f"{guard}; would close as duplicate of tab {where}"))
    else:
        closes.append(SiteClose(item, f"duplicate of tab {where}", keeper=keeper.tab))


def _same_page(url: str | None, other: str | None) -> bool:
    """Whether two URLs can name one page: the same scheme, host and path.

    The query and fragment are views of a page (a filter, an anchor), so they do
    not split a duplicate. An unknown URL leaves the decision to the title.
    """
    if url is None or other is None:
        return True
    first, second = urlsplit(url), urlsplit(other)
    return (first.scheme, first.netloc, first.path.rstrip("/")) == (
        second.scheme,
        second.netloc,
        second.path.rstrip("/"),
    )
