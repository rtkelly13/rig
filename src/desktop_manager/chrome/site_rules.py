"""Per-site rules that turn a tab title into an identity key, for de-duplicating non-PR tabs.

UI Automation shows a tab's title, never its URL, so two tabs are "the same
page" only when a rule says their titles name the same resource. Each rule is
an anchored regex over the title (Chrome's suffixes already stripped) and a key
template over its named groups; the first matching rule wins. Tabs with equal
keys in one profile are duplicates.

Three safety layers, in order:

1. `NEVER` - titles that name no specific resource (app shells, sign-in and
   404 pages, live calls, pages whose environment or account is not in the
   title). These are never keyed, so never closed.
2. `exact_only` rules key variant titles together (`X` and `X - Claude`) but
   only close a tab whose title equals the kept tab's exactly; a variant is
   reported as a possible duplicate instead. A claude.ai cowork session and a
   mid-login redirect both borrow an artifact's name, which is why.
3. `auto=False` rules only ever report.

The rule table was derived from the user's Chrome history (2026-09-25); see
docs/desktop-manager/chrome-tabs-uia.md. GitHub PR pages are not keyed here - `pr_prune` owns them.
"""

from __future__ import annotations

import re
from dataclasses import dataclass

_DOT = "·"
_REPO = r"(?P<repo>[\w.-]+/[\w.-]+)"
_GH_TAIL = rf"(?: {_DOT} GitHub)?"


@dataclass(frozen=True)
class SiteRule:
    site: str
    pattern: re.Pattern[str]
    #: `str.format` template over the pattern's named groups.
    key: str
    #: False: a match is only ever reported, never closed.
    auto: bool = True
    #: True: a duplicate closes only when its title equals the kept tab's exactly.
    exact_only: bool = False


@dataclass(frozen=True)
class TabIdentity:
    site: str
    key: str
    auto: bool
    exact_only: bool


def _rule(site: str, pattern: str, key: str, *, auto: bool = True, exact_only: bool = False) -> SiteRule:
    return SiteRule(site, re.compile(pattern), key, auto=auto, exact_only=exact_only)


NEVER: tuple[re.Pattern[str], ...] = tuple(
    re.compile(pattern)
    for pattern in (
        # claude.ai / Claude Code shells: every session, chat and edit view shares these.
        r"^Claude$",
        r"^Claude Code$",
        r"^New (?:chat|task) - Claude$",
        r"^Artifact - Claude$",
        # Sign-in and 404 pages on any site.
        r"^(?:Sign in|Log in|Log In|Login|Page not found)\b",
        r"Authentication$",
        r"^Redirecting",
        # GitHub
        rf"^Page not found {_DOT} GitHub",
        r"^GitHub$",
        r"^Sign in to GitHub",
        r"^Verify Session$",
        r"^Two-factor authentication",
        r"^OAuth application authorized$",
        # Google: a Meet tab may be a live call, and meeting names repeat daily.
        r"^Google Meet$",
        r"^Meet [-–] ",
        r"^Google$",
        r"^Gmail$",
        r"^Google Drive$",
        r"^(?:Home|My Drive) - Google Drive$",
        r" - Google Search$",
        r"^Sign in - Google Accounts$",
        # App shells that title every page the same.
        r"^Vercel$",
        r"^Slack$",
        r"^Ring$",
        r"^(?:My )?Dashboard$",
        r" - Team Settings - Dashboard$",
        r"^Kibana$",
        r"^Asana$",
        r"^Miro$",
        r"^Authorize Application",
        r"^Sign in to your account$",
        # AWS: the account is never in the title, and list pages are generic.
        r"^(?:Dashboard|Databases|Console home) \| ",
        r" - AWS Developer Tools$",
        r"^Amazon Web Services Sign-In$",
        # Rights App: LIVE, clone-N and localhost title their pages identically.
        r" - RightsApp Administration$",
        r"^Runtime Error$",
        # Locally generated reports share titles across fixture folders.
        r"^Datalake schema$",
        r"^Schema graph$",
        # Unloaded tabs showing their URL, or nothing.
        r"^https?://",
        r"^(?:file|chrome|about|edge):",
        r"^\s*$",
    )
)

