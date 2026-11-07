"""Decide which tabs to gather into one tab group per GitHub repository or work site.

`plan_groups` is a pure function of the Chrome windows, so every rule is
testable without a browser. Chrome's "Add tab to group" menu lists every group
in the profile by title alone, whichever window it is in, and picking one moves
the tab into that group's window. So the plan works per profile, not per window:

1. **One group per key per profile** - a key is a repo (titled with the repo
   name, or `owner/repo` when two repos in the profile share a name) or a site
   (`Google`, for any `google.com` host but Meet, `Asana`, `YouTrack`, `Claude`).
2. **One home window** - every repo group lives in the profile's review
   window: the window holding the most GitHub tabs (the earlier window on a
   tie). A site's home is the window of its existing group, or else the window
   holding most of its tabs. Tabs in other windows move there, and a group
   that lives elsewhere is ungrouped and gathered again in its home.
3. **Duplicates merge** - when several windows hold a group with the key's
   title, the menu cannot tell them apart, so every group but the home one is
   ungrouped first and its tabs then join the home group. A duplicate that also
   holds tabs of other keys was grouped by hand, and blocks the key instead.
4. **A new group needs company** - a key with no group and fewer than
   `min_size` loose tabs stays where it is.
5. **Hands off** - pinned tabs, call tabs and tabs in a group with another
   title never move.
6. **Every desktop** - by default windows on every virtual desktop take part;
   the caller brings each onto the current desktop while it works on it, since
   clicking into a window on another desktop would switch desktops. With
   `all_desktops=False`, windows on other desktops are skipped and a key with a
   group on another desktop is blocked.

A tab's repo comes from its title alone, as GitHub writes it: `... · owner/repo`
on most pages (optionally `@sha`, or a trailing `· GitHub`), and
`owner/repo: description` or `GitHub - owner/repo...` on a repo's home page.
Sites come from the title shapes `site_rules` knows, the sites' own shells, and
failing those the tab's host: its URL's where the driver reports one (macOS),
else the host history ties the title to (`hosts`), which is how a claude.ai
artifact, titled with its own name alone, is found. Hosts map to sites through
`config/chrome-site-groups.json` (`site_hosts`).

A tab's URL can name its repo too: a `github.com/owner/repo/...` page whose
title does not, or a local app or `*.vercel.app` deployment its project declares
(`rig.workspace.url_owners`), which groups with that project's GitHub tabs.
"""

from __future__ import annotations

import re
from collections import defaultdict
from collections.abc import Iterable, Mapping, Sequence
from dataclasses import dataclass, field
from typing import override
from urllib.parse import urlsplit

from desktop_manager.chrome.site_hosts import SiteHosts, default_site_hosts
from desktop_manager.chrome.site_rules import identify, is_call
from desktop_manager.chrome.tabs import ChromeTab, ChromeWindow, TabGroup
from rig.workspace.url_owners import UrlOwners

_NAME = r"[\w.-]+"
_TRAILING_REPO = re.compile(rf" · (?P<owner>{_NAME})/(?P<repo>{_NAME})(?:@\w+)?(?: · GitHub)?$")
_HOME_REPO = re.compile(rf"^(?:GitHub - )?(?P<owner>{_NAME})/(?P<repo>{_NAME})(?::\s|$)")

_SITE_OF_RULE = {
    "gmail": "Google",
    "google-calendar": "Google",
    "google-docs": "Google",
    "google-drive": "Google",
    "asana": "Asana",
    "youtrack": "YouTrack",
}
#: Titles `identify` leaves unkeyed (app shells, lists, searches) that still belong to a site.
_SITE_TITLES: dict[str, re.Pattern[str]] = {
    "Google": re.compile(r" - Google (?:Search|Docs|Sheets|Slides|Forms|Drive)$| - (?:Gmail|Google Calendar)$"),
    "AI": re.compile(r"(?:^| - )(?:Google )?Gemini$|^ChatGPT$"),
    "Asana": re.compile(r"(?:^| - )Asana$"),
    "YouTrack": re.compile(r"\bYouTrack\b"),
    "Claude": re.compile(r"(?:^| - )Claude(?: Code)?$"),
}
#: Hosts whose tabs may be a live call, whatever their title.
_CALL_HOSTS = ("meet.google.com", "zoom.us", "teams.microsoft.com", "teams.live.com", "whereby.com", "meet.jit.si")


@dataclass(frozen=True, slots=True)
class Repo:
    owner: str
    name: str

    @override
    def __str__(self) -> str:
        return f"{self.owner}/{self.name}"


