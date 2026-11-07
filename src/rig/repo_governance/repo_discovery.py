from __future__ import annotations

import pathlib
from dataclasses import dataclass
from typing import Annotated, ClassVar, Literal

from pydantic import Field

from rig import proc, roots, scopes
from rig.gh_origin import origin_args
from rig.repo_governance.branch_cleanup import repo_identity
from rig.repo_governance.policy_config import (
    ManageDecision,
    PolicyConfig,
    load_policy_config,
    should_manage_repo,
)


def personal_repos_dir() -> pathlib.Path:
    """Return the directory holding the personal repos (see `rig.roots`)."""
    return roots.personal_dir()


def estate_clones() -> list[pathlib.Path]:
    """Git clones in the personal repos dir and the org folders beside it."""
    clones: list[pathlib.Path] = []
    for parent in scopes.repo_parent_dirs(personal_repos_dir()):
        if parent.is_dir():
            clones.extend(child for child in sorted(parent.iterdir()) if child.is_dir() and (child / ".git").exists())
    return clones


def get_gh_repo_name(repo_path: pathlib.Path) -> str | None:
    """Retrieves full owner/repo string (e.g. rtkelly13/design-system) via gh CLI."""
    res = proc.gh(
        "repo", "view", *origin_args(repo_path), "--json", "nameWithOwner", "-q", ".nameWithOwner", cwd=repo_path
    )
    return res.out.strip() if res.ok else None


@dataclass(frozen=True, slots=True)
class RepoFound:
    """A local checkout linked to a GitHub repo that exists."""

    status: str
    repo_name: str
    message: str
    exists: ClassVar[Literal[True]] = True
    is_aligned: ClassVar[bool] = True


@dataclass(frozen=True, slots=True)
class RepoNotFound:
    """A local checkout that is missing, unlinked, or whose GitHub repo is gone."""

    status: str
    repo_name: str | None
    message: str
    exists: ClassVar[Literal[False]] = False
    is_aligned: ClassVar[bool] = False


type RepoExistence = RepoFound | RepoNotFound


@dataclass(frozen=True, slots=True)
class OwnerInspection:
    """Whether a repo's GitHub owner is the one its policy expects."""

    status: str
    actual_owner: str | None
    expected_owner: str | None
    allowed_owners: tuple[str, ...]
    is_aligned: bool
    message: str
    inferred_profile: str | None = None
    """The profile the owner maps to (``owner_profiles``) when no profile names the repo yet."""


def inspect_repo_exists(repo_path: pathlib.Path) -> RepoExistence:
    """Inspects if target directory exists locally and links to an existing GitHub remote."""
    if not repo_path.exists() or not (repo_path / ".git").exists():
        return RepoNotFound(
            status="MISSING_LOCAL",
            repo_name=None,
            message="Local directory or .git folder missing",
        )

    repo_name = get_gh_repo_name(repo_path)
    if not repo_name:
        return RepoNotFound(
            status="NOT_LINKED_TO_GITHUB",
            repo_name=None,
            message="Not linked to a remote GitHub repository",
        )

    res = proc.gh("repo", "view", repo_name, "--json", "nameWithOwner", "-q", ".nameWithOwner")
    if not res.ok:
        return RepoNotFound(
            status="NOT_FOUND_ON_GITHUB",
            repo_name=repo_name,
            message=f"GitHub repository {repo_name} does not exist or is inaccessible: {res.err.strip()}",
        )
    return RepoFound(
        status="EXISTS",
        repo_name=res.out.strip(),
        message="Repository exists on GitHub",
    )


