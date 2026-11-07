"""The PR-prune planner as a pure function of (tabs, PR states): every rule, by fixture."""

from __future__ import annotations

from dataclasses import dataclass, field

import pytest

from desktop_manager.chrome.pr_prune import (
    AppliedClose,
    PrunePlan,
    apply_plan,
    apply_plans,
    hold_unattended,
    plan_prune,
    pr_ref_from_title,
)
from desktop_manager.chrome.render import render_plan
from desktop_manager.chrome.tabs import ChromeTab, ChromeWindow
from rig.github_prs import OpenPr, PrRef, PrState, RepoChain, Resolution, build_chains
from tests.desktop_manager.fake_chrome import FakeChrome

pytestmark = pytest.mark.ci

REPO = "acme-corp/data-platform"
SEP = "·"


def pr_title(number: int, title: str = "Some change", repo: str = REPO) -> str:
    return f"{title} by rtkelly13 {SEP} Pull Request #{number} {SEP} {repo}"


def _memory(value: object) -> float | None:
    return float(value) if isinstance(value, int | float) else None


@dataclass
class Browser:
    """Builds windows tab by tab; runtime ids are unique and increase in creation order.

    A tab spec's `mb` sets its reported memory (and implies nothing about `loaded`).
    """

    windows: list[ChromeWindow] = field(default_factory=lambda: [])
    _next_id: int = 100

    def window(
        self, *tabs: dict[str, object], profile: str = "Profile 4", age: int | None = None, minimized: bool = False
    ) -> ChromeWindow:
        hwnd = 1000 + len(self.windows)
        built: list[ChromeTab] = []
        for index, spec in enumerate(tabs):
            self._next_id += 1
            built.append(
                ChromeTab(
                    runtime_id=(42, hwnd, self._next_id),
                    window_hwnd=hwnd,
                    index=index,
                    name=str(spec["title"]),
                    title=str(spec["title"]),
                    selected=bool(spec.get("selected", False)),
                    pinned=bool(spec.get("pinned", False)),
                    loaded=bool(spec.get("loaded", False)),
                    audible=bool(spec.get("audible", False)),
                    memory_mb=_memory(spec.get("mb")),
                    url=str(spec["url"]) if "url" in spec else None,
                )
            )
        window = ChromeWindow(
            hwnd=hwnd,
            title="w",
            profile_dir=profile,
            profile_name=None,
            age_rank=age if age is not None else len(self.windows),
            minimized=minimized,
            desktop_number=1,
            tabs=tuple(built),
        )
        self.windows.append(window)
        return window


def tab(number: int, **flags: object) -> dict[str, object]:
    return {"title": flags.pop("title", pr_title(number)), **flags}


def ref(number: int, repo: str = REPO) -> PrRef:
    return PrRef.parse(f"{repo}#{number}")


def open_pr(number: int, head: str, base: str = "main", **kwargs: object) -> OpenPr:
    return OpenPr(number, head, base, created_at="2026-09-01T00:00:00Z", **kwargs)  # type: ignore[arg-type]


def resolution(states: dict[int, str], open_prs: list[OpenPr] | None = None) -> Resolution:
    by_ref = {
        ref(n): PrState(s, "NOT_FOUND" if s == "UNKNOWN" else "")  # type: ignore[arg-type]
        for n, s in states.items()
    }
    owner, repo = REPO.split("/", 1)
    chains = [RepoChain.of(owner, repo, chain) for chain in build_chains(open_prs or [])]
    return Resolution(states=by_ref, chains=chains)


def closes(plan: PrunePlan) -> dict[int, list[tuple[str, str]]]:
    """PR number -> [(kind, reason)] per closed tab."""
    found: dict[int, list[tuple[str, str]]] = {}
    for close in plan.closes:
        found.setdefault(close.item.pr.number, []).append((close.kind, close.reason))
    return found


def reports(plan: PrunePlan) -> dict[int, list[str]]:
    found: dict[int, list[str]] = {}
    for report in plan.reports:
        found.setdefault(report.item.pr.number, []).append(report.kind)
    return found


# --- title identity -----------------------------------------------------------------------------