RULES: tuple[SiteRule, ...] = (
    _rule("github-issue", rf"^.+ {_DOT} Issue #(?P<n>\d+) {_DOT} {_REPO}{_GH_TAIL}$", "gh-issue:{repo}#{n}"),
    _rule("github-file", rf"^(?P<path>\S+) at (?P<ref>\S+) {_DOT} {_REPO}{_GH_TAIL}$", "gh-blob:{repo}@{ref}:{path}"),
    _rule(
        "github-list",
        rf"^(?P<kind>Pull requests|Issues|Actions|Commits|Releases|Branches) {_DOT} {_REPO}{_GH_TAIL}$",
        "gh-list:{repo}:{kind}",
    ),
    # Re-runs and matrix jobs share a title, so a match is only reported.
    _rule(
        "github-run", rf"^(?P<t>.+) {_DOT} {_REPO}@(?P<sha>[0-9a-f]{{7,40}})$", "gh-run:{repo}@{sha}:{t}", auto=False
    ),
    _rule("youtrack", r"^.+ : (?P<id>[A-Z][A-Z0-9]+-\d+)$", "youtrack:{id}"),
    _rule("jira", r"^\[(?P<id>[A-Z][A-Z0-9]+-\d+)\] .+ - (?:JIRA|Jira)$", "jira:{id}"),
    # The unread count changes over time; the account and folder do not.
    _rule(
        "gmail", r"^(?P<folder>[^()]+?)(?: \(\d+\))? - (?P<acct>\S+@\S+) - .*(?:Gmail|Mail)$", "gmail:{acct}:{folder}"
    ),
    _rule("google-calendar", r"^(?P<acct>.+?) – Calendar - (?P<view>.+)$", "gcal:{acct}:{view}"),
    _rule("google-docs", r"^(?P<t>.+) - Google (?P<kind>Docs|Sheets|Slides|Forms)$", "gdoc:{kind}:{t}"),
    _rule(
        "google-drive",
        r"^(?!(?:Home|My Drive|Search results|Shared with me|Recent) - )(?P<t>.+) - Google Drive$",
        "gdrive:{t}",
    ),
    # A leading dot is Asana's unread marker.
    _rule("asana", r"^(?:● )?(?!(?:Home|Log in|My tasks|Inbox) - )(?P<t>.+) - Asana$", "asana:{t}"),
    _rule(
        "aws",
        r"^(?P<res>.+?) - (?P<page>[^|]+Details) \| (?P<svc>[^|]+) \| (?P<region>[a-z]{2}-[a-z]+-\d)$",
        "aws:{region}:{svc}:{res}",
    ),
    _rule("nuget", r"^NuGet Gallery \| (?P<t>(?!Home$|Sign In$).+)$", "nuget:{t}"),
    # claude.ai loads one artifact titled either `X` or `X - Claude`; key both as the bare title.
    _rule("claude", r"^(?P<t>.+) - Claude$", "title:{t}", exact_only=True),
    # Anything else: the exact title. A one-word title names too little to trust.
    _rule("title", r"^(?P<t>\S+(?:\s+\S+)+)$", "title:{t}"),
    _rule("title", r"^(?P<t>.+)$", "title:{t}", auto=False),
)


#: Video-call pages. Kept by every rule even when muted with the camera off, which
#: Chrome's alerts cannot show. Meet's titles are from history; the rest are the
#: services' documented web-client titles, not yet seen live.
CALLS: tuple[re.Pattern[str], ...] = tuple(
    re.compile(pattern)
    for pattern in (
        r"^Meet [-–] ",
        r"^(?:Google )?Meet$",
        r"^Zoom(?: Meeting| Workplace| Webinar)?\b",
        r"\| Microsoft Teams$",
        r"^(?:Huddle|Call)\b.* - Slack$",
        r"\bWebex\b",
        r"^Jitsi Meet\b",
        r" \| Whereby$",
    )
)


def is_call(title: str) -> bool:
    return any(pattern.search(title.strip()) for pattern in CALLS)


def never_dedupe(title: str) -> bool:
    return is_call(title) or any(pattern.search(title) for pattern in NEVER)


def identify(title: str) -> TabIdentity | None:
    """The identity key a stripped tab title names, or None when it names nothing trustworthy."""
    text = title.strip()
    if never_dedupe(text):
        return None
    for rule in RULES:
        match = rule.pattern.match(text)
        if match is not None:
            key = rule.key.format(**{name: value.strip() for name, value in match.groupdict().items()})
            return TabIdentity(rule.site, key, rule.auto, rule.exact_only)
    return None
