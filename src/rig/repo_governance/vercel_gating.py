"""Vercel deployment-creation gating for repos in the estate.

Vercel's Hobby plan allows 100 deployments *created* per day, account-wide, and the
count is spent at creation — a deployment that ``ignoreCommand`` cancels one second
later still burns the budget it took to create it. Gating at the build step is
therefore not gating at all: on 2026-09-11 the design-system repo's PR traffic created
83 deployments in a day, 79 canceled. The same repo's production Storybook had served
the same stale build for two days meanwhile — its real cause (an unexposed system-env
project setting that left ``ignoreCommand`` comparing an empty branch name) was found
only once the empirical rules below started counting what Vercel actually did instead
of what the config claimed.

The rule this module checks: a Vercel-linked project must never *create* a deployment
for a push that cannot serve anything — feature branches and PRs. Either the project
does not create deployments on branch pushes at all (the legacy flag absent, or
``createDeployments: disabled``), or the repo's ``vercel.json`` declares a
``git.deploymentEnabled`` allowlist with a deny-all fallback. Observed deployments in
a recent window back that up empirically: whatever the flags claim, a deployment
created off the allowed refs is budget burning down.

Like the other checks here, the judgement is a pure function over facts
(``plan_project_findings``); reading Vercel and the local checkout is separate, so
this is testable without an account.
"""

from __future__ import annotations

import fnmatch
import os
import pathlib
import shutil
import subprocess
import time
from dataclasses import dataclass
from typing import Annotated

import httpx
from pydantic import Field, StrictBool, StrictFloat, StrictInt, StrictStr

from rig import proc
from rig.typed_json import Lenient, load_json, try_decode, try_decode_json

__all__ = [
    "CreatedDeployment",
    "Finding",
    "ProjectFacts",
    "audit_projects",
    "plan_project_findings",
    "ref_is_allowed",
    "CliAuth",
    "token_is_live",
    "vercel_token",
]

# Where the Vercel CLI persists its OAuth token, in preference order. The CLI is the
# credential; requiring a second, hand-copied token is how config drifts stale.
_CLI_AUTH_CANDIDATES = (
    pathlib.Path("Library/Application Support/com.vercel.cli/auth.json"),
    pathlib.Path(".config/com.vercel.cli/auth.json"),
)

API = "https://api.vercel.com"


@dataclass(frozen=True)
class CreatedDeployment:
    """One deployment as Vercel *created* it, in the audit window."""

    ref: str
    target: str | None
    state: str
    created_at_ms: int
    commit_sha: str | None = None


@dataclass(frozen=True)
class ProjectFacts:
    """Everything the audit knows about one Vercel project. Pure input.

    ``allow``/``deny`` are the true/false values of a ``git.deploymentEnabled`` map
    in the repo's own ``vercel.json``; ``allow is None`` means the repo declares no
    map at all. ``head_sha`` is the tip of the production branch in the local
    checkout, when there is one.
    """

    name: str
    repo: str | None
    create_deployments: str | None
    production_ref: str
    allow: tuple[str, ...] | None
    deny: tuple[str, ...] | None
    recent_deployments: tuple[CreatedDeployment, ...]
    head_sha: str | None = None


@dataclass(frozen=True)
class Finding:
    project: str
    rule: str
    severity: str  # "error" | "warn"
    detail: str


@dataclass(frozen=True, slots=True)
class ProjectAudit:
    """One project's facts and what the audit found in them."""

    facts: ProjectFacts
    findings: list[Finding]


def ref_is_allowed(ref: str, facts: ProjectFacts) -> bool:
    """Return True if a deployment created from *ref* is within budget etiquette."""
    if ref == facts.production_ref:
        return True
    return bool(facts.allow and any(fnmatch.fnmatch(ref, pattern) for pattern in facts.allow))


