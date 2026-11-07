"""The repo-grouping planner as a pure function of Chrome windows: every rule, by fixture."""

from __future__ import annotations

from dataclasses import replace
from itertools import count

import pytest

from desktop_manager.chrome.grouping import GroupPlan, Repo, SkippedWindow, plan_groups, repo_of, site_of
from desktop_manager.chrome.render import render_group_plan
from desktop_manager.chrome.tabs import ChromeTab, ChromeWindow, TabGroup
from rig.workspace.ports import PortClaim, PortRange
from rig.workspace.url_owners import UrlOwners
from rig.workspace.vercel_names import VercelClaim

pytestmark = pytest.mark.ci

SEP = "·"
_ids = count(100)


def pr(number: int, repo: str = "acme-corp/data-platform") -> str:
    return f"Some change by rtkelly13 {SEP} Pull Request #{number} {SEP} {repo}"


def tab(
    title: str, *, group: TabGroup | None = None, pinned: bool = False, url: str | None = None
) -> dict[str, object]:
    return {"title": title, "group": group, "pinned": pinned, "url": url}


def window(*specs: dict[str, object], desktop: int | None = 1, hwnd: int = 1000) -> ChromeWindow:
    tabs = tuple(
        ChromeTab(
            runtime_id=(42, hwnd, next(_ids)),
            window_hwnd=hwnd,
            index=index,
            name=str(spec["title"]),
            title=str(spec["title"]),
            selected=index == 0,
            pinned=bool(spec["pinned"]),
            loaded=True,
            audible=False,
            group=spec["group"],  # type: ignore[arg-type]
            url=spec["url"],  # type: ignore[arg-type]
        )
        for index, spec in enumerate(specs)
    )
    return ChromeWindow(
        hwnd=hwnd,
        title="w",
        profile_dir="Profile 4",
        profile_name="Ryan",
        age_rank=hwnd,
        minimized=False,
        desktop_number=desktop,
        tabs=tabs,
    )


def moves(plan: GroupPlan) -> list[tuple[int, str, bool]]:
    """(tab index, group title, creates the group) per move."""
    return [(move.tab.index, move.title, move.new) for move in plan.moves]


# --- repo identity ------------------------------------------------------------------------------


@pytest.mark.parametrize(
    ("title", "repo"),
    [
        (pr(12), Repo("acme-corp", "data-platform")),
        (f"Fix it {SEP} Issue #4 {SEP} rtkelly13/shared-utilities", Repo("rtkelly13", "shared-utilities")),
        (f"Pull requests {SEP} acme-corp/infrastructure {SEP} GitHub", Repo("acme-corp", "infrastructure")),
        (f"PR checks {SEP} acme-corp/data-platform@1a2b3c4", Repo("acme-corp", "data-platform")),
        ("acme-corp/rights-application: The rights app", Repo("acme-corp", "rights-application")),
        ("GitHub - astral-sh/uv: An extremely fast package manager", Repo("astral-sh", "uv")),
        (f"src/app.py at main {SEP} owner/repo.name", Repo("owner", "repo.name")),
        ("Inbox (3) - someone@example.com - Gmail", None),
        (f"A {SEP} B", None),
    ],
)
def test_repo_comes_from_githubs_title_shapes(title: str, repo: Repo | None):
    assert repo_of(title) == repo


# --- planning -----------------------------------------------------------------------------------


def test_first_ungrouped_tab_creates_the_group_and_the_rest_join():
    plan = plan_groups([window(tab(pr(1)), tab("Gmail"), tab(pr(2)), tab(pr(3)))], current_desktop=1)
    assert moves(plan) == [(0, "data-platform", True), (2, "data-platform", False), (3, "data-platform", False)]


def test_a_lone_tab_does_not_start_a_group_below_min_size():
    plan = plan_groups([window(tab(pr(1)), tab(pr(2, "acme-corp/infrastructure")))], current_desktop=1)
    assert moves(plan) == []
    assert moves(plan_groups([window(tab(pr(1)))], current_desktop=1, min_size=1)) == [(0, "data-platform", True)]


def test_an_existing_group_with_the_title_is_joined_even_by_one_tab():
    existing = TabGroup(runtime_id=(9,), title="data-platform")
    plan = plan_groups([window(tab(pr(1), group=existing), tab(pr(2)))], current_desktop=1)
    assert moves(plan) == [(1, "data-platform", False)]
    assert plan.already_grouped == 1