def repo_of(title: str) -> Repo | None:
    """The GitHub repository a tab's title names, or None for any other page."""
    match = _TRAILING_REPO.search(title) or _HOME_REPO.match(title)
    if match is None:
        return None
    return Repo(match.group("owner"), match.group("repo"))


def site_of(title: str, host: str | None = None) -> str | None:
    """The work site a tab's title (or, failing that, its `host`) belongs to, as its
    group title, or None. Calls never group."""
    text = title.strip()
    if is_call(text):
        return None
    identity = identify(text)
    if identity is not None and identity.site in _SITE_OF_RULE:
        return _SITE_OF_RULE[identity.site]
    for site, pattern in _SITE_TITLES.items():
        if pattern.search(text):
            return site
    return site_of_host(host) if host is not None else None


def site_of_host(host: str, sites: SiteHosts | None = None) -> str | None:
    """The site group a host belongs to, from `config/chrome-site-groups.json`; calls never group."""
    name = host.lower()
    if any(name == call or name.endswith("." + call) for call in _CALL_HOSTS):
        return None
    return (sites or default_site_hosts()).site_for(name)


#: First path segments of github.com pages that are not `owner/repo`.
_GITHUB_NON_OWNERS = frozenset(
    {
        "orgs",
        "users",
        "search",
        "settings",
        "notifications",
        "pulls",
        "issues",
        "marketplace",
        "sponsors",
        "topics",
        "login",
        "new",
        "organizations",
        "apps",
        "features",
        "explore",
        "codespaces",
        "collections",
        "trending",
        "about",
        "enterprise",
        "dashboard",
        "account",
    }
)
_NO_OWNERS = UrlOwners()


def repo_of_url(url: str | None, owners: UrlOwners = _NO_OWNERS) -> Repo | None:
    """The repo a tab's URL names: a GitHub repo page, or a local or Vercel URL a project declares."""
    if url is None:
        return None
    parts = urlsplit(url)
    if (parts.hostname or "").lower() in ("github.com", "www.github.com"):
        segments = [segment for segment in parts.path.split("/") if segment]
        if len(segments) >= 2 and segments[0].lower() not in _GITHUB_NON_OWNERS:
            return Repo(segments[0], segments[1])
        return None
    slug = owners.slug_for(url)
    if slug is None:
        return None
    owner, _, name = slug.partition("/")
    return Repo(owner, name)


def tab_key(tab: ChromeTab, hosts: Mapping[str, str], owners: UrlOwners = _NO_OWNERS) -> str | None:
    """The repo or site a tab groups under, from its title first and then its URL."""
    repo = repo_of(tab.title) or repo_of_url(tab.url, owners)
    if repo is not None:
        return str(repo)
    host = urlsplit(tab.url).hostname if tab.url else hosts.get(tab.title)
    return site_of(tab.title, host)


def key_of(title: str, host: str | None = None) -> str | None:
    """The repo (`owner/repo`) or site a tab groups under, or None."""
    repo = repo_of(title)
    return str(repo) if repo is not None else site_of(title, host)


@dataclass(frozen=True, slots=True)
class GroupMove:
    tab: ChromeTab
    #: 1-based number of the tab's window, in the order the windows were given.
    window_number: int
    profile: str
    #: The repo (`owner/repo`) or site the tab belongs to.
    key: str
    title: str
    #: True for the tab that creates the group; every later move joins it. A creating
    #: tab from another window first moves to the home window, then creates it there.
    new: bool
    #: The window the group lives in; a tab from another window moves there.
    home_hwnd: int
    home_number: int

    @property
    def crosses(self) -> bool:
        return self.tab.window_hwnd != self.home_hwnd


@dataclass(frozen=True, slots=True)
class Ungroup:
    """Dissolve a duplicate group, so its tabs can join the key's home group."""

    group: TabGroup
    tabs: tuple[ChromeTab, ...]
    window_number: int
    profile: str


@dataclass(frozen=True, slots=True)
class SkippedWindow:
    window_number: int
    profile: str
    reason: str


@dataclass(frozen=True, slots=True)
class BlockedKey:
    profile: str
    title: str
    reason: str


@dataclass(frozen=True, slots=True)
class GroupPlan:
    #: In execution order: window by window, each window's ungroups before its moves.
    moves: tuple[GroupMove, ...]
    skipped: tuple[SkippedWindow, ...]
    #: Tabs already in the group they would go to.
    already_grouped: int
    #: Tabs left alone because they sit in some other group, by hand.
    elsewhere: int
    ungroups: tuple[Ungroup, ...] = ()
    blocked: tuple[BlockedKey, ...] = ()

    def window_numbers(self) -> list[int]:
        """The windows whose tabs move, in the order to work through them."""
        return list(dict.fromkeys(move.window_number for move in self.moves))

    def ungroup_window_numbers(self) -> list[int]:
        """The windows with duplicates to dissolve. Every ungroup runs before any move, so
        each join finds exactly one group with its title."""
        return list(dict.fromkeys(ungroup.window_number for ungroup in self.ungroups))