def plan_project_findings(facts: ProjectFacts, *, now_ms: int, window_days: int) -> list[Finding]:
    """Audit one project's facts. All three rules, no I/O.

    Severity split: a *creation* finding is an error because it spends money-or-equals
    (the quota) on every push whether or not anything is broken yet; a freshness
    finding is a warning because a quiet production branch is legitimately quiet.
    """
    findings: list[Finding] = []
    window_ms = now_ms - window_days * 86_400_000
    recent = [d for d in facts.recent_deployments if d.created_at_ms >= window_ms]

    # 1. The creation gate. ``enabled`` with no repo-level deny-all means every
    #    branch push creates a deployment; skipping happens after creation.
    if facts.create_deployments == "enabled" and not _has_deny_all(facts):
        findings.append(
            Finding(
                facts.name,
                "creation-gate",
                "error",
                "creates a deployment for every branch push; ignored builds still "
                "count toward the 100/day. Add a git.deploymentEnabled allowlist with "
                "a '**: false' fallback to the repo's vercel.json, or turn off "
                "deployment creation for branches in project settings.",
            )
        )
    elif facts.allow is not None and not _has_deny_all(facts):
        findings.append(
            Finding(
                facts.name,
                "allowlist-has-no-deny-all",
                "warn",
                "git.deploymentEnabled lists no deny-all fallback, so unlisted "
                "branches still create deployments by default.",
            )
        )

    # 2. The empirical leak: whatever the flags say, deployments were *created* off
    #    the allowed refs in the window. This is the rule that cannot be fooled by
    #    a pattern that silently does not match (e.g. '*' against slashed branches).
    leaked = [d for d in recent if not ref_is_allowed(d.ref, facts)]
    if leaked:
        by_ref: dict[str, int] = {}
        for d in leaked:
            by_ref[d.ref] = by_ref.get(d.ref, 0) + 1
        listing = ", ".join(f"{ref} ×{n}" for ref, n in sorted(by_ref.items()))
        findings.append(
            Finding(
                facts.name,
                "budget-leak",
                "error",
                f"created {len(leaked)} deployment(s) off the allowed refs in the "
                f"last {window_days}d — each one burns the daily budget: {listing}",
            )
        )

    # 3. Production freshness, the stale-storybook class: the served build is behind
    #    the branch it is supposed to serve.
    findings.extend(_freshness_findings(facts, recent))

    return findings


def _has_deny_all(facts: ProjectFacts) -> bool:
    """True if the repo's allowlist denies everything that is not named."""
    if facts.allow is None or not facts.deny:
        return False
    return any(pattern in ("*", "**") for pattern in facts.deny)


def _freshness_findings(facts: ProjectFacts, recent: list[CreatedDeployment]) -> list[Finding]:
    prod_ready = [d for d in recent if d.target == "production" and d.state == "READY"]
    if facts.head_sha is None:
        # No checkout to compare against; "no production build at all in the window"
        # is still reportable when there were pushes.
        pushes = [d for d in recent if d.ref == facts.production_ref]
        if pushes and not prod_ready:
            return [
                Finding(
                    facts.name,
                    "production-stale",
                    "warn",
                    f"{len(pushes)} push(es) to {facts.production_ref} in the window, "
                    "none with a Ready production deployment.",
                )
            ]
        return []
    if not prod_ready:
        return [
            Finding(
                facts.name,
                "production-stale",
                "warn",
                f"no Ready production deployment in the window while origin/{facts.production_ref} "
                f"is at {facts.head_sha[:7]}.",
            )
        ]
    served = prod_ready[0].commit_sha
    if served and served != facts.head_sha:
        return [
            Finding(
                facts.name,
                "production-stale",
                "warn",
                f"latest Ready production deployment is {served[:7]}, origin/"
                f"{facts.production_ref} is {facts.head_sha[:7]} — production is behind.",
            )
        ]
    return []


# -- I/O ------------------------------------------------------------


@dataclass(frozen=True, slots=True)
class CliAuth:
    """The Vercel CLI's auth file: its token and, for a rotating OAuth token, when that expires."""

    token: Lenient[StrictStr] = None
    expires_at: Annotated[Lenient[StrictInt | StrictFloat], Field(alias="expiresAt")] = None


@dataclass(frozen=True, slots=True)
class _VercelGitConfig:
    deployment_enabled: Annotated[
        Lenient[StrictBool | dict[str, Lenient[StrictBool]]], Field(alias="deploymentEnabled")
    ] = None


@dataclass(frozen=True, slots=True)
class _VercelJson:
    """The part of a repo's ``vercel.json`` the deployment rules read."""

    git: Lenient[_VercelGitConfig] = None


def token_is_live(entry: CliAuth, now_s: float) -> bool:
    """True if a CLI auth entry's token is usable right now.

    The CLI stores a rotating OAuth token with an ``expiresAt``; a direct API
    caller that reads the file without checking gets a 403 minutes after the
    CLI last refreshed it. Absent expiry is treated as a long-lived token.
    """
    expires_at = entry.expires_at
    if expires_at is None or expires_at <= 0:
        return True
    # The CLI has written this both ways across versions: epoch seconds, and
    # epoch milliseconds. Anything past 1e12 can only be the latter.
    expires_s = expires_at / 1000 if expires_at > 1e12 else expires_at
    # Refresh 60s early; a request that starts valid and lands expired is worse
    # than one needless CLI round-trip.
    return expires_s > now_s + 60


