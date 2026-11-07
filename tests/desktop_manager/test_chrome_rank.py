"""Tab ranking, call protection and the memory budget, without a browser."""

from __future__ import annotations

import pytest

from desktop_manager.chrome.rank import OVERSIZED_LINES, RankedTab, plan_memory, rank_tabs
from desktop_manager.chrome.render import render_memory_plan
from desktop_manager.chrome.site_rules import identify, is_call
from desktop_manager.chrome.tabs import parse_tab_name
from rig.github_prs import PrDetails, PrRef, PrState, Resolution
from tests.desktop_manager.test_chrome_pr_prune import REPO, Browser, pr_title

pytestmark = pytest.mark.ci


def page(title: str, **flags: object) -> dict[str, object]:
    return {"title": title, "loaded": "mb" in flags, **flags}


def by_title(ranked: list[RankedTab]) -> dict[str, RankedTab]:
    return {item.tab.title: item for item in ranked}


def pr(number: int) -> PrRef:
    owner, repo = REPO.split("/")
    return PrRef(owner, repo, number)


# -- calls ---------------------------------------------------------------------


@pytest.mark.parametrize(
    "title",
    [
        "Meet – Registrations Standup",
        "Meet - Risk log review",
        "Google Meet",
        "Meet",
        "Zoom Meeting",
        "Weekly sync | Microsoft Teams",
        "Huddle: #data-platform - Acme - Slack",
        "Jitsi Meet",
    ],
)
def test_call_pages_are_calls_and_never_deduped(title: str) -> None:
    assert is_call(title)
    assert identify(title) is None


def test_ordinary_pages_are_not_calls() -> None:
    assert not is_call("Meeting notes - Google Docs")
    assert not is_call("Merge Queue Handbook")


def test_memory_is_read_from_the_tab_name() -> None:
    assert parse_tab_name("Inbox - Memory usage - 1,204 MB").memory_mb == 1204
    assert parse_tab_name("Docs - High memory usage - 1.5 GB").memory_mb == 1536
    assert parse_tab_name("Discarded").memory_mb is None


# -- ranking -------------------------------------------------------------------


def test_protection_comes_first_and_calls_are_protected_when_silent() -> None:
    browser = Browser()
    browser.window(
        page("Meet – Registrations Standup", mb=700),
        page("Rights App CI Clock", mb=300, pinned=True),
        page("Claude Code", mb=900),
        page("Merge Queue Handbook", mb=200, selected=True),
        page("Bulk Job Docs Caching", mb=400),
    )
    ranked = by_title(rank_tabs(browser.windows))
    assert ranked["Meet – Registrations Standup"].protected == "video call"
    assert ranked["Rights App CI Clock"].protected == "pinned"
    assert ranked["Claude Code"].protected == "unidentified page"
    assert ranked["Merge Queue Handbook"].protected == "active tab"
    assert ranked["Bulk Job Docs Caching"].protected == ""
    assert rank_tabs(browser.windows)[-1].tab.title == "Bulk Job Docs Caching"


def test_pr_signals_order_the_pr_tabs() -> None:
    browser = Browser()
    browser.window(
        page(pr_title(1)), page(pr_title(2)), page(pr_title(3)), page(pr_title(4)), page(pr_title(5)), page(pr_title(6))
    )
    states = {
        pr(1): PrState("OPEN", details=PrDetails(review_requested=True)),
        pr(2): PrState("OPEN", details=PrDetails(authored_by_viewer=True, checks="FAILURE")),
        pr(3): PrState("OPEN", details=PrDetails(review_decision="APPROVED")),
        pr(4): PrState("OPEN", details=PrDetails(draft=True, additions=OVERSIZED_LINES, deletions=1)),
        pr(5): PrState("MERGED", details=PrDetails()),
        pr(6): PrState("OPEN", details=PrDetails()),
    }
    ranked = rank_tabs(browser.windows, Resolution(states, []))
    assert [(item.pr.number if item.pr else 0, item.score) for item in ranked] == [
        (1, 3),
        (2, 3),
        (6, 0),
        (3, -1),
        (4, -1),
        (5, -2),
    ]
    oversized = next(item for item in ranked if item.pr == pr(4))
    assert oversized.flags == (f"oversized: +{OVERSIZED_LINES}/-1 in 0 files",)
    assert next(item for item in ranked if item.pr == pr(2)).reasons == ("+2 your PR", "+1 checks failing")


def test_work_items_rank_up_and_minimised_windows_rank_down() -> None:
    browser = Browser()
    browser.window(page("Reclaim CI throughput : PROJ-3425"), page("Merge Queue Handbook"))
    browser.window(page("Bulk Job Docs Caching"), minimized=True)
    scores = {title: item.score for title, item in by_title(rank_tabs(browser.windows)).items()}
    assert scores == {"Reclaim CI throughput : PROJ-3425": 1, "Merge Queue Handbook": 0, "Bulk Job Docs Caching": -1}


# -- memory budget -------------------------------------------------------------


def test_memory_budget_closes_lowest_score_then_biggest_until_it_fits() -> None:
    browser = Browser()
    browser.window(
        page("Meet – Standup", mb=2000),
        page("Reclaim CI throughput : PROJ-3425", mb=900),
        page("Merge Queue Handbook", mb=300),
        page("Bulk Job Docs Caching", mb=800),
        page("Link Certainty Carve-out", mb=100),
    )
    plan = plan_memory(rank_tabs(browser.windows), 3000)
    assert [close.item.tab.title for close in plan.closes] == ["Bulk Job Docs Caching", "Merge Queue Handbook"]
    assert (plan.before_mb, plan.after_mb, plan.over_budget) == (4100, 3000, False)


def test_memory_budget_skips_tabs_other_plans_already_close() -> None:
    browser = Browser()
    window = browser.window(page("Merge Queue Handbook", mb=800), page("Bulk Job Docs Caching", mb=500))
    plan = plan_memory(rank_tabs(browser.windows), 600, already_closing={window.tabs[0].runtime_id})
    assert plan.before_mb == 500
    assert plan.closes == ()


def test_memory_budget_counts_but_never_closes_held_tabs() -> None:
    browser = Browser()
    window = browser.window(page("Rights App CI Clock - Claude", mb=900), page("Merge Queue Handbook", mb=100))
    plan = plan_memory(rank_tabs(browser.windows), 500, held={window.tabs[0].runtime_id})
    assert [close.item.tab.title for close in plan.closes] == ["Merge Queue Handbook"]
    assert plan.over_budget


def test_memory_budget_never_touches_protected_tabs_and_says_so() -> None:
    browser = Browser()
    browser.window(page("Meet – Standup", mb=3000), page("Merge Queue Handbook", mb=500, audible=True))
    plan = plan_memory(rank_tabs(browser.windows), 1024)
    assert plan.closes == ()
    assert plan.over_budget
    assert render_memory_plan(plan) == [
        "Memory: 3.42 GB -> 3.42 GB of 1.00 GB, still over budget: every remaining tab is protected or held for review"
    ]
