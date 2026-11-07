"""Per-site title rules and the non-PR duplicate planner, from real titles seen in Chrome history."""

from __future__ import annotations

import pytest

from desktop_manager.chrome.dedupe import DedupePlan, plan_dedupe
from desktop_manager.chrome.pr_prune import apply_plans, plan_prune
from desktop_manager.chrome.render import render_site_plan
from desktop_manager.chrome.site_rules import identify
from rig.github_prs import Resolution
from tests.desktop_manager.fake_chrome import FakeChrome
from tests.desktop_manager.test_chrome_pr_prune import SEP, Browser, pr_title

pytestmark = pytest.mark.ci


def page(title: str, **flags: object) -> dict[str, object]:
    return {"title": title, **flags}


def closed_titles(plan: DedupePlan) -> list[str]:
    return [close.item.tab.title for close in plan.closes]


# -- rules ---------------------------------------------------------------------


@pytest.mark.parametrize(
    ("title", "site", "key"),
    [
        ("Reclaim CI throughput : PROJ-3425", "youtrack", "youtrack:PROJ-3425"),
        ("[RA-4078] Royalty Shipments missing sourcename - JIRA", "jira", "jira:RA-4078"),
        (f"Flaky build · Issue #12 {SEP} acme-corp/playbook", "github-issue", "gh-issue:acme-corp/playbook#12"),
        (
            f"data-platform/README.md at main {SEP} acme-corp/data-platform",
            "github-file",
            "gh-blob:acme-corp/data-platform@main:data-platform/README.md",
        ),
        (
            f"Pull requests {SEP} acme-corp/data-platform",
            "github-list",
            "gh-list:acme-corp/data-platform:Pull requests",
        ),
        (
            "Inbox (13) - someone@example.com - example.com Mail",
            "gmail",
            "gmail:someone@example.com:Inbox",
        ),
        (
            "example.com – Calendar - Week of 21 September 2026",
            "google-calendar",
            "gcal:example.com:Week of 21 September 2026",
        ),
        ("Leadership Ladder - Google Sheets", "google-docs", "gdoc:Sheets:Leadership Ladder"),
        ("Work - Google Drive", "google-drive", "gdrive:Work"),
        ("● SharedResources - Asana", "asana", "asana:SharedResources"),
        (
            "acme-live-cluster - Database Details | Aurora and RDS | eu-west-1",
            "aws",
            "aws:eu-west-1:Aurora and RDS:acme-live-cluster",
        ),
        ("NuGet Gallery | MySql.Data 26.7.0", "nuget", "nuget:MySql.Data 26.7.0"),
        ("Rights App CI Clock - Claude", "claude", "title:Rights App CI Clock"),
        ("Rights App CI Clock", "title", "title:Rights App CI Clock"),
        ("Merge Queue Handbook", "title", "title:Merge Queue Handbook"),
    ],
)
def test_rules_key_real_titles(title: str, site: str, key: str) -> None:
    identity = identify(title)
    assert identity is not None
    assert (identity.site, identity.key) == (site, key)


@pytest.mark.parametrize(
    "title",
    [
        "Claude",
        "Claude Code",
        "New chat - Claude",
        "New task - Claude",
        "Artifact - Claude",
        "Page not found – Claude",
        "Sign in - Claude",
        f"Page not found {SEP} GitHub {SEP} GitHub",
        "Google Meet",
        "Meet – Data & Platform Standup - Daily",
        "My Drive - Google Drive",
        "Home - Google Drive",
        "parquet encodings - Google Search",
        "Vercel",
        "Dashboard | Aurora and RDS | eu-west-1",
        "CodeDeploy - AWS Developer Tools",
        "New Songs - RightsApp Administration",
        "Runtime Error",
        "Datalake schema",
        "https://github.com/acme-corp/data-platform/pull/778",
        "",
    ],
)
def test_ambiguous_titles_are_never_keyed(title: str) -> None:
    assert identify(title) is None


def test_one_word_titles_and_actions_runs_only_report() -> None:
    for title in ("Checkly", f"CI {SEP} acme-corp/playbook@1a2b3c4"):
        identity = identify(title)
        assert identity is not None
        assert not identity.auto


# -- planner -------------------------------------------------------------------


def test_duplicate_artifacts_close_down_to_the_active_tab() -> None:
    browser = Browser()
    window = browser.window(
        page("Merge Queue Handbook", loaded=True),
        page("Merge Queue Handbook"),
        page("Merge Queue Handbook", selected=True),
        page("One Writer, One Code"),
        page("One Writer, One Code", loaded=True),
    )
    plan = plan_dedupe(browser.windows)
    closed = {close.item.tab.runtime_id for close in plan.closes}
    kept = {item.tab.runtime_id for item in plan.kept}
    assert kept == {window.tabs[2].runtime_id, window.tabs[4].runtime_id}
    assert closed == {window.tabs[i].runtime_id for i in (0, 1, 3)}


