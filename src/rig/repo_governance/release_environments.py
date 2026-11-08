"""Audit and synchronize GitHub deployment environments across the estate.

The `release` environment is the target release workflows publish against. On
personal repos it is deliberately bare: required reviewers are an org-only
feature, and even if served, GitHub forbids self-approval, so a sole maintainer
cannot gate themselves — the environment buys deployment history and a future
secrets namespace, not an approval pause. Reviewer support exists here for the
day a repo moves to an org with a second human.

The plan step is pure (desired policy in, current state in, actions out) so the
judgement is testable without touching GitHub; reads and writes go through the GitHubApi port.
"""

from __future__ import annotations

from collections.abc import Sequence
from dataclasses import dataclass, field

from rig.proc import GitHubApi, gh_api
from rig.repo_governance.policy_config import EnvironmentPolicy, PolicyConfig
from rig.repo_governance.resource import Change, GitHubReadError, Target, read_json, send
from rig.typed_json import encode_json

__all__ = [
    "Action",
    "CurrentEnvironment",
    "EnvironmentSync",
    "EnvironmentsResource",
    "fetch_current_environments",
    "plan_environment_actions",
    "read_environments",
    "sync_repo_environments",
]


@dataclass(frozen=True)
class CurrentEnvironment:
    """What GitHub reports for one environment today.

    ``reviewers`` is ``None`` when the protection-rule endpoints are not
    served for this repo (a plan entitlement, not a config choice) — "cannot
    tell" must not collapse into "empty".
    """

    name: str
    wait_timer: int = 0
    reviewers: tuple[str, ...] | None = None


@dataclass(frozen=True)
class Action:
    """One mutation (or one report-only finding) the plan decided on."""

    kind: str  # create | wait-timer | add-reviewers | reviewers-unavailable | undeclared
    environment: str
    detail: str = ""

    @property
    def mutates(self) -> bool:
        return self.kind in {"create", "wait-timer", "add-reviewers"}


def plan_environment_actions(
    desired: dict[str, EnvironmentPolicy],
    current: dict[str, CurrentEnvironment],
) -> list[Action]:
    """Decide what to do. Pure — every fact is supplied by the caller."""
    actions: list[Action] = []

    for name in sorted(desired):
        policy = desired[name]
        env = current.get(name)

        if env is None:
            actions.append(Action("create", name, f"wait_timer={policy.wait_timer}"))
            continue

        if policy.wait_timer and env.wait_timer != policy.wait_timer:
            actions.append(Action("wait-timer", name, f"{env.wait_timer} -> {policy.wait_timer}"))

        if policy.reviewers:
            if env.reviewers is None:
                actions.append(
                    Action("reviewers-unavailable", name, "GitHub does not serve protection rules for this repo's plan")
                )
            else:
                missing = sorted(set(policy.reviewers) - set(env.reviewers))
                if missing:
                    actions.append(Action("add-reviewers", name, ", ".join(missing)))

    for name in sorted(set(current) - set(desired)):
        actions.append(Action("undeclared", name, "exists on GitHub but no profile declares it"))

    return actions


@dataclass(frozen=True, slots=True)
class EnvironmentSync:
    """What :func:`sync_repo_environments` decided and the log of what it did about it."""

    actions: list[Action]
    log: list[str] = field(default_factory=list[str])


@dataclass(frozen=True, slots=True)
class _GhUser:
    id: int


@dataclass(frozen=True, slots=True)
class _GhEnvironment:
    name: str
    wait_timer: int = 0


@dataclass(frozen=True, slots=True)
class _GhEnvironmentList:
    """The list endpoint wraps the array: ``{"total_count": n, "environments": [...]}``."""

    environments: list[_GhEnvironment]


@dataclass(frozen=True, slots=True)
class _GhReviewerAccount:
    """A user reviewer carries ``login``; a team reviewer does not and is not counted."""

    login: str | None = None


@dataclass(frozen=True, slots=True)
class _GhReviewerEntry:
    reviewer: _GhReviewerAccount


@dataclass(frozen=True, slots=True)
class _GhDeployReviewers:
    reviewers: list[_GhReviewerEntry]


@dataclass(frozen=True, slots=True)
class _WaitTimerBody:
    wait_timer: int


@dataclass(frozen=True, slots=True)
class _ReviewerBody:
    id: int
    reviewer_type: str = "User"


@dataclass(frozen=True, slots=True)
class _ReviewersBody:
    reviewers: list[_ReviewerBody]


def _user_id(api: GitHubApi, login: str) -> int | None:
    try:
        return read_json(api, _GhUser, f"users/{login}").id
    except GitHubReadError:
        return None