def test_tabs_in_another_group_and_pinned_tabs_never_move():
    by_hand = TabGroup(runtime_id=(9,), title="today")
    plan = plan_groups(
        [window(tab(pr(1), group=by_hand), tab(pr(2), pinned=True), tab(pr(3)), tab(pr(4)))], current_desktop=1
    )
    assert moves(plan) == [(2, "data-platform", True), (3, "data-platform", False)]
    assert plan.elsewhere == 1


def test_same_repo_name_under_two_owners_titles_both_with_the_owner():
    plan = plan_groups(
        [window(tab(pr(1, "a/tools")), tab(pr(2, "b/tools")), tab(pr(3, "a/tools")), tab(pr(4, "b/tools")))],
        current_desktop=1,
    )
    assert moves(plan) == [(0, "a/tools", True), (1, "b/tools", True), (2, "a/tools", False), (3, "b/tools", False)]


def test_owner_titles_apply_across_the_profile_not_just_the_window():
    plan = plan_groups(
        [
            window(tab(pr(1, "a/tools")), tab(pr(2, "a/tools")), hwnd=1000),
            window(tab(pr(3, "b/tools")), tab(pr(4, "b/tools")), hwnd=2000),
        ],
        current_desktop=1,
    )
    assert {move.title for move in plan.moves} == {"a/tools", "b/tools"}


# --- consolidation across windows ---------------------------------------------------------------


def test_a_repo_spread_over_windows_gathers_in_the_window_holding_most_of_it():
    plan = plan_groups([window(tab(pr(1)), hwnd=1000), window(tab(pr(2)), tab(pr(3)), hwnd=2000)], current_desktop=1)
    assert [(move.window_number, move.tab.index, move.new, move.home_number) for move in plan.moves] == [
        (2, 0, True, 2),
        (2, 1, False, 2),
        (1, 0, False, 2),
    ]
    assert [move.crosses for move in plan.moves] == [False, False, True]


def test_a_tie_goes_to_the_earlier_window():
    plan = plan_groups([window(tab(pr(1)), hwnd=1000), window(tab(pr(2)), hwnd=2000)], current_desktop=1)
    assert [(move.window_number, move.new, move.home_number) for move in plan.moves] == [(1, True, 1), (2, False, 1)]


def test_an_existing_site_group_is_home_and_loose_tabs_elsewhere_join_it():
    existing = TabGroup((9,), "YouTrack")
    plan = plan_groups(
        [window(tab("A : PROJ-1"), tab("B : PROJ-2"), hwnd=1000), window(tab("C : PROJ-3", group=existing), hwnd=2000)],
        current_desktop=1,
    )
    assert [(move.window_number, move.new, move.home_number) for move in plan.moves] == [(1, False, 2), (1, False, 2)]
    assert plan.already_grouped == 1


def test_duplicate_site_groups_merge_into_the_biggest_after_ungrouping_the_rest():
    big, small = TabGroup((8,), "YouTrack"), TabGroup((9,), "YouTrack")
    plan = plan_groups(
        [
            window(tab("A : PROJ-1", group=small), tab("B : PROJ-2", group=small), tab("E : PROJ-5"), hwnd=1000),
            window(tab("C : PROJ-3", group=big), tab("D : PROJ-4", group=big), tab("F : PROJ-6", group=big), hwnd=2000),
        ],
        current_desktop=1,
    )
    assert [(ungroup.group, ungroup.window_number, len(ungroup.tabs)) for ungroup in plan.ungroups] == [(small, 1, 2)]
    assert [(move.window_number, move.tab.index, move.home_number) for move in plan.moves] == [
        (1, 0, 2),
        (1, 1, 2),
        (1, 2, 2),
    ]
    assert plan.window_numbers() == [1]
    assert plan.ungroup_window_numbers() == [1]