def vercel_token() -> str:
    """The CLI's own token — refreshing through the CLI when it has expired.

    ``VERCEL_TOKEN`` (a long-lived API token) always wins. Otherwise the CLI's
    OAuth token is used, and an expired one is renewed by asking the CLI to do
    what it does on every command — run ``vercel whoami``, which rotates the
    pair and rewrites its own file. This tool never refreshes the OAuth grant
    itself and never writes to the CLI's state.
    """
    env = os.environ.get("VERCEL_TOKEN")
    if env:
        return env
    now_s = time.time()
    for candidate in _cli_auth_candidates():
        entry = _read_json(CliAuth, candidate) or CliAuth()
        if entry.token is None:
            continue
        if not token_is_live(entry, now_s):
            refreshed = _refresh_through_cli(candidate)
            if refreshed:
                return refreshed
            if not token_is_live(entry, now_s):
                continue
        return entry.token
    raise RuntimeError("no usable Vercel token: `vercel login` or set VERCEL_TOKEN")


def _cli_auth_candidates() -> list[pathlib.Path]:
    home = pathlib.Path.home()
    return [home / rel for rel in _CLI_AUTH_CANDIDATES]


def _refresh_through_cli(auth_file: pathlib.Path) -> str | None:
    """Let the CLI rotate its own token, then re-read it. None if either step fails."""
    if shutil.which("vercel") is None:
        return None
    subprocess.run(["vercel", "whoami"], capture_output=True, text=True)
    entry = _read_json(CliAuth, auth_file)
    return entry.token if entry is not None and entry.token else None


def _get(path: str, params: dict[str, str], token: str) -> object:
    """One authenticated Vercel API GET; raises ``httpx.HTTPError`` on transport failure or a non-2xx status."""
    response = httpx.get(
        f"{API}{path}",
        params=params,
        headers={"Authorization": f"Bearer {token}", "User-Agent": "shared-utilities-repo-governance"},
        timeout=30,
    )
    _ = response.raise_for_status()
    return load_json(response.content)


def _read_json[T](kind: type[T], path: pathlib.Path) -> T | None:
    """A JSON file decoded as ``kind``, or ``None`` when it is unreadable, malformed or misshapen."""
    try:
        text = path.read_text(encoding="utf-8")
    except (OSError, UnicodeDecodeError):
        return None
    return try_decode_json(kind, text)


@dataclass(frozen=True, slots=True)
class _VercelLink:
    """A project's git link; CLI-deployed projects have none, or one without ``repo``."""

    repo: str | None = None
    org: str | None = None


@dataclass(frozen=True, slots=True)
class _VercelGitOptions:
    create_deployments: Annotated[str | None, Field(alias="createDeployments")] = None


@dataclass(frozen=True, slots=True)
class _VercelProject:
    id: str
    name: str
    link: _VercelLink | None = None
    root_directory: Annotated[str | None, Field(alias="rootDirectory")] = None
    production_branch: Annotated[str | None, Field(alias="productionBranch")] = None
    git_provider_options: Annotated[_VercelGitOptions | None, Field(alias="gitProviderOptions")] = None


@dataclass(frozen=True, slots=True)
class _VercelProjects:
    projects: list[_VercelProject]


@dataclass(frozen=True, slots=True)
class _VercelDeploymentMeta:
    github_commit_ref: Annotated[str | None, Field(alias="githubCommitRef")] = None
    github_commit_sha: Annotated[str | None, Field(alias="githubCommitSha")] = None
    #: What a CLI deploy from a git checkout records: the commit, but not the repo.
    git_commit_sha: Annotated[Lenient[StrictStr], Field(alias="gitCommitSha")] = None


@dataclass(frozen=True, slots=True)
class _VercelDeployment:
    state: str
    created_at: Annotated[int, Field(alias="createdAt")]
    target: str | None = None
    meta: _VercelDeploymentMeta | None = None


@dataclass(frozen=True, slots=True)
class _VercelDeployments:
    deployments: list[_VercelDeployment]


@dataclass(frozen=True, slots=True)
class _CheckoutFacts:
    """What the local checkout says: see :class:`ProjectFacts` for ``allow``/``deny``/``head_sha``."""

    allow: tuple[str, ...] | None = None
    deny: tuple[str, ...] | None = None
    head_sha: str | None = None


@dataclass(frozen=True, slots=True)
class ProjectLink:
    """A Vercel project and the GitHub ``owner/repo`` it deploys from; None for a CLI-deployed one."""

    name: str
    slug: str | None