def test_duplicates_are_found_across_windows_of_one_profile_only() -> None:
    browser = Browser()
    title = "example.com – Calendar - Week of 21 September 2026"
    browser.window(page(title))
    browser.window(page(title))
    browser.window(page(title), profile="Default")
    plan = plan_dedupe(browser.windows)
    assert len(plan.closes) == 1
    assert plan.closes[0].reason == "duplicate of tab 1 in window 1"


def test_gmail_unread_count_does_not_split_a_duplicate() -> None:
    browser = Browser()
    browser.window(
        page("Inbox (13) - someone@example.com - example.com Mail", selected=True),
        page("Inbox (2) - someone@example.com - example.com Mail"),
    )
    assert closed_titles(plan_dedupe(browser.windows)) == ["Inbox (2) - someone@example.com - example.com Mail"]


def test_claude_suffix_variant_is_reported_while_exact_copies_close() -> None:
    browser = Browser()
    browser.window(
        page("Acme Review Queue", selected=True),
        page("Acme Review Queue"),
        page("Acme Review Queue - Claude"),
        page("Acme Review Queue - Claude"),
    )
    plan = plan_dedupe(browser.windows)
    # One exact copy of each variant closes; the surviving suffixed tab (maybe a cowork session) is only reported.
    assert closed_titles(plan) == ["Acme Review Queue", "Acme Review Queue - Claude"]
    assert [(r.kind, r.item.tab.title) for r in plan.reports] == [("variant", "Acme Review Queue - Claude")]


def test_same_title_on_different_urls_is_only_reported() -> None:
    browser = Browser()
    browser.window(
        page("Rig / Triage", selected=True, url="http://127.0.0.1:8094/triage/issues/resultful/resultful/5"),
        page("Rig / Triage", url="http://127.0.0.1:8094/triage/inbox/rtkelly13/rig/397"),
    )
    plan = plan_dedupe(browser.windows)
    assert closed_titles(plan) == []
    assert [r.kind for r in plan.reports] == ["other_page"]


def test_query_and_fragment_do_not_split_a_duplicate() -> None:
    browser = Browser()
    browser.window(
        page(
            f"Pull requests {SEP} rtkelly13/rig",
            selected=True,
            url="https://github.com/rtkelly13/rig/pulls?q=is%3Aopen",
        ),
        page(f"Pull requests {SEP} rtkelly13/rig", url="https://github.com/rtkelly13/rig/pulls/#top"),
    )
    assert closed_titles(plan_dedupe(browser.windows)) == [f"Pull requests {SEP} rtkelly13/rig"]


def test_one_unknown_url_leaves_the_title_to_decide() -> None:
    browser = Browser()
    browser.window(
        page("Merge Queue Handbook", selected=True, url="https://example.com/a"),
        page("Merge Queue Handbook"),
    )
    assert closed_titles(plan_dedupe(browser.windows)) == ["Merge Queue Handbook"]


def test_pinned_audible_and_never_titles_are_not_closed() -> None:
    browser = Browser()
    browser.window(
        page("Merge Queue Handbook"),
        page("Merge Queue Handbook", pinned=True),
        page("Link Certainty Carve-out"),
        page("Link Certainty Carve-out", audible=True),
        page("Claude Code"),
        page("Claude Code"),
        page("Google Meet"),
        page("Google Meet"),
    )
    plan = plan_dedupe(browser.windows)
    assert plan.closes == ()
    assert sorted(r.kind for r in plan.reports) == ["protected", "protected"]


def test_pr_tabs_are_left_to_the_pr_planner() -> None:
    browser = Browser()
    browser.window(page(pr_title(778)), page(pr_title(778)))
    assert plan_dedupe(browser.windows) == DedupePlan((), (), ())


def test_both_plans_apply_in_one_pass_with_one_selection_restore() -> None:
    browser = Browser()
    window = browser.window(
        page(pr_title(744)), page("Merge Queue Handbook"), page("Merge Queue Handbook", selected=True)
    )
    pr_plan = plan_prune(browser.windows, Resolution({}, []))
    site_plan = plan_dedupe(browser.windows)
    actions = FakeChrome()
    applied = apply_plans([pr_plan, site_plan], browser.windows, actions)
    assert [t.runtime_id for t in actions.closed] == [window.tabs[1].runtime_id]
    assert [a.planned.item.label for a in applied] == ["title:Merge Queue Handbook"]
    assert len(actions.restored) == 1


def test_render_site_plan_groups_closes_then_reports() -> None:
    browser = Browser()
    browser.window(page("X Handbook", selected=True), page("X Handbook"), page("X Handbook - Claude"))
    lines = render_site_plan(plan_dedupe(browser.windows))
    headings = [line.split(" (")[0] for line in lines if not line.startswith(" ")]
    assert headings == [
        "Close: duplicate pages",
        "Report only: title variant of a kept tab",
        "1 to close, 1 duplicated pages kept, 1 reported",
    ]