def test_pr_ref_from_title_lowercases_and_ignores_subpages_and_suffixes():
    assert pr_ref_from_title(pr_title(780, repo="Acme-Corp/Data-Platform")) == ref(780)
    assert pr_ref_from_title(pr_title(780) + " - Memory usage - 12 MB") == ref(780)
    assert pr_ref_from_title("Pull requests · acme-corp/data-platform") is None
    assert pr_ref_from_title("Issue 12 · acme-corp/data-platform") is None


def test_pr_ref_from_title_takes_the_last_marker_when_a_pr_title_quotes_another():
    title = pr_title(12, title=f"Revert {SEP} Pull Request #9 {SEP} other/repo")
    assert pr_ref_from_title(title) == ref(12)


# --- rule 1: duplicates -------------------------------------------------------------------------


def test_duplicate_keeps_the_active_tab_over_a_loaded_one():
    browser = Browser()
    browser.window(tab(1, loaded=True), tab(1, selected=True), tab(2))
    plan = plan_prune(browser.windows, resolution({1: "OPEN", 2: "OPEN"}))
    assert closes(plan) == {1: [("duplicate", "duplicate of tab 2 in window 1")]}
    assert plan.closes[0].item.tab.index == 0


def test_duplicate_keeps_a_loaded_tab_over_the_leftmost():
    browser = Browser()
    browser.window(tab(1), tab(1, loaded=True))
    plan = plan_prune(browser.windows, resolution({1: "OPEN"}))
    assert [c.item.tab.index for c in plan.closes] == [0]


def test_duplicate_falls_back_to_the_leftmost_tab_in_the_oldest_window():
    browser = Browser()
    young = browser.window(tab(1), age=50)
    old = browser.window(tab(9), tab(1), age=10)
    plan = plan_prune(browser.windows, resolution({1: "OPEN", 9: "OPEN"}))
    assert [c.item.tab.window_hwnd for c in plan.closes] == [young.hwnd]
    assert plan.closes[0].keeper == old.tabs[1]
    assert plan.closes[0].reason == "duplicate of tab 2 in window 1"


def test_renamed_title_is_still_a_duplicate():
    browser = Browser()
    browser.window(tab(1, title=pr_title(1, "Old name"), loaded=True), tab(1, title=pr_title(1, "New name")))
    plan = plan_prune(browser.windows, resolution({1: "OPEN"}))
    assert closes(plan) == {1: [("duplicate", "duplicate of tab 1 in window 1")]}


def test_duplicates_are_per_profile():
    browser = Browser()
    browser.window(tab(1), profile="Default")
    browser.window(tab(1), profile="Profile 4")
    plan = plan_prune(browser.windows, resolution({1: "OPEN"}))
    assert plan.closes == ()
    assert len(plan.kept) == 2


# --- rule 2: merged -----------------------------------------------------------------------------


def test_merged_closes_every_tab_even_the_active_one():
    browser = Browser()
    browser.window(tab(1, selected=True, loaded=True), tab(1))
    plan = plan_prune(browser.windows, resolution({1: "MERGED"}))
    assert closes(plan) == {1: [("merged", "merged"), ("merged", "merged")]}
    assert plan.activations() == {}


# --- rule 3: chains -----------------------------------------------------------------------------


def _stack_of_three() -> list[OpenPr]:
    return [open_pr(10, "a"), open_pr(11, "b", base="a"), open_pr(12, "c", base="b")]


def test_stack_of_three_with_only_the_top_two_open_as_tabs_keeps_the_middle():
    browser = Browser()
    browser.window(tab(11), tab(12))
    plan = plan_prune(browser.windows, resolution({11: "OPEN", 12: "OPEN"}, _stack_of_three()))
    assert closes(plan) == {12: [("stack", "superseded by #11 (stack bottom)")]}
    assert [item.pr.number for item in plan.kept] == [11]


def test_stack_keeper_with_several_tabs_is_deduplicated():
    browser = Browser()
    browser.window(tab(10), tab(10, selected=True), tab(12, selected=False))
    plan = plan_prune(browser.windows, resolution({10: "OPEN", 12: "OPEN"}, _stack_of_three()))
    assert closes(plan) == {
        10: [("duplicate", "duplicate of tab 2 in window 1")],
        12: [("stack", "superseded by #10 (stack bottom)")],
    }