@dataclass(frozen=True, slots=True)
class _NumberedWindow:
    """A window with its 1-based number in the order the windows were given."""

    number: int
    window: ChromeWindow


@dataclass(slots=True)
class _Found:
    window: ChromeWindow
    number: int
    tab: ChromeTab


def _empty_found_list() -> list[_Found]:
    return []


@dataclass(slots=True)
class _Key:
    key: str
    title: str
    repo: bool
    tabs: list[_Found] = field(default_factory=_empty_found_list)


def plan_groups(
    windows: Sequence[ChromeWindow],
    *,
    current_desktop: int | None,
    min_size: int = 2,
    all_desktops: bool = True,
    hosts: Mapping[str, Mapping[str, str]] | None = None,
    owners: UrlOwners = _NO_OWNERS,
) -> GroupPlan:
    """Pass the windows in the order to work through them (screen order); ties go to the earlier.

    `hosts` maps a profile key to the host history ties each title to (see `history`);
    `owners` ties local and Vercel URLs to the projects that declare them."""
    moves: list[GroupMove] = []
    ungroups: list[Ungroup] = []
    skipped: list[SkippedWindow] = []
    blocked: list[BlockedKey] = []
    already = elsewhere = 0

    profiles: dict[str, list[_NumberedWindow]] = defaultdict(list)
    for number, window in enumerate(windows, start=1):
        profiles[window.profile_key].append(_NumberedWindow(number, window))

    for profile_key, profile_windows in profiles.items():
        eligible: set[int] = set()
        for numbered in profile_windows:
            number, window = numbered.number, numbered.window
            if not all_desktops and current_desktop is not None and window.desktop_number != current_desktop:
                where = (
                    f"desktop {window.desktop_number}" if window.desktop_number is not None else "an unknown desktop"
                )
                skipped.append(SkippedWindow(number, _profile(window), f"on {where}, not the current one"))
            else:
                eligible.add(window.hwnd)
        result = _plan_profile(profile_windows, eligible, min_size, (hosts or {}).get(profile_key, {}), owners)
        moves.extend(result.moves)
        ungroups.extend(result.ungroups)
        blocked.extend(result.blocked)
        already += result.already_grouped
        elsewhere += result.elsewhere

    order = _execution_order(moves)
    moves.sort(key=lambda move: _step(move, order[move.window_number]))
    ungroups.sort(key=lambda ungroup: ungroup.window_number)
    skipped.sort(key=lambda skip: skip.window_number)
    return GroupPlan(tuple(moves), tuple(skipped), already, elsewhere, tuple(ungroups), tuple(blocked))


def _profile(window: ChromeWindow) -> str:
    return window.profile_name or window.profile_key