def test_every_repo_gathers_in_the_window_holding_the_most_github_tabs():
    infra = "acme-corp/infrastructure"
    stray = TabGroup((9,), "data-platform")
    plan = plan_groups(
        [
            window(tab(pr(1), group=stray), tab(pr(2)), hwnd=1000),
            window(tab(pr(3, infra)), tab(pr(4, infra)), tab(pr(5, infra)), tab(pr(6)), hwnd=2000),
        ],
        current_desktop=1,
    )
    assert [(ungroup.group, ungroup.window_number) for ungroup in plan.ungroups] == [(stray, 1)]
    assert sorted((move.title, move.window_number, move.tab.index, move.new) for move in plan.moves) == [
        ("data-platform", 1, 0, False),
        ("data-platform", 1, 1, False),
        ("data-platform", 2, 3, True),
        ("infrastructure", 2, 0, True),
        ("infrastructure", 2, 1, False),
        ("infrastructure", 2, 2, False),
    ]
    assert {move.home_number for move in plan.moves} == {2}


def test_a_repo_absent_from_the_review_window_is_created_there_by_a_tab_moved_in():
    infra = "acme-corp/infrastructure"
    plan = plan_groups(
        [
            window(tab(pr(1, infra)), tab(pr(2, infra)), tab(pr(3, infra)), hwnd=1000),
            window(tab(pr(4)), tab(pr(5)), hwnd=2000),
        ],
        current_desktop=1,
    )
    platform = [move for move in plan.moves if move.title == "data-platform"]
    assert [(move.window_number, move.tab.index, move.new, move.crosses) for move in platform] == [
        (2, 0, True, True),
        (2, 1, False, True),
    ]
    assert render_group_plan(plan)[-3].startswith("  new in w1 data-platform")


def test_windows_on_other_desktops_are_gathered_by_default():
    plan = plan_groups(
        [window(tab(pr(1)), tab(pr(2)), hwnd=1000), window(tab(pr(3)), desktop=2, hwnd=2000)], current_desktop=1
    )
    assert [(move.window_number, move.new, move.home_number) for move in plan.moves] == [
        (1, True, 1),
        (1, False, 1),
        (2, False, 1),
    ]
    assert plan.skipped == ()


def test_a_duplicate_holding_other_tabs_blocks_the_key():
    first, second = TabGroup((8,), "data-platform"), TabGroup((9,), "data-platform")
    plan = plan_groups(
        [
            window(tab(pr(1), group=first), tab(pr(2), group=first), hwnd=1000),
            window(tab(pr(3), group=second), tab("Inbox - me@x.com - Gmail", group=second), hwnd=2000),
        ],
        current_desktop=1,
    )
    assert plan.moves == ()
    assert plan.ungroups == ()
    assert [block.title for block in plan.blocked] == ["data-platform"]


def test_a_group_on_another_desktop_blocks_the_key():
    away = TabGroup((9,), "data-platform")
    plan = plan_groups(
        [window(tab(pr(1)), tab(pr(2)), hwnd=1000), window(tab(pr(3), group=away), desktop=2, hwnd=2000)],
        current_desktop=1,
        all_desktops=False,
    )
    assert plan.moves == ()
    assert [(block.title, block.reason) for block in plan.blocked] == [("data-platform", "its group is on desktop 2")]


def test_groups_are_per_profile():
    other = replace(window(tab(pr(3)), tab(pr(4)), hwnd=2000), profile_dir="Default", profile_name="Work")
    plan = plan_groups([window(tab(pr(1)), tab(pr(2)), hwnd=1000), other], current_desktop=1)
    assert [(move.window_number, move.new, move.home_number) for move in plan.moves] == [
        (1, True, 1),
        (1, False, 1),
        (2, True, 2),
        (2, False, 2),
    ]


def test_windows_that_create_groups_run_before_windows_that_only_join():
    plan = plan_groups([window(tab(pr(1)), hwnd=1000), window(tab(pr(2)), tab(pr(3)), hwnd=2000)], current_desktop=1)
    assert plan.window_numbers() == [2, 1]


# --- sites --------------------------------------------------------------------------------------