def test_merged_middle_member_breaks_the_stack():
    # 11 merged: it is no longer in the open list, so 12's base is nobody's open head.
    browser = Browser()
    browser.window(tab(10), tab(11), tab(12))
    open_list = [open_pr(10, "a"), open_pr(12, "c", base="b")]
    plan = plan_prune(browser.windows, resolution({10: "OPEN", 11: "MERGED", 12: "OPEN"}, open_list))
    assert closes(plan) == {11: [("merged", "merged")]}
    assert sorted(item.pr.number for item in plan.kept) == [10, 12]


def test_native_merge_queue_keeps_the_front_member_that_has_a_tab():
    browser = Browser()
    browser.window(tab(21), tab(22), tab(23))
    queue = [
        open_pr(20, "q0", queue_position=1),
        open_pr(23, "q3", queue_position=2),
        open_pr(21, "q1", queue_position=3),
        open_pr(22, "q2", queue_position=4),
    ]
    plan = plan_prune(browser.windows, resolution({21: "OPEN", 22: "OPEN", 23: "OPEN"}, queue))
    assert closes(plan) == {21: [("queue", "queued behind #23")], 22: [("queue", "queued behind #23")]}


def test_label_queue_puts_the_holder_first_then_orders_by_label_time():
    browser = Browser()
    browser.window(tab(31), tab(32), tab(33))
    queue = [
        open_pr(31, "l1", labels=frozenset({"automerge"}), labeled_at={"automerge": "2026-09-02T00:00:00Z"}),
        open_pr(32, "l2", labels=frozenset({"automerge"}), labeled_at={"automerge": "2026-09-01T00:00:00Z"}),
        open_pr(33, "l3", labels=frozenset({"automerge", "locked-merging"})),
    ]
    plan = plan_prune(browser.windows, resolution({31: "OPEN", 32: "OPEN", 33: "OPEN"}, queue))
    assert closes(plan) == {31: [("queue", "queued behind #33")], 32: [("queue", "queued behind #33")]}


def test_chain_rules_are_skipped_for_a_repo_whose_open_list_failed():
    browser = Browser()
    browser.window(tab(11), tab(12))
    base = resolution({11: "OPEN", 12: "OPEN"}, _stack_of_three())
    failed = Resolution(states=base.states, chains=base.chains, failed_repos={REPO: "timeout"})
    assert plan_prune(browser.windows, failed).closes == ()


def test_active_superseded_tab_activates_the_keeper_in_the_same_window():
    browser = Browser()
    window = browser.window(tab(11), tab(12, selected=True))
    plan = plan_prune(browser.windows, resolution({11: "OPEN", 12: "OPEN"}, _stack_of_three()))
    assert plan.activations() == {window.hwnd: window.tabs[0]}


def test_active_superseded_tab_in_another_window_leaves_chrome_to_pick():
    browser = Browser()
    browser.window(tab(11))
    browser.window(tab(12, selected=True))
    plan = plan_prune(browser.windows, resolution({11: "OPEN", 12: "OPEN"}, _stack_of_three()))
    assert plan.activations() == {}


# --- closed, unknown state, and protection ----------------------------------------------------------------


def test_closed_without_merging_closes_every_tab():
    browser = Browser()
    browser.window(tab(5), tab(5))
    plan = plan_prune(browser.windows, resolution({5: "CLOSED"}))
    assert closes(plan) == {5: [("closed", "closed without merging"), ("closed", "closed without merging")]}
    assert reports(plan) == {}


def test_unknown_state_is_never_closed_even_as_a_duplicate_or_chain_member():
    browser = Browser()
    browser.window(tab(6), tab(6), tab(11), tab(12))
    plan = plan_prune(browser.windows, resolution({6: "UNKNOWN", 11: "OPEN", 12: "UNKNOWN"}, _stack_of_three()))
    assert plan.closes == ()
    assert reports(plan) == {6: ["unknown", "unknown"], 12: ["unknown"]}


def test_unresolved_pr_counts_as_unknown():
    browser = Browser()
    browser.window(tab(7))
    plan = plan_prune(browser.windows, resolution({}))
    assert [(r.kind, r.detail) for r in plan.reports] == [("unknown", "not resolved")]


