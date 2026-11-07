"""Which repository each Vercel project belongs to, checked against what Vercel says.

Managed projects declare their Vercel project names under ``vercel`` in
``config/managed-projects.json`` (``rig.workspace.vercel_names``). This audit
establishes each Vercel project's repository from Vercel itself and reports
where the declarations disagree. It reports; it never edits the config (ADR 0020).

A project's repository comes from its git link. A project deployed from the CLI
has no link, but its deployments record the commit they were built from, and the
managed repository that has that commit on GitHub is its source. A project with
neither rests on its declaration alone, which the audit says.
"""

from __future__ import annotations

import subprocess
import sys
from collections.abc import Callable, Mapping, Sequence
from dataclasses import dataclass
from typing import Literal

from rig.workspace.vercel_names import VercelClaim

Severity = Literal["error", "warn", "ok"]


@dataclass(frozen=True, slots=True)
class Ownership:
    """What Vercel says about one project: where it deploys from, and how that was found."""

    name: str
    #: ``owner/repo`` from the project's git link, or from its newest deployment's commit; None when neither.
    source: str | None
    via: Literal["link", "commit", "none"]


@dataclass(frozen=True, slots=True)
class OwnershipFinding:
    vercel_project: str
    severity: Severity
    rule: str
    detail: str
    #: The managed project the Vercel project belongs to, when that is known.
    managed: str | None = None


def plan_findings(
    owned: Sequence[Ownership], claims: Sequence[VercelClaim], repos: Mapping[str, str]
) -> list[OwnershipFinding]:
    """Compare Vercel's facts with the declarations. Pure.

    ``repos`` maps each managed project's name to its GitHub ``owner/repo``."""
    by_slug = {slug.lower(): name for name, slug in repos.items()}
    by_repo_name = {slug.rsplit("/", 1)[-1].lower(): name for name, slug in repos.items()}
    declared = {vercel: claim for claim in claims for vercel in claim.names}
    findings: list[OwnershipFinding] = []
    for project in owned:
        claim = declared.get(project.name)
        owner: str | None = None
        if project.source is not None:
            owner = by_slug.get(project.source.lower())
            if owner is None:
                owner = by_repo_name.get(project.source.rsplit("/", 1)[-1].lower())
                if owner is not None:
                    findings.append(
                        OwnershipFinding(
                            project.name,
                            "warn",
                            "stale-link",
                            f"Vercel names {project.source}, which is now {repos[owner]}: relink the project",
                            owner,
                        )
                    )
        findings.append(_verdict(project, claim, owner))
    present = {project.name for project in owned}
    findings.extend(
        OwnershipFinding(name, "error", "missing", "declared, but there is no such Vercel project", claim.project)
        for name, claim in sorted(declared.items())
        if name not in present
    )
    return findings


def _verdict(project: Ownership, claim: VercelClaim | None, owner: str | None) -> OwnershipFinding:
    via = {"link": "its git link", "commit": "its deployed commit", "none": "nothing"}[project.via]
    if claim is None:
        if owner is not None:
            return OwnershipFinding(
                project.name, "error", "undeclared", f"deploys from {owner} ({via}): declare it under {owner}", owner
            )
        source = f" ({project.source}, not managed)" if project.source else ""
        return OwnershipFinding(project.name, "error", "undeclared", f"owner unknown{source}: declare it")
    if owner is None:
        if project.source is None:
            return OwnershipFinding(
                project.name, "warn", "unverified", "no git link or commit: rests on the declaration", claim.project
            )
        return OwnershipFinding(
            project.name, "error", "mismatch", f"declared under {claim.project}, deploys from {project.source}"
        )
    if owner != claim.project:
        return OwnershipFinding(
            project.name, "error", "mismatch", f"declared under {claim.project}, deploys from {owner} ({via})", owner
        )
    return OwnershipFinding(project.name, "ok", "ok", f"{owner}, confirmed by {via}", owner)


def has_commit(slug: str, sha: str) -> bool:
    """Whether GitHub's ``owner/repo`` has this commit."""
    flags = subprocess.CREATE_NO_WINDOW if sys.platform == "win32" else 0
    result = subprocess.run(
        ["gh", "api", f"repos/{slug}/commits/{sha}", "--jq", ".sha"],
        capture_output=True,
        text=True,
        check=False,
        creationflags=flags,
    )
    return result.returncode == 0 and result.stdout.strip() == sha


def commit_source(
    sha: str, repos: Mapping[str, str], first: str | None, contains: Callable[[str, str], bool] = has_commit
) -> str | None:
    """The managed ``owner/repo`` holding ``sha``, trying the declared project's repo first."""
    order = sorted(repos.values(), key=lambda slug: slug != (repos.get(first) if first else None))
    return next((slug for slug in order if contains(slug, sha)), None)


def gather(team_id: str, claims: Sequence[VercelClaim], repos: Mapping[str, str]) -> list[Ownership]:
    """Every Vercel project in the team, with the repository Vercel says it deploys from."""
    from rig.repo_governance.vercel_gating import latest_commit, list_project_links

    declared = {vercel: claim.project for claim in claims for vercel in claim.names}
    owned: list[Ownership] = []
    for link in list_project_links(team_id):
        if link.slug is not None:
            owned.append(Ownership(link.name, link.slug, "link"))
            continue
        sha = latest_commit(team_id, link.name)
        source = commit_source(sha, repos, declared.get(link.name)) if sha else None
        owned.append(Ownership(link.name, source, "commit" if source else "none"))
    return owned