def _deploy_reviewers(api: GitHubApi, repo_name: str, environment: str) -> tuple[str, ...] | None:
    """User reviewer logins, or ``None`` when GitHub does not serve the protection rules for this repo."""
    try:
        served = read_json(api, _GhDeployReviewers, f"repos/{repo_name}/environments/{environment}/deploy_reviewers")
    except GitHubReadError:
        return None
    return tuple(entry.reviewer.login for entry in served.reviewers if entry.reviewer.login)


def read_environments(api: GitHubApi, repo_name: str) -> dict[str, CurrentEnvironment]:
    """Read every environment on *repo_name*, tolerating unserved protection endpoints.

    Raises :class:`GitHubReadError` when the list itself cannot be read.
    """
    listed = read_json(api, _GhEnvironmentList, f"repos/{repo_name}/environments")
    return {
        env.name: CurrentEnvironment(
            name=env.name, wait_timer=env.wait_timer, reviewers=_deploy_reviewers(api, repo_name, env.name)
        )
        for env in listed.environments
        if env.name
    }


def fetch_current_environments(repo_name: str, api: GitHubApi = gh_api) -> dict[str, CurrentEnvironment]:
    """:func:`read_environments`, reading an unreadable list as no environments."""
    try:
        return read_environments(api, repo_name)
    except GitHubReadError:
        return {}


def _apply_action(api: GitHubApi, repo_name: str, policy: EnvironmentPolicy, action: Action) -> str:
    """Make one mutating action; returns its log line, starting ``FAILED`` when it failed."""
    route = f"repos/{repo_name}/environments/{action.environment}"
    if action.kind in {"create", "wait-timer"}:
        ran = send(api, "PUT", route, encode_json(_WaitTimerBody, _WaitTimerBody(policy.wait_timer)))
        verb = "created" if action.kind == "create" else "updated wait_timer on"
        return f"{verb} {action.environment}" if ran.ok else f"FAILED {action.kind} {action.environment}: {ran.err}"
    reviewers: list[_ReviewerBody] = []
    for login in policy.reviewers:
        uid = _user_id(api, login)
        if uid is None:
            return f"FAILED add-reviewers {action.environment}: cannot resolve user {login}"
        reviewers.append(_ReviewerBody(id=uid))
    ran = send(api, "PATCH", f"{route}/deploy_reviewers", encode_json(_ReviewersBody, _ReviewersBody(reviewers)))
    return (
        f"configured reviewers on {action.environment}"
        if ran.ok
        else f"FAILED add-reviewers {action.environment}: {ran.err}"
    )


def sync_repo_environments(
    repo_name: str,
    desired: dict[str, EnvironmentPolicy],
    *,
    dry_run: bool = False,
    api: GitHubApi = gh_api,
) -> EnvironmentSync:
    """Apply the plan, returning the actions it decided on and a log line per action."""
    actions = plan_environment_actions(desired, fetch_current_environments(repo_name, api))
    log: list[str] = []
    for action in actions:
        if not action.mutates:
            log.append(f"{action.kind}: {action.environment} — {action.detail}")
        elif dry_run:
            log.append(f"dry-run {action.kind}: {action.environment} — {action.detail}")
        else:
            log.append(_apply_action(api, repo_name, desired[action.environment], action))
    return EnvironmentSync(actions=actions, log=log)


@dataclass(frozen=True, slots=True)
class EnvironmentsResource:
    """Deployment environments a profile declares (`environments` in config/repo_policies.json)."""

    name: str = "environments"

    def desired(self, cfg: PolicyConfig, target: Target) -> dict[str, EnvironmentPolicy] | None:
        return cfg.environments(target.slug) or None

    def read(self, api: GitHubApi, target: Target) -> dict[str, CurrentEnvironment]:
        return read_environments(api, target.slug)

    def diff(self, desired: dict[str, EnvironmentPolicy], observed: dict[str, CurrentEnvironment]) -> list[Change]:
        return [
            Change(self.name, a.kind, a.detail, kind="apply" if a.mutates else "note", subject=a.environment)
            for a in plan_environment_actions(desired, observed)
        ]

    def apply(
        self, api: GitHubApi, target: Target, desired: dict[str, EnvironmentPolicy], changes: Sequence[Change]
    ) -> list[str]:
        lines = (
            _apply_action(api, target.slug, desired[c.subject], Action(c.action, c.subject, c.detail))
            for c in changes
            if c.kind == "apply"
        )
        return [line for line in lines if line.startswith("FAILED")]