def _plan_profile(
    profile_windows: Sequence[_NumberedWindow],
    eligible: set[int],
    min_size: int,
    hosts: Mapping[str, str],
    owners: UrlOwners,
) -> GroupPlan:
    keys = _keys(profile_windows, hosts, owners)
    groups: dict[str, dict[tuple[int, ...], list[_Found]]] = defaultdict(dict)
    for numbered in profile_windows:
        number, window = numbered.number, numbered.window
        for tab in window.tabs:
            if tab.group is not None:
                groups[tab.group.title].setdefault(tab.group.runtime_id, []).append(_Found(window, number, tab))
    review = _review_window(keys.values(), eligible)

    moves: list[GroupMove] = []
    ungroups: list[Ungroup] = []
    blocked: list[BlockedKey] = []
    already = elsewhere = 0
    for spec in keys.values():
        live = [found for found in spec.tabs if found.window.hwnd in eligible]
        loose = [found for found in live if found.tab.group is None]
        elsewhere += sum(1 for found in live if found.tab.group is not None and found.tab.group.title != spec.title)
        every_group = groups.get(spec.title, {})
        titled = [members for members in every_group.values() if members[0].window.hwnd in eligible]
        if not titled and not loose:
            continue
        profile = _profile(spec.tabs[0].window)

        away = sorted(
            {members[0].window.desktop_number or 0 for members in every_group.values()}
            - {members[0].window.desktop_number or 0 for members in titled}
        )
        if len(titled) < len(every_group):
            if loose or len(every_group) > 1:
                desktops = ", ".join(str(number) for number in away)
                blocked.append(BlockedKey(profile, spec.title, f"its group is on desktop {desktops}"))
            already += sum(len(members) for members in titled)
            continue
        if not titled and len(loose) < min_size:
            continue

        ranked = sorted(titled, key=lambda members: (-len(members), members[0].number))
        target = review if spec.repo else None
        if target is not None:
            home = next((members for members in ranked if members[0].window.hwnd == target.window.hwnd), None)
        else:
            home = ranked[0] if ranked else None
        duplicates = [members for members in ranked if members is not home]
        if any(tab_key(found.tab, hosts, owners) != spec.key for members in duplicates for found in members):
            blocked.append(
                BlockedKey(profile, spec.title, f"{len(titled)} groups share its title, one holds other tabs")
            )
            already += sum(len(members) for members in titled)
            continue

        already += len(home) if home is not None else 0
        joiners = sorted(
            [*loose, *(found for members in duplicates for found in members)],
            key=lambda found: (found.number, found.tab.index),
        )
        for members in duplicates:
            group = members[0].tab.group
            assert group is not None
            ungroups.append(Ungroup(group, tuple(found.tab for found in members), members[0].number, profile))
        if home is not None:
            moves.extend(_move(found, spec, profile, home[0].window, home[0].number, new=False) for found in joiners)
            continue

        if target is None:
            per_window: dict[int, list[_Found]] = defaultdict(list)
            for found in joiners:
                per_window[found.number].append(found)
            best = max(per_window, key=lambda number: (len(per_window[number]), -number))
            target = per_window[best][0]
        in_home = [found for found in joiners if found.window.hwnd == target.window.hwnd]
        creator = in_home[0] if in_home else joiners[0]
        moves.append(_move(creator, spec, profile, target.window, target.number, new=True))
        moves.extend(
            _move(found, spec, profile, target.window, target.number, new=False)
            for found in joiners
            if found is not creator
        )
    return GroupPlan(tuple(moves), (), already, elsewhere, tuple(ungroups), tuple(blocked))


def _review_window(keys: Iterable[_Key], eligible: set[int]) -> _Found | None:
    """The window holding the most GitHub tabs, where every repo group lives."""
    counts: dict[int, list[_Found]] = defaultdict(list)
    for spec in keys:
        if spec.repo:
            for found in spec.tabs:
                if found.window.hwnd in eligible:
                    counts[found.number].append(found)
    if not counts:
        return None
    best = max(counts, key=lambda number: (len(counts[number]), -number))
    return counts[best][0]


def _keys(profile_windows: Sequence[_NumberedWindow], hosts: Mapping[str, str], owners: UrlOwners) -> dict[str, _Key]:
    """Every repo and site among the profile's movable tabs, titled so no two share a title."""
    repos: dict[str, Repo] = {}
    keys: dict[str, _Key] = {}
    for numbered in profile_windows:
        number, window = numbered.number, numbered.window
        for tab in window.tabs:
            if tab.pinned:
                continue
            repo = repo_of(tab.title) or repo_of_url(tab.url, owners)
            key = tab_key(tab, hosts, owners)
            if key is None:
                continue
            if repo is not None:
                repos[key] = repo
            keys.setdefault(key, _Key(key, key, repo is not None)).tabs.append(_Found(window, number, tab))
    name_counts: dict[str, int] = defaultdict(int)
    for repo in repos.values():
        name_counts[repo.name.casefold()] += 1
    for key, repo in repos.items():
        if name_counts[repo.name.casefold()] == 1:
            keys[key].title = repo.name
    return keys


def _move(found: _Found, spec: _Key, profile: str, home: ChromeWindow, home_number: int, *, new: bool) -> GroupMove:
    return GroupMove(found.tab, found.number, profile, spec.key, spec.title, new, home.hwnd, home_number)


@dataclass(frozen=True, slots=True, order=True)
class _Step:
    window: int
    phase: int
    index: int


def _step(move: GroupMove, window_rank: int) -> _Step:
    """Window by window; within one: create groups, then join groups here, then send tabs to other windows."""
    phase = 0 if move.new else 2 if move.crosses else 1
    return _Step(window_rank, phase, move.tab.index)


def _execution_order(moves: Sequence[GroupMove]) -> dict[int, int]:
    """Rank per window number: windows that create a group go first, so every later join
    finds its group, then the rest; each set in the order the windows were given."""
    creating = sorted({move.window_number for move in moves if move.new})
    rest = sorted({move.window_number for move in moves})
    ordered = creating + [number for number in rest if number not in creating]
    return {number: rank for rank, number in enumerate(ordered)}
