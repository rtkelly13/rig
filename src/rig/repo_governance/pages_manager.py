"""GitHub Pages and custom domain governance manager.

Audits and enforces GitHub Pages configuration, build types (workflow vs legacy),
custom domain (CNAME) assignments, and HTTPS enforcement via the GitHub API.
"""

from __future__ import annotations

from collections.abc import Sequence
from dataclasses import dataclass
from typing import Literal, override

from rig.proc import ApiRequest, GitHubApi, gh_api
from rig.repo_governance.policy_config import PagesPolicy, PolicyConfig
from rig.repo_governance.resource import Change, GitHubReadError, Resource, Target, is_not_found
from rig.typed_json import encode_json, try_decode_json

__all__ = [
    "PagesResource",
    "align_pages",
    "inspect_pages",
    "pages_drift",
]


@dataclass(frozen=True, slots=True)
class PagesInspection:
    """A repo's GitHub Pages configuration against its policy."""

    enabled: bool
    cname: str | None
    build_type: str | None
    https_enforced: bool
    status: str | None
    html_url: str | None
    is_aligned: bool
    drift_reasons: tuple[str, ...]
    error: str | None = None


@dataclass(frozen=True, slots=True)
class PagesAlignResult:
    """Outcome of aligning a repo's GitHub Pages with its policy."""

    status: str
    repo: str
    details: str | None = None
    action: str | None = None
    error: str | None = None
    actions: tuple[str, ...] = ()
    actions_taken: tuple[str, ...] = ()
    drift_reasons: tuple[str, ...] = ()


@dataclass(frozen=True, slots=True)
class _Pages:
    cname: str | None = None
    build_type: str | None = None
    https_enforced: bool = False
    status: str | None = None
    html_url: str | None = None


@dataclass(frozen=True, slots=True)
class _EnablePages:
    build_type: Literal["workflow", "legacy"]


@dataclass(frozen=True, slots=True)
class _ConfigurePages:
    cname: str
    https_enforced: bool


@dataclass(frozen=True, slots=True)
class _EnforceHttps:
    """A configure body with no `cname`: GitHub reads `"cname": null` as removing the custom domain."""

    https_enforced: bool


def read_pages(api: GitHubApi, repo_name: str) -> _Pages | None:
    """The repo's Pages site, or None when Pages is off (GitHub's 404); raises when it cannot be read."""
    route = f"repos/{repo_name}/pages"
    ran = api(ApiRequest("GET", route))
    if not ran.ok:
        if is_not_found(ran):
            return None
        raise GitHubReadError(f"GET {route}: {ran.err.strip() or f'exit {ran.code}'}")
    pages = try_decode_json(_Pages, ran.out)
    if pages is None:
        raise GitHubReadError(f"GET {route}: unreadable response")
    return pages


def pages_drift(expected: PagesPolicy | None, pages: _Pages | None) -> list[str]:
    """Why *pages* (None when Pages is off) does not match *expected*; empty when it does."""
    if pages is None:
        return ["Pages is disabled"] if expected and expected.enabled else []
    if expected is None:
        return []
    if not expected.enabled:
        return ["Pages is enabled, expected disabled"]
    drift: list[str] = []
    if pages.build_type != expected.build_type:
        drift.append(f"build_type is '{pages.build_type}', expected '{expected.build_type}'")
    if expected.cname and pages.cname != expected.cname:
        drift.append(f"cname is '{pages.cname}', expected '{expected.cname}'")
    if expected.https_enforced and not pages.https_enforced:
        drift.append("https_enforced is False, expected True")
    return drift