def list_project_links(team_id: str) -> list[ProjectLink]:
    """Every project in the team, with the repository its git link names."""
    listing = try_decode(_VercelProjects, _get("/v9/projects", {"teamId": team_id, "limit": "100"}, vercel_token()))
    if listing is None:
        raise RuntimeError("Vercel /v9/projects returned an unreadable project list")
    links: list[ProjectLink] = []
    for project in listing.projects:
        link = project.link
        slug = f"{link.org}/{link.repo}" if link and link.org and link.repo else None
        links.append(ProjectLink(project.name, slug))
    return sorted(links, key=lambda link: link.name)


def latest_commit(team_id: str, project: str) -> str | None:
    """The commit the project's newest deployment was built from, from its git or CLI metadata."""
    raw = _get("/v6/deployments", {"teamId": team_id, "app": project, "limit": "1"}, vercel_token())
    deployments = try_decode(_VercelDeployments, raw)
    if deployments is None or not deployments.deployments:
        return None
    meta = deployments.deployments[0].meta
    return (meta.github_commit_sha or meta.git_commit_sha) if meta else None


def audit_projects(
    *,
    team_id: str,
    window_days: int,
    repos_dir: pathlib.Path | None = None,
    only_repo: str | None = None,
) -> list[ProjectAudit]:
    """Fetch facts per project and plan. ``only_repo`` narrows to one repo's projects."""
    token = vercel_token()
    now_ms = int(time.time() * 1000)
    listing = try_decode(_VercelProjects, _get("/v9/projects", {"teamId": team_id, "limit": "100"}, token))
    if listing is None:
        raise RuntimeError("Vercel /v9/projects returned an unreadable project list")
    results: list[ProjectAudit] = []
    for project in listing.projects:
        repo = project.link.repo if project.link else None
        if repo is None:
            continue  # CLI-deployed project: no pushes, no budget burn
        if only_repo and repo != only_repo:
            continue
        raw = _get(
            "/v6/deployments",
            {"teamId": team_id, "projectId": project.id, "limit": "100"},
            token,
        )
        deployments = try_decode(_VercelDeployments, raw)
        recent = tuple(
            CreatedDeployment(
                ref=(d.meta.github_commit_ref if d.meta else None) or "?",
                target=d.target,
                state=d.state,
                created_at_ms=d.created_at,
                commit_sha=d.meta.github_commit_sha if d.meta else None,
            )
            for d in (deployments.deployments if deployments else ())
        )
        checkout = _repo_facts(repo, project.root_directory, repos_dir)
        facts = ProjectFacts(
            name=project.name,
            repo=repo,
            create_deployments=project.git_provider_options.create_deployments
            if project.git_provider_options
            else None,
            production_ref=project.production_branch or "main",
            allow=checkout.allow,
            deny=checkout.deny,
            recent_deployments=recent,
            head_sha=checkout.head_sha,
        )
        findings = plan_project_findings(facts, now_ms=now_ms, window_days=window_days)
        if deployments is None:
            findings.append(
                Finding(
                    project.name,
                    "deployments-unreadable",
                    "warn",
                    "Vercel /v6/deployments returned an unreadable response; deployment rules were not evaluated.",
                )
            )
        results.append(ProjectAudit(facts, findings))
    return results


def _repo_facts(
    repo: str,
    root_directory: str | None,
    repos_dir: pathlib.Path | None,
) -> _CheckoutFacts:
    """Read the local checkout: the vercel.json creation map and the branch tip.

    A repo that is not checked out contributes ``None``s — absence is not a finding,
    the rules degrade to the empirical one.
    """
    allow: tuple[str, ...] | None = None
    deny: tuple[str, ...] | None = None
    if repos_dir is None:
        return _CheckoutFacts()
    checkout = repos_dir / repo
    if not checkout.is_dir():
        return _CheckoutFacts()
    vercel_json = checkout / (root_directory or "") / "vercel.json"
    config = _read_json(_VercelJson, vercel_json)
    git = config.git if config is not None else None
    match git.deployment_enabled if git is not None else None:
        case dict() as entries:
            allow = tuple(k for k, v in entries.items() if v is True)
            deny = tuple(k for k, v in entries.items() if v is False)
        case False:
            allow, deny = (), ("**",)
        case _:
            pass
    head_sha = proc.git_out("rev-parse", "origin/main", cwd=checkout) or None
    return _CheckoutFacts(allow=allow, deny=deny, head_sha=head_sha)
