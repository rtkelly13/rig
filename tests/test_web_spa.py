"""Browser contracts for URL navigation and the inbox's durable attention states."""

from __future__ import annotations

import re
import threading
from collections.abc import Iterator
from contextlib import closing
from dataclasses import dataclass, replace
from pathlib import Path
from typing import TypedDict, cast

import pytest
from playwright.sync_api import Page, expect, sync_playwright

from rig.connectivity import web as connectivity_web
from rig.connectivity.models import Component, ComponentEdge, Dependency, Graph, Project
from rig.triage import github, service, store
from rig.triage.facts import Check, Facts, Related, Stack, StackMember
from rig.triage.models import Activity, Interaction, Item
from rig.web_host.server import StudioServer

pytestmark = pytest.mark.ci


@dataclass(frozen=True, slots=True)
class BrowserApp:
    url: str
    db: Path
    page: Page


@pytest.fixture
def browser_app(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> Iterator[BrowserApp]:
    graph = Graph(
        [
            Project("owner/repo", "repo", "Shared library", "Libraries", "Python", True, True, 1, ["managed-library"]),
            Project("owner/other", "other", "Application", "Apps", "TypeScript", True, True, 1, []),
        ],
        [Dependency("owner/repo", "owner/other", "npm", "managed-library", "^1.0", "runtime", "package.json")],
        [Component("owner/repo", ["owner/repo"], False), Component("owner/other", ["owner/other"], False)],
        [ComponentEdge("owner/repo", "owner/other")],
        "2026-01-01T00:00:00Z",
        [],
    )

    def load_graph(*, refresh: bool = False) -> Graph:
        del refresh
        return graph

    monkeypatch.setattr(connectivity_web, "load_graph", load_graph)
    db = tmp_path / "rig.db"
    monkeypatch.setattr(service, "queue_path", lambda: tmp_path / "queue.md")
    monkeypatch.setattr(github, "observe", lambda item: item)
    monkeypatch.setattr(github, "activity", lambda _item: [])
    items = [
        Item(
            "owner/repo",
            1,
            "pr",
            "Fix the first issue",
            "OPEN",
            "2026-01-01T00:00:00Z",
            "2026-01-02T00:00:00Z",
            "a" * 40,
            1,
            0,
            6,
            body=(
                "## Why this change\n\nRestore the first workflow.\n\n"
                "- Keep the context\n- Preserve drafts\n\n<script>window.injected = true</script>"
            ),
        ),
        Item(
            "owner/repo",
            2,
            "pr",
            "<script> must stay text",
            "OPEN",
            "2026-01-02T00:00:00Z",
            "2026-01-02T00:00:00Z",
            "b" * 40,
            2,
            1,
            40,
        ),
        Item("owner/other", 3, "issue", "Triage the bug", "OPEN", "2026-01-03T00:00:00Z", "2026-01-03T00:00:00Z"),
    ]
    with closing(store.connect(db)) as conn, conn:
        store.save(conn, items)
    server = StudioServer(port=0, db_path=db, assets_root=tmp_path / "assets")
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    with sync_playwright() as playwright:
        browser = playwright.chromium.launch()
        page = browser.new_page(viewport={"width": 1440, "height": 1000})
        try:
            yield BrowserApp(f"http://127.0.0.1:{server.server_address[1]}", db, page)
        finally:
            browser.close()
            server.shutdown()
            server.server_close()
            thread.join()


def test_deep_links_filters_reload_and_history_are_one_document(browser_app: BrowserApp) -> None:
    page, url = browser_app.page, browser_app.url
    page.goto(url + "/triage/inbox/owner/repo/1?repo=owner%2Frepo")
    expect(page.get_by_role("heading", name="Fix the first issue", exact=True)).to_be_visible()
    page.evaluate("window.rigDocumentSentinel = 42")
    page.get_by_role("button", name="Rig tools", exact=True).click()
    page.get_by_role("dialog", name="Rig tools").get_by_role("link", name="UI references", exact=True).click()
    expect(page).to_have_url(url + "/triage/references")
    expect(page.get_by_role("heading", name="Triage interface catalogue")).to_be_visible()
    assert page.evaluate("window.rigDocumentSentinel") == 42
    page.go_back()
    expect(page.get_by_role("heading", name="Fix the first issue", exact=True)).to_be_visible()
    page.get_by_role("button", name="Queue filters", exact=True).click()
    expect(page.get_by_role("combobox", name="Repository", exact=True)).to_have_text("owner/repo")
    page.keyboard.press("Escape")
    page.get_by_role("textbox", name="Search queue").fill("first")
    expect(page).to_have_url(url + "/triage/inbox/owner/repo/1?repo=owner%2Frepo&q=first")
    page.reload()
    expect(page.get_by_role("textbox", name="Search queue")).to_have_value("first")
    expect(page.get_by_role("heading", name="Fix the first issue", exact=True)).to_be_visible()
    page.evaluate("window.rigDocumentSentinel = 43")
    page.get_by_role("button", name="Rig tools", exact=True).click()
    page.get_by_role("dialog", name="Rig tools", exact=True).get_by_role("link", name="Estate", exact=True).click()
    expect(page.get_by_role("heading", name="Estate Managed Repositories")).to_be_visible()
    assert page.evaluate("window.rigDocumentSentinel") == 43
    page.go_back()
    expect(page.get_by_role("heading", name="Fix the first issue", exact=True)).to_be_visible()


def test_archive_read_snooze_and_head_change_restore_attention(browser_app: BrowserApp) -> None:
    page, url = browser_app.page, browser_app.url
    page.goto(url + "/triage/inbox/owner/repo/1")
    expect(page.get_by_role("button", name="Archive", exact=True)).to_be_enabled()
    expect(page.get_by_role("button", name="Mark unread", exact=True)).to_be_enabled()
    page.get_by_role("button", name="Mark unread", exact=True).click()
    expect(page.get_by_role("button", name="Mark read", exact=True)).to_be_enabled()
    page.get_by_role("button", name="Mark read", exact=True).click()
    page.get_by_role("button", name="Archive", exact=True).click()
    expect(page.get_by_role("button", name="Move to inbox", exact=True)).to_be_visible()
    assert next(entry for entry in store.read_entries(browser_app.db) if entry.item.number == 1).archived
    page.get_by_role("link", name="Done", exact=True).click()
    expect(page.locator(".queue-row")).to_have_count(1)
    page.locator(".queue-row").click()
    page.get_by_role("button", name="Move to inbox", exact=True).click()
    expect(page.get_by_role("button", name="Archive", exact=True)).to_be_visible()
    page.get_by_role("button", name="Snooze", exact=True).first.click()
    expect(page.get_by_role("button", name="Resume", exact=True).first).to_be_visible()
    page.get_by_role("link", name="Snoozed", exact=True).click()
    expect(page.locator(".queue-row")).to_have_count(1)
    page.locator(".queue-row").click()
    page.get_by_role("button", name="Resume", exact=True).first.click()
    page.get_by_role("button", name=re.compile(r"^reviewed$", re.IGNORECASE)).click()
    expect(page.get_by_role("heading", name="Waiting for the next change")).to_be_visible()
    page.get_by_role("link", name="Waiting", exact=True).click()
    expect(page.locator(".queue-row")).to_have_count(1)
    with closing(store.connect(browser_app.db)) as conn, conn:
        item = next(entry.item for entry in store.entries(conn) if entry.item.number == 1)
        store.save(conn, [replace(item, head="c" * 40)])
    page.goto(url + "/triage/inbox/owner/repo/1")
    expect(page.get_by_role("heading", name="A new commit needs your review")).to_be_visible()
    expect(page.locator(".queue-row")).to_have_count(2)


def test_drafts_survive_navigation_keyboard_and_mobile_layout(browser_app: BrowserApp) -> None:
    page, url = browser_app.page, browser_app.url
    page.goto(url + "/triage/inbox/owner/repo/1")
    notes = page.get_by_role("textbox", name="Review notes", exact=True)
    notes.fill("Keep this draft while inspecting another PR")
    notes.press("j")
    expect(page).to_have_url(url + "/triage/inbox/owner/repo/1")
    notes.fill("Draft retained")
    page.locator("h1").click()
    page.keyboard.press("j")
    expect(page.get_by_role("heading", name="<script> must stay text", exact=True)).to_be_visible()
    page.keyboard.press("k")
    expect(notes).to_have_value("Draft retained")
    page.reload()
    expect(notes).to_have_value("Draft retained")
    page.set_viewport_size({"width": 390, "height": 844})
    expect(page.get_by_role("heading", name="Fix the first issue", exact=True)).to_be_visible()
    assert page.evaluate("document.documentElement.scrollWidth <= innerWidth")
    assert page.locator(".detail-pane").evaluate("element => element.scrollWidth <= element.clientWidth")
    page.get_by_role("button", name="Rig tools", exact=True).click()
    page.get_by_role("dialog", name="Rig tools").get_by_role("link", name="UI references", exact=True).click()
    expect(page.get_by_role("heading", name="Triage interface catalogue")).to_be_visible()
    assert page.evaluate("document.documentElement.scrollWidth <= innerWidth")


def test_workspace_layout_controls_and_external_links(browser_app: BrowserApp) -> None:
    page, url = browser_app.page, browser_app.url
    page.goto(url + "/triage/inbox/owner/repo/1")
    expect(page.get_by_role("heading", name="Fix the first issue", exact=True)).to_be_visible()
    title = page.locator("#selected-title a")
    expect(title).to_have_attribute("href", "https://github.com/owner/repo/pull/1")
    expect(title).to_have_attribute("target", "_blank")
    expect(page.get_by_role("link", name="Open PR on GitHub", exact=True)).to_have_attribute(
        "href", "https://github.com/owner/repo/pull/1"
    )
    page.get_by_role("button", name="Expand sidebar", exact=True).click()
    initial_width = page.locator(".detail-pane").bounding_box()
    assert initial_width
    page.get_by_role("button", name="Collapse sidebar", exact=True).click()
    expect(page.get_by_role("button", name="Expand sidebar", exact=True)).to_have_attribute("aria-expanded", "false")
    expanded_width = page.locator(".detail-pane").bounding_box()
    assert expanded_width and expanded_width["width"] > initial_width["width"]
    separator = page.get_by_role("separator", name="Resize item list")
    separator.focus()
    page.keyboard.press("Home")
    expect(separator).to_have_attribute("aria-valuenow", "260")
    wider = page.locator(".detail-pane").bounding_box()
    assert wider and wider["width"] > expanded_width["width"]
    handle = separator.bounding_box()
    assert handle
    page.mouse.move(handle["x"] + handle["width"] / 2, handle["y"] + 30)
    page.mouse.down()
    page.mouse.move(handle["x"] + 100, handle["y"] + 30)
    page.mouse.up()
    expect(separator).not_to_have_attribute("aria-valuenow", "260")
    width_after_drag = separator.get_attribute("aria-valuenow")
    assert width_after_drag and int(width_after_drag) > 260
    page.reload()
    expect(separator).to_have_attribute("aria-valuenow", width_after_drag)
    expect(page.get_by_role("button", name="Expand sidebar", exact=True)).to_be_visible()
    page.get_by_role("button", name="Rig tools", exact=True).click()
    expect(page.get_by_role("dialog", name="Rig tools", exact=True)).to_be_visible()
    page.keyboard.press("j")
    expect(page).to_have_url(url + "/triage/inbox/owner/repo/1")
    page.keyboard.press("Escape")
    expect(page.get_by_role("dialog", name="Rig tools", exact=True)).not_to_be_visible()
    expect(page.get_by_role("button", name="Rig tools", exact=True)).to_be_focused()
    for theme in ("midnight", "sketch"):
        page.evaluate("theme => document.documentElement.dataset.theme = theme", theme)
        page.evaluate("""() => {
            const probe = document.createElement('div');
            probe.id = 'surface-probe';
            probe.style.background = 'var(--ds-surface-raised)';
            document.body.append(probe);
        }""")
        assert page.locator(".detail-pane").evaluate(
            "element => getComputedStyle(element).backgroundColor"
        ) == page.locator("#surface-probe").evaluate("element => getComputedStyle(element).backgroundColor")
        page.locator("#surface-probe").evaluate("element => element.remove()")


def test_collapsed_sidebar_labels_support_hover_focus_and_responsive_rail(browser_app: BrowserApp) -> None:
    page, url = browser_app.page, browser_app.url
    page.goto(url + "/triage/inbox/owner/repo/1")
    inbox = page.get_by_role("navigation", name="Rig navigation").get_by_role("link", name="Inbox", exact=True)
    tooltip = page.locator('[data-slot="tooltip"]').filter(has_text=re.compile("^Inbox$"))
    page.get_by_role("button", name="Expand sidebar", exact=True).click()
    page.get_by_role("button", name="Collapse sidebar", exact=True).click()
    inbox.hover()
    expect(tooltip).to_be_visible()
    expect(tooltip).to_have_text("Inbox")
    bounds = tooltip.bounding_box()
    sidebar = page.locator(".app-sidebar").bounding_box()
    assert bounds and sidebar and bounds["x"] >= sidebar["width"]
    page.mouse.move(1200, 20)
    expect(tooltip).not_to_be_visible()
    inbox.focus()
    page.keyboard.press("Shift+Tab")
    page.keyboard.press("Tab")
    expect(tooltip).to_be_visible()
    page.keyboard.press("Escape")
    expect(tooltip).not_to_be_visible()
    expect(inbox).to_be_focused()
    page.get_by_role("button", name="Expand sidebar", exact=True).click()
    inbox.hover()
    expect(tooltip).to_have_count(0)
    page.set_viewport_size({"width": 820, "height": 900})
    expect(page.locator(".app-sidebar")).to_have_attribute("data-compact", "true")
    inbox.focus()
    page.keyboard.press("Shift+Tab")
    page.keyboard.press("Tab")
    expect(tooltip).to_be_visible()
    expect(tooltip).to_have_text("Inbox")
    inbox.click()
    expect(page).to_have_url(url + "/triage/inbox")


def test_review_workspace_ratios_focus_content_and_mobile_navigation(browser_app: BrowserApp) -> None:
    page, url = browser_app.page, browser_app.url
    page.goto(url + "/triage/inbox/owner/repo/1?repo=owner%2Frepo")
    expect(page.get_by_role("heading", name="Why this change")).to_be_visible()
    assert page.evaluate("window.injected === undefined")
    expect(page.get_by_role("heading", name="PR feed 0", exact=True)).to_be_visible()
    for width, target in ((1280, 908), (1440, 1048), (1920, 1488)):
        page.set_viewport_size({"width": width, "height": 1000})
        pane = page.locator(".detail-pane").bounding_box()
        assert pane and abs(pane["width"] - target) < 2
        assert pane["y"] <= 112
    page.set_viewport_size({"width": 1440, "height": 1000})
    notes = page.get_by_role("textbox", name="Review notes")
    notes.fill("Keep this while focusing")
    page.locator(".detail-pane").evaluate("element => element.scrollTop = 100")
    page.get_by_role("button", name="Focus view", exact=True).click()
    expect(page).to_have_url(url + "/triage/inbox/owner/repo/1?repo=owner%2Frepo&layout=focus")
    expect(page.locator(".queue-column")).not_to_be_visible()
    assert page.locator(".detail-pane").evaluate("element => element.scrollTop") == 100
    expect(notes).to_have_value("Keep this while focusing")
    page.reload()
    expect(page.locator(".queue-column")).not_to_be_visible()
    expect(notes).to_have_value("Keep this while focusing")
    page.get_by_role("button", name="Next item", exact=True).click()
    expect(page.get_by_role("heading", name="<script> must stay text", exact=True)).to_be_visible()
    page.get_by_role("button", name="Previous item", exact=True).click()
    expect(notes).to_have_value("Keep this while focusing")
    page.get_by_role("button", name="Split view", exact=True).click()
    expect(page.locator(".queue-column")).to_be_visible()
    page.set_viewport_size({"width": 390, "height": 844})
    expect(page.locator(".queue-column")).not_to_be_visible()
    expect(page.get_by_role("heading", name="Fix the first issue", exact=True)).to_be_visible()
    assert page.locator(".detail-pane").bounding_box()["y"] <= 112
    page.get_by_role("link", name="Back to inbox", exact=True).click()
    expect(page.locator(".queue-column")).to_be_visible()
    page.get_by_role("button", name="Queue filters", exact=True).click()
    expect(page.get_by_role("combobox", name="Repository", exact=True)).to_have_text("owner/repo")
    page.keyboard.press("Escape")
    assert page.evaluate("document.documentElement.scrollWidth <= innerWidth")


def test_queue_filters_stay_compact_and_preserve_selection(browser_app: BrowserApp) -> None:
    page, url = browser_app.page, browser_app.url
    page.set_viewport_size({"width": 1280, "height": 800})
    page.goto(url + "/triage/inbox/owner/repo/1")
    expect(page.get_by_role("heading", name="Fix the first issue", exact=True)).to_be_visible()
    column = page.locator(".queue-column").bounding_box()
    listing = page.locator(".queue-list").bounding_box()
    assert column and listing and listing["y"] - column["y"] <= 88
    assert listing["height"] >= column["height"] * 0.8
    trigger = page.get_by_role("button", name="Queue filters", exact=True)
    trigger.click()
    dialog = page.get_by_role("dialog", name="Queue filters", exact=True)
    expect(dialog).to_be_visible()
    dialog.get_by_role("combobox", name="Repository", exact=True).click()
    page.get_by_role("option", name="owner/repo", exact=True).click()
    dialog.get_by_role("combobox", name="Readiness", exact=True).click()
    page.get_by_role("option", name="Clean (1)", exact=True).click()
    expect(page).to_have_url(url + "/triage/inbox/owner/repo/1?repo=owner%2Frepo&status=0")
    page.keyboard.press("j")
    expect(page.get_by_role("heading", name="Fix the first issue", exact=True)).to_be_visible()
    dialog.get_by_role("button", name="Done", exact=True).click()
    expect(dialog).not_to_be_visible()
    expect(trigger).to_be_focused()
    expect(page.locator(".filter-summary")).to_have_text("owner/repo, Clean")
    page.reload()
    expect(page.locator(".filter-summary")).to_have_text("owner/repo, Clean")
    trigger.click()
    expect(dialog.get_by_role("combobox", name="Readiness", exact=True)).to_have_text("Clean (1)")
    dialog.get_by_role("checkbox", name="Unread only", exact=True).check()
    expect(page).to_have_url(url + "/triage/inbox/owner/repo/1?repo=owner%2Frepo&status=0&read=unread")
    page.keyboard.press("Escape")
    expect(trigger).to_be_focused()
    page.get_by_role("button", name="Clear queue filters", exact=True).click()
    expect(page).to_have_url(url + "/triage/inbox/owner/repo/1")
    page.set_viewport_size({"width": 390, "height": 844})
    page.get_by_role("link", name="Back to inbox", exact=True).click()
    trigger.click()
    expect(dialog).to_be_visible()
    bounds = dialog.bounding_box()
    assert bounds and bounds["x"] >= 0 and bounds["x"] + bounds["width"] <= 390
    assert page.evaluate("document.documentElement.scrollWidth <= innerWidth")


def test_filter_selects_keep_the_parent_open_and_fit_both_themes(browser_app: BrowserApp) -> None:
    page, url = browser_app.page, browser_app.url
    page.goto(url + "/triage/inbox/owner/repo/1")
    page.get_by_role("button", name="Queue filters", exact=True).click()
    dialog = page.get_by_role("dialog", name="Queue filters", exact=True)
    repository = dialog.get_by_role("combobox", name="Repository", exact=True)
    expect(repository).to_have_text("All repositories")
    expect(dialog.get_by_role("combobox", name="Readiness", exact=True)).to_have_text("Any readiness")
    repository.click()
    expect(page.get_by_role("listbox", name="Repository", exact=True)).to_be_visible()
    page.keyboard.press("j")
    expect(page).to_have_url(url + "/triage/inbox/owner/repo/1")
    page.keyboard.press("Escape")
    expect(page.get_by_role("listbox", name="Repository", exact=True)).not_to_be_visible()
    expect(dialog).to_be_visible()
    expect(repository).to_be_focused()
    repository.press("ArrowDown")
    expect(page.get_by_role("listbox", name="Repository", exact=True)).to_be_visible()
    expect(page.get_by_role("option", name="All repositories", exact=True)).to_be_focused()
    page.keyboard.press("End")
    expect(page.get_by_role("option", name="owner/repo", exact=True)).to_be_focused()
    expect(page.get_by_role("option", name="owner/repo", exact=True)).to_have_attribute("data-highlighted", "")
    page.keyboard.press("Enter")
    expect(repository).to_have_text("owner/repo")
    expect(dialog).to_be_visible()
    repository.click()
    page.get_by_role("option", name="All repositories", exact=True).click()
    expect(page).to_have_url(url + "/triage/inbox/owner/repo/1")
    for theme in ("midnight", "sketch"):
        page.evaluate("theme => document.documentElement.dataset.theme = theme", theme)
        repository.click()
        popup = page.locator('[data-slot="select-popup"]')
        expect(popup).to_be_visible()
        expected_background = page.evaluate("""() => {
            const probe = document.createElement('div');
            probe.style.backgroundColor = 'var(--ds-surface-raised)';
            document.body.append(probe);
            const value = getComputedStyle(probe).backgroundColor;
            probe.remove();
            return value;
        }""")
        expect(popup).to_have_css("background-color", expected_background)
        page.keyboard.press("Escape")
    page.keyboard.press("Escape")
    page.set_viewport_size({"width": 390, "height": 844})
    page.get_by_role("link", name="Back to inbox", exact=True).click()
    page.get_by_role("button", name="Queue filters", exact=True).click()
    page.get_by_role("combobox", name="Repository", exact=True).click()
    expect(page.locator('[data-slot="select-popup"]')).to_be_visible()
    bounds = page.locator('[data-slot="select-popup"]').bounding_box()
    assert bounds and bounds["x"] >= 0 and bounds["x"] + bounds["width"] <= 390
    assert page.evaluate("document.documentElement.scrollWidth <= innerWidth")


def test_stack_groups_actions_and_issue_context(browser_app: BrowserApp) -> None:
    page, url = browser_app.page, browser_app.url
    with closing(store.connect(browser_app.db)) as conn, conn:
        original = store.entries(conn)
        first = next(entry.item for entry in original if entry.item.number == 1)
        second = next(entry.item for entry in original if entry.item.number == 2)
        issue = next(entry.item for entry in original if entry.item.number == 3)
        members = [StackMember(2, second.title, "OPEN", 1), StackMember(1, first.title, "OPEN", 2)]
        store.save(
            conn,
            [
                replace(
                    first,
                    facts=Facts(
                        author="author",
                        comments=3,
                        review_requested=True,
                        review_decision="CHANGES_REQUESTED",
                        unresolved_threads=1,
                        threads_total=1,
                        checks_state="FAILURE",
                        checks=[Check("lint", "FAILURE", "https://github.com/check/1")],
                        checks_total=1,
                        stack=Stack(20, 2, 2, "main", members),
                    ),
                ),
                replace(
                    second,
                    facts=Facts(
                        checks_state="SUCCESS", review_decision="APPROVED", stack=Stack(20, 2, 1, "main", members)
                    ),
                ),
                replace(
                    issue,
                    facts=Facts(
                        labels=["bug"], milestone="v1", related=[Related("owner/repo", 1, first.title, "OPEN", "pr")]
                    ),
                ),
            ],
        )
        store.interact(conn, Interaction("owner/repo", 1, "reviewed", first.head))
        store.import_activity(
            conn,
            first,
            [
                Activity(
                    "human/1",
                    "2099-01-01T00:00:00Z",
                    "reviewer",
                    "github:commented",
                    first.head,
                    "Please explain this change.",
                )
            ],
        )
    page.goto(url + "/triage/inbox/owner/repo/1")
    group = page.get_by_role("button", name=re.compile("Stack #20"))
    expect(group).to_have_attribute("aria-expanded", "true")
    expect(page.locator(".queue-row strong").nth(0)).to_have_text(second.title)
    expect(page.locator(".queue-row strong").nth(1)).to_have_text(first.title)
    actions = page.get_by_label("Action signals")
    expect(actions.get_by_text("1 failed check", exact=True)).to_be_visible()
    expect(actions.get_by_text("Address review changes", exact=True)).to_be_visible()
    expect(actions.get_by_text("Read new replies", exact=True)).to_be_visible()
    expect(page.get_by_role("link", name="lint", exact=True)).to_have_attribute("href", "https://github.com/check/1")
    page.locator(".stack-context summary").click()
    expect(page.locator(".stack-context li")).to_have_count(2)
    expect(page.locator(".stack-context")).to_contain_text("Part 2 of 2")
    group.click()
    expect(page.locator(".queue-row")).to_have_count(0)
    expect(page.get_by_role("heading", name=first.title, exact=True)).to_be_visible()
    expect(page.get_by_role("button", name="Next item", exact=True)).to_be_disabled()
    group.click()
    page.get_by_role("button", name="Previous item", exact=True).click()
    expect(page.get_by_role("heading", name=second.title, exact=True)).to_be_visible()
    expect(page.get_by_label("Action signals").get_by_text("Checks passed", exact=True)).to_be_visible()
    page.get_by_role("button", name="Queue filters", exact=True).click()
    page.get_by_role("combobox", name="Needs action", exact=True).click()
    page.get_by_role("option", name="Failed checks", exact=True).click()
    page.get_by_role("combobox", name="Group by", exact=True).click()
    page.get_by_role("option", name="Next action", exact=True).click()
    page.get_by_role("button", name="Done", exact=True).click()
    expect(page.locator(".queue-row")).to_have_count(1)
    expect(page.locator(".queue-group-toggle")).to_contain_text("Failed checks")
    expect(page).to_have_url(url + "/triage/inbox/owner/repo/2?action=checks-failed&group=action")
    page.get_by_role("link", name="Issues", exact=True).click()
    page.locator(".queue-row").click()
    expect(page.get_by_label("Action signals").get_by_text("Assign owner", exact=True)).to_be_visible()
    expect(page.get_by_label("Action signals").get_by_text("Set priority", exact=True)).to_be_visible()
    expect(page.get_by_label("Action signals").get_by_text("Linked PR in progress", exact=True)).to_be_visible()
    page.locator(".item-context summary").click()
    expect(page.locator(".github-labels")).to_have_text("bug")
    expect(page.locator(".linked-work")).to_contain_text("owner/repo#1")


@pytest.mark.parametrize("relationship,group_count", [("chain", 1), ("cycle", 0), ("fork", 0)])
def test_branch_chains_are_explicitly_inferred_and_ignore_cycles_and_forks(
    browser_app: BrowserApp, relationship: str, group_count: int
) -> None:
    page, url = browser_app.page, browser_app.url
    with closing(store.connect(browser_app.db)) as conn, conn:
        entries = store.entries(conn)
        first = next(entry.item for entry in entries if entry.item.number == 1)
        second = next(entry.item for entry in entries if entry.item.number == 2)
        store.save(
            conn,
            [
                replace(
                    first, facts=Facts(head="feature-top", base="feature-base", cross_repository=relationship == "fork")
                ),
                replace(
                    second, facts=Facts(head="feature-base", base="feature-top" if relationship == "cycle" else "main")
                ),
            ],
        )
    page.goto(url + "/triage/inbox/owner/repo/1")
    expect(page.get_by_role("heading", name=first.title, exact=True)).to_be_visible()
    expect(page.locator(".queue-group-toggle")).to_have_count(group_count)
    if group_count:
        expect(page.locator(".queue-group-description")).to_contain_text("inferred from base branches")
        expect(page.locator(".queue-row strong").nth(0)).to_have_text(second.title)
        page.locator(".stack-context summary").click()
        expect(page.locator(".stack-context")).to_contain_text("not a registered GitHub stack")
    page.get_by_role("button", name="Queue filters", exact=True).click()
    page.get_by_role("combobox", name="Group by", exact=True).click()
    page.get_by_role("option", name="Repository", exact=True).click()
    page.get_by_role("button", name="Done", exact=True).click()
    expect(page.locator(".queue-group-toggle")).to_have_count(1)
    expect(page.locator(".queue-group-toggle")).to_contain_text("owner/repo")


def test_unfetched_metadata_is_not_presented_as_passing_or_missing_ownership(browser_app: BrowserApp) -> None:
    page = browser_app.page
    page.goto(browser_app.url + "/triage/inbox/owner/repo/1")
    signals = page.get_by_label("Action signals")
    expect(signals.get_by_text("Signals not fetched", exact=True)).to_be_visible()
    expect(signals.locator('[data-tone="success"]')).to_have_count(0)
    page.get_by_role("link", name="Issues", exact=True).click()
    page.locator(".queue-row").click()
    expect(signals.get_by_text("Signals not fetched", exact=True)).to_be_visible()
    expect(signals.get_by_text("Assign owner", exact=True)).to_have_count(0)


def test_inbox_shell_stays_fixed_while_only_panels_scroll(browser_app: BrowserApp) -> None:
    page, url = browser_app.page, browser_app.url
    with closing(store.connect(browser_app.db)) as conn, conn:
        first = next(entry.item for entry in store.entries(conn) if entry.item.number == 1)
        store.save(
            conn,
            [
                replace(
                    first, body="\n\n".join(f"Paragraph {number}. Context for the review." for number in range(80))
                ),
                *(replace(first, number=number, title=f"Queue item {number}") for number in range(4, 35)),
            ],
        )
    page.set_viewport_size({"width": 1280, "height": 720})
    page.goto(url + "/triage/inbox/owner/repo/1")
    expect(page.get_by_role("heading", name="Fix the first issue", exact=True)).to_be_visible()
    toolbar = page.locator(".workspace-toolbar").bounding_box()
    assert toolbar
    pane = page.locator(".detail-pane")
    pane.hover()
    page.mouse.wheel(0, 600)
    expect(pane).to_have_js_property("scrollTop", 600)
    page.locator(".queue-list").hover()
    page.mouse.wheel(0, 600)
    expect(page.locator(".queue-list")).to_have_js_property("scrollTop", 600)
    page.locator(".workspace-toolbar").hover()
    page.mouse.wheel(0, 2000)
    assert page.locator(".workspace-toolbar").bounding_box() == toolbar
    for selector in (".app-content", ".triage-page"):
        page.locator(selector).evaluate("element => element.scrollTop = 200")
        expect(page.locator(selector)).to_have_js_property("scrollTop", 0)
    page.get_by_role("button", name="Focus view", exact=True).click()
    expect(pane).to_have_js_property("scrollTop", 600)
    assert page.locator(".workspace-toolbar").bounding_box() == toolbar


def test_description_and_feed_are_separate_and_links_resolve_to_github(browser_app: BrowserApp) -> None:
    page, url = browser_app.page, browser_app.url
    with closing(store.connect(browser_app.db)) as conn, conn:
        first = next(entry.item for entry in store.entries(conn) if entry.item.number == 1)
        store.save(
            conn,
            [
                replace(
                    first,
                    body=(
                        "[Related issue](/owner/repo/issues/2)\n\n"
                        "![Diagram](/owner/repo/raw/main/a.png)\n\n[Unsafe](javascript:alert(1))"
                    ),
                )
            ],
        )
        store.import_activity(
            conn,
            first,
            [
                Activity(
                    "comment/1",
                    "2026-01-02T00:00:00Z",
                    "reviewer-one",
                    "github:commented",
                    first.head,
                    "[Context](#discussion_r123)",
                ),
                Activity(
                    "review/2",
                    "2026-01-03T00:00:00Z",
                    "reviewer-two",
                    "github:changes_requested",
                    first.head,
                    "Please add validation.",
                ),
            ],
        )
    page.goto(url + "/triage/inbox/owner/repo/1")
    description = page.get_by_role("region", name="PR description", exact=True)
    expect(description.get_by_role("link", name="Related issue", exact=True)).to_have_attribute(
        "href", "https://github.com/owner/repo/issues/2"
    )
    expect(description.get_by_role("link", name="Diagram", exact=True)).to_have_attribute(
        "href", "https://github.com/owner/repo/raw/main/a.png"
    )
    expect(description.get_by_role("link", name="Unsafe", exact=True)).to_have_attribute("href", "")
    feed = page.get_by_role("region", name="PR feed 2", exact=True)
    expect(feed.locator(".event-header > strong")).to_have_text(["reviewer-one", "reviewer-two"])
    expect(feed.get_by_role("link", name="Context", exact=True)).to_have_attribute(
        "href", "https://github.com/owner/repo/pull/1#discussion_r123"
    )
    description.get_by_role("button", name="Collapse", exact=True).click()
    expect(description.get_by_role("link", name="Related issue", exact=True)).not_to_be_visible()
    expect(feed).to_be_visible()


def test_repository_scope_and_estate_graph_connectivity(browser_app: BrowserApp) -> None:
    page, url = browser_app.page, browser_app.url
    page.goto(url + "/estate?repo=owner%2Frepo")
    expect(page.get_by_role("heading", name="Estate Managed Repositories", exact=True)).to_be_visible()
    expect(page.get_by_role("button", name="Inspect owner/repo", exact=True)).to_be_visible()
    details = page.get_by_role("complementary", name="Repository details")
    expect(details).to_contain_text("Used by 1 repository")
    expect(details).to_contain_text("managed-library")
    page.get_by_role("button", name="Inspect owner/other", exact=True).click()
    expect(page).to_have_url(url + "/estate?repo=owner%2Fother")
    expect(details).to_contain_text("Consumes from 1 repository")
    expect(details).to_contain_text("^1.0")
    expect(details).to_contain_text("package.json")
    page.evaluate("window.rigEstateDocument = 42")
    details.get_by_role("link", name="1 issues to triage", exact=True).click()
    expect(page).to_have_url(url + "/repos/owner/other/triage/issues")
    assert page.evaluate("window.rigEstateDocument") == 42
    expect(page.get_by_role("combobox", name="Queue scope", exact=True)).to_have_text("owner/other")
    expect(page.locator(".queue-row")).to_have_count(1)
    page.locator(".queue-row").click()
    expect(page).to_have_url(url + "/repos/owner/other/triage/issues/3")
    page.reload()
    expect(page.get_by_role("heading", name="Triage the bug", exact=True)).to_be_visible()
    page.get_by_role("link", name="Inbox", exact=True).click()
    expect(page).to_have_url(url + "/repos/owner/other/triage/inbox")
    expect(page.locator(".queue-row")).to_have_count(0)
    page.get_by_role("combobox", name="Queue scope", exact=True).click()
    page.get_by_role("option", name="All repositories", exact=True).click()
    expect(page).to_have_url(url + "/triage/inbox")
    expect(page.locator(".queue-row")).to_have_count(2)
    page.goto(url + "/repos/owner/repo/triage/inbox/1?layout=focus")
    expect(page.get_by_role("heading", name="Fix the first issue", exact=True)).to_be_visible()
    expect(page.locator(".queue-column")).not_to_be_visible()
    page.get_by_role("button", name="Next item", exact=True).click()
    expect(page).to_have_url(url + "/repos/owner/repo/triage/inbox/2?layout=focus")
    page.goto(url + "/estate?repo=owner%2Fother")
    page.set_viewport_size({"width": 390, "height": 844})
    expect(page.get_by_role("heading", name="Estate Managed Repositories", exact=True)).to_be_visible()
    assert page.evaluate("document.documentElement.scrollWidth <= innerWidth")
    assert page.locator(".app-content").evaluate("element => getComputedStyle(element).overflowY") == "clip"


class _EstateLine(TypedDict):
    id: str
    to: str
    through: list[str]
    startY: float
    endY: float
    startGap: float
    endGap: float
    square: bool
    vectorEffect: str


_EstateLineFrom = TypedDict("_EstateLineFrom", {"from": str})


class _DrawnLine(_EstateLine, _EstateLineFrom):
    """What `_ESTATE_LINES` measures about one drawn line, in screen pixels."""


_ESTATE_LINES = """() => {
    const boxes = new Map([...document.querySelectorAll('.estate-node')].map(
        (node) => [node.dataset.node, node.getBoundingClientRect()]));
    return [...document.querySelectorAll('.estate-edges path[data-from]')].map((path) => {
        const matrix = path.getScreenCTM();
        const screen = (x, y) => new DOMPoint(x, y).matrixTransform(matrix);
        const corners = [...path.getAttribute('d').matchAll(/[ML] ([-\\d.]+) ([-\\d.]+)/g)]
            .map(([, x, y]) => screen(Number(x), Number(y)));
        const samples = [];
        for (let at = 0; at <= path.getTotalLength(); at += 3) {
            const point = path.getPointAtLength(at);
            samples.push(screen(point.x, point.y));
        }
        const through = [...boxes].filter(([id]) => id !== path.dataset.from && id !== path.dataset.to)
            .filter(([, box]) => samples.some((point) =>
                point.x > box.left + 1 && point.x < box.right - 1 &&
                point.y > box.top + 1 && point.y < box.bottom - 1))
            .map(([id]) => id);
        const source = boxes.get(path.dataset.from), target = boxes.get(path.dataset.to);
        const start = corners[0], end = corners.at(-1);
        return {
            id: `${path.dataset.from}->${path.dataset.to}`,
            from: path.dataset.from,
            to: path.dataset.to,
            through,
            startY: start.y,
            endY: end.y,
            startGap: Math.abs(start.x - source.right),
            endGap: Math.abs(end.x - target.left),
            square: corners.slice(1).every((point, at) =>
                Math.abs(point.x - corners[at].x) < 0.5 || Math.abs(point.y - corners[at].y) < 0.5),
            vectorEffect: getComputedStyle(path).vectorEffect,
        };
    });
}"""


def test_estate_lines_turn_in_gutters_and_never_cross_a_box(
    browser_app: BrowserApp, monkeypatch: pytest.MonkeyPatch
) -> None:
    # a feeds three consumers, one of them two layers on: b and d stand in the layer
    # between, so the direct line from a to c has to find the gap between them.
    ids = ["owner/a", "owner/b", "owner/c", "owner/d", "owner/e"]
    pairs = [("a", "b"), ("a", "d"), ("a", "c"), ("b", "c"), ("d", "c"), ("d", "e")]
    graph = Graph(
        [Project(id, id.split("/")[1], "", "Libraries", "Python", True, True, 1, []) for id in ids],
        [
            Dependency(f"owner/{s}", f"owner/{t}", "npm", f"pkg-{s}", "^1.0", "runtime", "package.json")
            for s, t in pairs
        ],
        [Component(id, [id], False) for id in ids],
        [ComponentEdge(f"owner/{s}", f"owner/{t}") for s, t in pairs],
        "2026-01-01T00:00:00Z",
        [],
    )
    monkeypatch.setattr(connectivity_web, "load_graph", lambda *, refresh=False: graph)
    page, url = browser_app.page, browser_app.url
    page.goto(url + "/estate")
    expect(page.locator(".estate-edges path[data-from]")).to_have_count(len(pairs))

    for zoom_out in (0, 3):
        for _ in range(zoom_out):
            page.get_by_role("button", name="Zoom graph out", exact=True).click()
        lines = cast(list[_DrawnLine], page.evaluate(_ESTATE_LINES))
        for line in lines:
            assert line["through"] == [], f"{line['id']} runs through {line['through']}"
            assert line["square"], f"{line['id']} has a segment that is neither level nor upright"
            # Two units of slack for the border; the arrow's tip is at the path's end.
            assert line["startGap"] < 2.5 and line["endGap"] < 2.5, line
            assert line["vectorEffect"] == "non-scaling-stroke"
        # No two lines share a port: five consumers of one box are five lines, not one.
        for node in ids:
            for side, ys in (
                ("out", sorted(line["startY"] for line in lines if line["from"] == node)),
                ("in", sorted(line["endY"] for line in lines if line["to"] == node)),
            ):
                assert all(b - a > 3 for a, b in zip(ys, ys[1:], strict=False)), (node, side, ys)


def test_estate_keeps_the_existing_operations_tools_in_the_spa(browser_app: BrowserApp) -> None:
    page, url = browser_app.page, browser_app.url
    page.goto(url + "/estate")
    expect(page.get_by_role("heading", name="Estate Managed Repositories", exact=True)).to_be_visible()
    # The sidebar carries the current tool's pages; other tools, C4 among them, live in the launcher.
    sidebar = page.get_by_role("navigation", name="Rig navigation")
    expect(sidebar.get_by_role("link", name="Inbox", exact=True)).to_have_count(0)
    expect(sidebar.get_by_role("link", name="C4 architecture", exact=True)).to_have_count(0)
    page.get_by_role("button", name="Rig tools", exact=True).click()
    tools = page.get_by_role("dialog", name="Rig tools")
    expect(tools.get_by_role("link", name="C4 architecture", exact=True)).to_have_attribute("href", "/c4/")
    page.keyboard.press("Escape")
    expect(tools).not_to_be_visible()
    page.evaluate("window.rigOperationsDocument = 42")
    page.get_by_role("navigation", name="Rig navigation").get_by_role("link", name="Operations", exact=True).click()
    expect(page).to_have_url(url + "/estate/operations")
    expect(page.get_by_role("link", name=re.compile("JOBS"))).to_have_attribute("href", "/ops/jobs")
    expect(page.get_by_role("button", name=re.compile("BACKUP_DB"))).to_be_visible()
    assert page.evaluate("window.rigOperationsDocument") == 42
    page.set_viewport_size({"width": 390, "height": 844})
    bounds = page.locator("#view-estate").bounding_box()
    assert bounds and bounds["y"] + bounds["height"] <= 844 - 53
    page.go_back()
    expect(page.get_by_role("heading", name="Estate Managed Repositories", exact=True)).to_be_visible()
    assert page.evaluate("window.rigOperationsDocument") == 42


def test_scoped_readiness_and_own_pr_actions(browser_app: BrowserApp) -> None:
    with closing(store.connect(browser_app.db)) as conn, conn:
        first = next(entry.item for entry in store.entries(conn) if entry.item.number == 1)
        store.save(
            conn,
            [
                replace(first, readiness=0, facts=Facts(viewer_authored=True, review_decision="APPROVED")),
                replace(first, repo="owner/other", number=4, readiness=2),
            ],
        )
    page = browser_app.page
    page.goto(browser_app.url + "/repos/owner/repo/triage/inbox/1")
    signals = page.get_by_label("Action signals")
    expect(signals.get_by_text("Approved", exact=True)).to_be_visible()
    expect(signals.get_by_text("Review this PR", exact=True)).to_have_count(0)
    page.get_by_role("button", name="Queue filters", exact=True).click()
    page.get_by_role("combobox", name="Readiness", exact=True).click()
    expect(page.get_by_role("option", name="Blocked (0)", exact=True)).to_be_visible()


def test_note_keeps_edits_typed_while_the_request_is_in_flight(browser_app: BrowserApp) -> None:
    from playwright.sync_api import Route

    page = browser_app.page
    page.goto(browser_app.url + "/triage/inbox/owner/repo/1")
    field = page.get_by_role("textbox", name="Review notes", exact=True)
    expect(field).to_be_visible()
    expect(page.get_by_role("button", name="Save note", exact=True)).to_be_disabled()
    field.fill("Submitted note")

    def save(route: Route) -> None:
        response = route.fetch()
        field.fill("Newer draft typed while saving")
        route.fulfill(response=response)

    page.route("**/api/triage/interact", save)
    page.get_by_role("button", name="Save note", exact=True).click()
    expect(page.get_by_text("Note saved.", exact=True)).to_be_visible()
    expect(field).to_have_value("Newer draft typed while saving")
    page.reload()
    expect(field).to_have_value("Newer draft typed while saving")