def inspect_pages(repo_name: str, expected: PagesPolicy | None = None, api: GitHubApi = gh_api) -> PagesInspection:
    """Inspects the current GitHub Pages configuration for *repo_name* against *expected*."""
    try:
        pages = read_pages(api, repo_name)
    except GitHubReadError as exc:
        return PagesInspection(
            enabled=False,
            cname=None,
            build_type=None,
            https_enforced=False,
            status="error",
            html_url=None,
            is_aligned=False,
            drift_reasons=(),
            error=str(exc),
        )
    drift = pages_drift(expected, pages)
    if pages is None:
        return PagesInspection(
            enabled=False,
            cname=None,
            build_type=None,
            https_enforced=False,
            status="disabled",
            html_url=None,
            is_aligned=expected is not None and not expected.enabled,
            drift_reasons=tuple(drift),
        )
    return PagesInspection(
        enabled=True,
        cname=pages.cname,
        build_type=pages.build_type,
        https_enforced=pages.https_enforced,
        status=pages.status,
        html_url=pages.html_url,
        is_aligned=not drift,
        drift_reasons=tuple(drift),
    )


def _pages_writes(repo_name: str, policy: PagesPolicy, enabled: bool) -> list[ApiRequest]:
    """The writes that bring an enabled policy's Pages site into line: enable it, then configure it."""
    route = f"repos/{repo_name}/pages"
    writes: list[ApiRequest] = []
    if not enabled:
        writes.append(ApiRequest("POST", route, encode_json(_EnablePages, _EnablePages(policy.build_type))))
    if policy.cname:
        body = _ConfigurePages(cname=policy.cname, https_enforced=policy.https_enforced)
        writes.append(ApiRequest("PUT", route, encode_json(_ConfigurePages, body)))
    elif policy.https_enforced:
        writes.append(ApiRequest("PUT", route, encode_json(_EnforceHttps, _EnforceHttps(https_enforced=True))))
    return writes


def _describe(request: ApiRequest) -> str:
    return f"{request.method} {request.route} {request.body or ''}".rstrip()


def align_pages(
    repo_name: str, policy: PagesPolicy, dry_run: bool = False, api: GitHubApi = gh_api
) -> PagesAlignResult:
    """Applies GitHub Pages configuration according to *policy*.

    - Enables Pages with the policy's build type if not currently enabled.
    - Configures custom domain CNAME and enforces HTTPS.
    """
    current = inspect_pages(repo_name, expected=policy, api=api)

    if current.is_aligned:
        return PagesAlignResult(status="aligned", repo=repo_name, details="Pages already aligned with policy")

    writes = _pages_writes(repo_name, policy, current.enabled) if policy.enabled else []
    planned_actions = tuple(_describe(w) for w in writes)
    if dry_run:
        return PagesAlignResult(
            status="dry_run", repo=repo_name, actions=planned_actions, drift_reasons=current.drift_reasons
        )
    for write in writes:
        ran = api(write)
        if not ran.ok:
            action = "enable_pages" if write.method == "POST" else "configure_pages_cname"
            return PagesAlignResult(status="error", action=action, repo=repo_name, error=ran.err.strip())
    return PagesAlignResult(status="success", repo=repo_name, actions_taken=planned_actions)


class PagesResource(Resource[PagesPolicy, "_Pages | None"]):
    """The GitHub Pages site, for repos whose policy enables it; a repo that declares nothing is left alone."""

    @property
    @override
    def name(self) -> str:
        return "pages"

    @override
    def desired(self, cfg: PolicyConfig, target: Target) -> PagesPolicy | None:
        policy = cfg.pages(target.slug)
        return policy if policy.enabled else None

    @override
    def read(self, api: GitHubApi, target: Target) -> _Pages | None:
        return read_pages(api, target.slug)

    @override
    def diff(self, desired: PagesPolicy, observed: _Pages | None) -> list[Change]:
        drift = pages_drift(desired, observed)
        if not drift:
            return []
        return [Change(self.name, "enable" if observed is None else "update", "; ".join(drift))]

    @override
    def apply(self, api: GitHubApi, target: Target, desired: PagesPolicy, changes: Sequence[Change]) -> list[str]:
        if not any(change.kind == "apply" for change in changes):
            return []
        enabled = all(change.action != "enable" for change in changes)
        failures: list[str] = []
        for write in _pages_writes(target.slug, desired, enabled):
            ran = api(write)
            if not ran.ok:
                failures.append(f"{_describe(write)}: {ran.err.strip()}")
                break
        return failures