def test_pinned_and_audible_tabs_are_never_closed():
    browser = Browser()
    browser.window(tab(1, pinned=True), tab(1, audible=True), tab(1, selected=True), tab(2, pinned=True))
    plan = plan_prune(browser.windows, resolution({1: "OPEN", 2: "MERGED"}))
    assert plan.closes == ()
    assert sorted((r.item.pr.number, r.detail) for r in plan.reports) == [
        (1, "audible; would close as: duplicate of tab 3 in window 1"),
        (1, "pinned; would close as: duplicate of tab 3 in window 1"),
        (2, "pinned; would close as: merged"),
    ]


def test_non_pr_tabs_are_ignored():
    browser = Browser()
    browser.window(tab(0, title="Inbox"), tab(0, title="Pull requests · acme-corp/data-platform"))
    plan = plan_prune(browser.windows, resolution({}))
    assert (plan.closes, plan.reports, plan.kept) == ((), (), ())


# --- rendering and execution --------------------------------------------------------------------


def test_render_plan_groups_by_reason_in_a_fixed_order():
    browser = Browser()
    browser.window(tab(12), tab(1), tab(1, selected=True), tab(11), tab(5), tab(3))
    plan = plan_prune(
        browser.windows, resolution({1: "OPEN", 3: "MERGED", 5: "CLOSED", 11: "OPEN", 12: "OPEN"}, _stack_of_three())
    )
    headings = [line.split(" (")[0] for line in render_plan(plan) if not line.startswith(" ")]
    assert headings == [
        "Close: merged",
        "Close: closed without merging",
        "Close: duplicate PR tabs",
        "Close: superseded by a lower stack member",
        "4 to close, 2 PR tabs kept, 0 reported, 5 PRs seen",
    ]


def test_apply_plan_closes_by_identity_then_restores_selection_once():
    browser = Browser()
    window = browser.window(tab(11), tab(12, selected=True), tab(3))
    plan = plan_prune(browser.windows, resolution({3: "MERGED", 11: "OPEN", 12: "OPEN"}, _stack_of_three()))
    actions = FakeChrome()
    applied = apply_plan(plan, browser.windows, actions)
    assert [t.runtime_id for t in actions.closed] == [window.tabs[2].runtime_id, window.tabs[1].runtime_id]
    assert all(result.closed for result in applied)
    assert actions.restored == [([window.hwnd], {window.hwnd: window.tabs[0]})]


def test_apply_finishes_each_window_before_the_next_in_the_given_order():
    browser = Browser()
    first = browser.window(tab(3), tab(11, selected=True))
    second = browser.window(tab(4), tab(12), tab(5))
    untouched = browser.window(tab(13))
    plan = plan_prune(
        browser.windows, resolution({3: "MERGED", 4: "MERGED", 5: "MERGED", 11: "OPEN", 12: "OPEN", 13: "OPEN"})
    )
    actions = FakeChrome()

    def after(window: ChromeWindow, closes: list[AppliedClose]) -> None:
        actions.log.append(("after", window.hwnd, len(closes)))

    apply_plans([plan], browser.windows, actions, order=[second.hwnd, first.hwnd], after_window=after)
    assert actions.log == [
        ("close", second.hwnd, 0),
        ("close", second.hwnd, 2),
        ("restore", second.hwnd, -1),
        ("after", second.hwnd, 2),
        ("close", first.hwnd, 0),
        ("restore", first.hwnd, -1),
        ("after", first.hwnd, 1),
        ("after", untouched.hwnd, 0),
    ]


def test_unattended_holds_back_active_tabs_and_the_foreground_window():
    browser = Browser()
    background = browser.window(tab(1), tab(1, selected=True), tab(2))
    foreground = browser.window(tab(2), tab(3))
    plan = plan_prune(browser.windows, resolution({1: "MERGED", 2: "MERGED", 3: "MERGED"}))
    runnable = hold_unattended([plan], foreground_hwnd=foreground.hwnd)
    assert [close.item.tab for close in runnable.closes] == [background.tabs[0], background.tabs[2]]
    assert {close.item.tab.runtime_id for close in runnable.held} == {
        background.tabs[1].runtime_id,
        *(t.runtime_id for t in foreground.tabs),
    }