def inspect_repo_owner(
    repo_path: pathlib.Path | None = None,
    repo_name: str | None = None,
    config: PolicyConfig | None = None,
) -> OwnerInspection:
    """Inspects whether the repository owner/organization conforms to governance policies."""
    cfg = config or load_policy_config()

    full_name = repo_name
    if not full_name and repo_path:
        full_name = get_gh_repo_name(repo_path)

    if not full_name:
        return OwnerInspection(
            status="UNKNOWN_OWNER",
            actual_owner=None,
            expected_owner=None,
            allowed_owners=(),
            is_aligned=False,
            message="Could not determine repository owner",
        )

    parts = full_name.split("/", 1)
    if len(parts) == 2:
        actual_owner, bare_name = parts
    else:
        actual_owner = None
        bare_name = full_name

    if not actual_owner:
        return OwnerInspection(
            status="UNKNOWN_OWNER",
            actual_owner=None,
            expected_owner=None,
            allowed_owners=(),
            is_aligned=False,
            message=f"Could not determine owner from repository slug '{full_name}'",
        )

    # The GitHub slug is the policy key (see `policy_key`): its owner lets an unregistered repo
    # take its owner's profile, and its name is the remote's, not a renamed folder's.
    lookup_key = f"{actual_owner}/{bare_name}"
    expected = cfg.expected_owner(lookup_key)
    allowed = cfg.allowed_owners(lookup_key)
    inferred = cfg.owner_profile_for(lookup_key)

    actual_lower = actual_owner.lower()
    expected_lower = expected.lower() if expected else None
    allowed_lower = {o.lower() for o in allowed}

    if expected_lower and actual_lower != expected_lower:
        return OwnerInspection(
            status="WRONG_OWNER",
            actual_owner=actual_owner,
            expected_owner=expected,
            allowed_owners=allowed,
            is_aligned=False,
            message=f"Expected owner '{expected}', but repo is owned by '{actual_owner}'",
            inferred_profile=inferred,
        )

    if allowed_lower and actual_lower not in allowed_lower:
        return OwnerInspection(
            status="UNAUTHORIZED_OWNER",
            actual_owner=actual_owner,
            expected_owner=expected,
            allowed_owners=allowed,
            is_aligned=False,
            message=f"Owner '{actual_owner}' is not in allowed owners ({', '.join(allowed)})",
            inferred_profile=inferred,
        )

    message = f"Owner '{actual_owner}' is authorized"
    if inferred is not None:
        message = f"Owner '{actual_owner}' is authorized; not adopted: add it to profile {inferred}"
    return OwnerInspection(
        status="ALIGNED",
        actual_owner=actual_owner,
        expected_owner=expected,
        allowed_owners=allowed,
        is_aligned=True,
        message=message,
        inferred_profile=inferred,
    )


def is_managed_repo(repo_path: pathlib.Path) -> ManageDecision:
    """Decide whether *repo_path* is in scope, and why not when it is not.

    Governance should not touch code you merely cloned. A checkout of someone
    else's repo is indistinguishable from your own on disk, so the decision
    comes from GitHub: forks and repos you cannot push to are out of scope
    unless a profile names them explicitly.
    """
    identity = repo_identity(repo_path)
    if not identity.resolved:
        return ManageDecision(managed=False, reason="could not resolve origin")

    cfg = load_policy_config()
    decision = should_manage_repo(
        rules=cfg.repo_selection(repo_path.name),
        registered=cfg.is_registered(repo_path.name),
        is_fork=identity.is_fork,
        permission=identity.permission,
    )
    return decision


@dataclass(slots=True, frozen=True)
class _GhRepoName:
    name_with_owner: Annotated[str, Field(alias="nameWithOwner")]


def discover_org_repos(org: str) -> list[str]:
    """Discovers repositories for a GitHub organization via gh CLI with local estate fallback."""
    cfg = load_policy_config()
    repos: list[str] = []
    listed = proc.gh_json(
        list[_GhRepoName], "repo", "list", org, "--no-archived", "--limit", "100", "--json", "nameWithOwner"
    )
    repos.extend(row.name_with_owner for row in listed or [])

    if not repos:
        # Fallback to policy config registered repos for this org
        cfg_repos = cfg.repos_for_org(org)
        for r in cfg_repos:
            repos.append(f"{org}/{r}" if "/" not in r else r)

        # Also inspect local estate
        for child in estate_clones():
            expected = cfg.expected_owner(child.name)
            if (expected and expected.lower() == org.lower()) or cfg.is_repo_in_org(child.name, org):
                gh_name = get_gh_repo_name(child) or f"{org}/{child.name}"
                if gh_name not in repos:
                    repos.append(gh_name)

    return repos


def resolve_target_directories(
    path: str | None,
    all_repos: bool,
    *,
    filter_unmanaged: bool = True,
    org: str | None = None,
) -> list[pathlib.Path]:
    """Resolves local repository target paths.

    An ``--all`` sweep is filtered to managed repos (see :func:`is_managed_repo`)
    and reports what it skipped. If *org* is specified, repositories are further
    filtered to those belonging to the given GitHub organization.
    """
    targets: list[pathlib.Path] = []
    cfg = load_policy_config()

    if all_repos or (org and not path):
        for child in estate_clones():
            if org:
                gh_name = get_gh_repo_name(child)
                expected = cfg.expected_owner(child.name)
                in_org = (
                    (gh_name and gh_name.lower().startswith(f"{org.lower()}/"))
                    or (expected and expected.lower() == org.lower())
                    or cfg.is_repo_in_org(child.name, org)
                )
                if not in_org:
                    continue

            if filter_unmanaged and not is_managed_repo(child).managed:
                continue
            targets.append(child)
        return targets

    if path:
        target = pathlib.Path(path).resolve()
        if not target.exists():
            return []
        return [target]

    cwd = pathlib.Path.cwd()
    if org:
        gh_name = get_gh_repo_name(cwd)
        expected = cfg.expected_owner(cwd.name)
        in_org = (
            (gh_name and gh_name.lower().startswith(f"{org.lower()}/"))
            or (expected and expected.lower() == org.lower())
            or cfg.is_repo_in_org(cwd.name, org)
        )
        if not in_org:
            return []

    return [cwd]