@pytest.mark.parametrize(
    ("title", "site"),
    [
        ("Inbox (5) - ryan@example.com - example.com Mail", "Google"),
        ("example.com \u2013 Calendar - Week of 28 September 2026", "Google"),
        ("Budget - Google Sheets", "Google"),
        ("worktrees - Google Search", "Google"),
        ("Gemini", "AI"),
        ("Draft the retro - Gemini", "AI"),
        ("Cross-Language Profiling - Google Gemini", "AI"),
        ("Q3 plan - Asana", "Asana"),
        ("\u25cf Q3 plan - Asana", "Asana"),
        ("Home - Asana", "Asana"),
        ("Rename six link relationships : PROJ-3302", "YouTrack"),
        ("Issues | YouTrack", "YouTrack"),
        ("Link direction brief - Claude", "Claude"),
        ("Claude", "Claude"),
        ("Google Meet", None),
        ("Meet - abc-defg-hij", None),
        ("Zoom Meeting", None),
        ("Standup | Microsoft Teams", None),
        ("Huddle with Sam - Slack", None),
        ("Slack", None),
        ("Registrations Retro - Miro", None),
    ],
)
def test_site_comes_from_the_title_and_calls_never_group(title: str, site: str | None):
    assert site_of(title) == site


@pytest.mark.parametrize(
    ("title", "host", "site"),
    [
        ("Linking Triage", "claude.ai", "Claude"),
        ("Rights App Merge Path", "www.claude.ai", "Claude"),
        ("Q3 roadmap", "app.asana.com", "Asana"),
        ("Budget", "docs.google.com", "Google"),
        ("Draft the retro", "gemini.google.com", "AI"),
        ("Search", "www.google.com", "Google"),
        ("Some page", "notgoogle.com", None),
        ("Board", "acme.youtrack.cloud", "YouTrack"),
        ("Standup", "meet.google.com", None),
        ("Some page", "example.com", None),
        ("Some page", None, None),
    ],
)
def test_a_bare_title_takes_its_site_from_the_host(title: str, host: str | None, site: str | None):
    assert site_of(title, host) == site


def test_history_hosts_group_bare_claude_artifact_titles():
    plan = plan_groups(
        [window(tab("Linking Triage"), tab("Linking by Entity"), tab("Somewhere else"))],
        current_desktop=1,
        hosts={"Profile 4": {"Linking Triage": "claude.ai", "Linking by Entity": "claude.ai"}},
    )
    assert moves(plan) == [(0, "Claude", True), (1, "Claude", False)]


def test_site_tabs_group_like_repos_and_call_tabs_stay_put():
    plan = plan_groups(
        [
            window(tab("Google Meet"), tab("Inbox - me@x.com - Gmail"), tab("A : PROJ-1"), hwnd=1000),
            window(tab("Budget - Google Sheets"), tab("B : PROJ-2"), tab("Meet - abc-defg-hij"), hwnd=2000),
        ],
        current_desktop=1,
    )
    assert sorted((move.title, move.window_number, move.new) for move in plan.moves) == [
        ("Google", 1, True),
        ("Google", 2, False),
        ("YouTrack", 1, True),
        ("YouTrack", 2, False),
    ]


def test_with_this_desktop_only_windows_on_other_desktops_are_skipped():
    plan = plan_groups(
        [window(tab(pr(1)), tab(pr(2)), desktop=2), window(tab(pr(3)), tab(pr(4)), desktop=None, hwnd=2000)],
        current_desktop=1,
        all_desktops=False,
    )
    assert plan.moves == ()
    assert plan.skipped == (
        SkippedWindow(1, "Ryan", "on desktop 2, not the current one"),
        SkippedWindow(2, "Ryan", "on an unknown desktop, not the current one"),
    )


def test_render_lists_moves_per_window_then_a_summary():
    plan = plan_groups([window(tab(pr(1)), tab(pr(2)))], current_desktop=1)
    lines = render_group_plan(plan)
    assert lines[0] == "Window 1 [Ryan] (2)"
    assert lines[1].startswith("  new group data-platform")
    assert lines[2].startswith("  join      data-platform")
    assert lines[-1] == (
        "2 tabs to move (0 across windows), 1 new groups, 0 duplicates merged, "
        "0 already grouped, 0 left in other groups"
    )


def test_render_shows_ungroups_and_moves_to_other_windows():
    small, big = TabGroup((8,), "data-platform"), TabGroup((9,), "data-platform")
    plan = plan_groups(
        [
            window(tab(pr(1), group=small), hwnd=1000),
            window(tab(pr(3), group=big), tab(pr(4), group=big), hwnd=2000),
        ],
        current_desktop=1,
    )
    lines = render_group_plan(plan)
    assert lines[:4] == [
        "Ungroup first (1)",
        "  ungroup   data-platform            1 tabs in window 1",
        "Window 1 [Ryan] (1)",
        lines[3],
    ]
    assert lines[3].startswith("  to w2     data-platform")


# --- URLs: GitHub pages and local apps -----------------------------------------------------------

RIG_PORTS = UrlOwners(ports=(PortClaim("rig", "rtkelly13/rig", PortRange(8090, 8099)),))
DS_VERCEL = UrlOwners(
    vercel=(
        VercelClaim("design-system", "rtkelly-labs/design-system", ("design-system", "design-system-storybook")),
        VercelClaim("rig", "rtkelly13/rig", ("design-system-docs",)),
    )
)


def test_local_app_on_a_claimed_port_joins_its_repo_group() -> None:
    rig = TabGroup((42, 1, 1), "rig")
    plan = plan_groups(
        [
            window(
                tab(pr(1, "rtkelly13/rig"), group=rig),
                tab("Rig / Triage", url="http://127.0.0.1:8094/triage/inbox"),
                tab("Rig / Triage", url="http://localhost:8094/triage/issues/5"),
            )
        ],
        current_desktop=1,
        owners=RIG_PORTS,
    )
    assert moves(plan) == [(1, "rig", False), (2, "rig", False)]


@pytest.mark.parametrize("url", ["http://127.0.0.1:3000/a", "http://localhost/a", "https://example.com:8094/a"])
def test_unclaimed_or_remote_ports_do_not_group(url: str) -> None:
    plan = plan_groups([window(tab("App", url=url), tab("App", url=url))], current_desktop=1, owners=RIG_PORTS)
    assert moves(plan) == []


def test_github_url_names_the_repo_a_title_does_not() -> None:
    plan = plan_groups(
        [
            window(
                tab("General", url="https://github.com/rtkelly-labs/Iceberg.NET/settings"),
                tab(pr(9, "rtkelly-labs/Iceberg.NET")),
            )
        ],
        current_desktop=1,
    )
    assert [title for _, title, _ in moves(plan)] == ["Iceberg.NET", "Iceberg.NET"]


@pytest.mark.parametrize(
    "url",
    [
        "https://github.com/users/rtkelly13/projects/1/views/1",
        "https://github.com/search?q=x",
        "https://github.com/rtkelly13",
    ],
)
def test_github_pages_without_a_repo_group_as_dev(url: str) -> None:
    plan = plan_groups([window(tab("Page", url=url), tab("Page", url=url))], current_desktop=1)
    assert [title for _, title, _ in moves(plan)] == ["Dev", "Dev"]


@pytest.mark.parametrize(
    "host",
    [
        "design-system-storybook.vercel.app",
        "design-system-storybook-cl1lgrpyn-rtkelly13s-projects.vercel.app",
        "design-system-storybook-diffra.vercel.app",
        "design-system-git-feat-manifesto-lan-326cca-rtkelly13s-projects.vercel.app",
    ],
)
def test_vercel_hosts_join_the_repo_their_project_name_declares(host: str) -> None:
    plan = plan_groups(
        [window(tab("Storybook", url=f"https://{host}/"), tab(pr(1, "rtkelly-labs/design-system")))],
        current_desktop=1,
        owners=DS_VERCEL,
    )
    assert [title for _, title, _ in moves(plan)] == ["design-system", "design-system"]


def test_longest_declared_vercel_name_wins() -> None:
    assert DS_VERCEL.slug_for("https://design-system-docs-abc.vercel.app/") == "rtkelly13/rig"
    assert DS_VERCEL.slug_for("https://design-system-storybook-abc.vercel.app/") == "rtkelly-labs/design-system"
    assert DS_VERCEL.slug_for("https://design-systemx.vercel.app/") is None
    assert DS_VERCEL.slug_for("https://ds-review-eta.vercel.app/") is None


@pytest.mark.parametrize(
    ("host", "site"),
    [
        ("www.npmjs.com", "Dev"),
        ("docs.renovatebot.com", "Dev"),
        ("vercel.com", "Dev"),
        ("chatgpt.com", "AI"),
        ("gemini.google.com", "AI"),
        ("docs.google.com", "Google"),
        ("www.plex.tv", None),
        ("meet.google.com", None),
    ],
)
def test_configured_site_groups_take_the_longest_domain(host: str, site: str | None) -> None:
    assert site_of("Some page", host) == site
