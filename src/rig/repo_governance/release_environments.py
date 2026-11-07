"""Audit and synchronize GitHub deployment environments across the estate.

The `release` environment is the target release workflows publish against. On
personal repos it is deliberately bare: required reviewers are an org-only
feature, and even if served, GitHub forbids self-approval, so a sole maintainer
cannot gate themselves — the environment buys deployment history and a future
secrets namespace, not an approval pause. Reviewer support exists here for the
day a repo moves to an org with a second human.

The plan step is pure (desired policy in, current state in, actions out) so the
judgement is testable without touching GitHub; only the apply step shells out.
"""

from __future__ import annotations

import json
from dataclasses import asdict, dataclass, field

from rig import proc
from rig.typed_json import load_json, try_decode

from .policy_config import EnvironmentPolicy

__all__ = [
    "Action",
    "CurrentEnvironment",
    "EnvironmentSync",
    "fetch_current_environments",
    "plan_environment_actions",
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
class _GhCall:
    code: int
    body: object
    stderr: str


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


def _gh_api(method: str, endpoint: str, payload: _WaitTimerBody | _ReviewersBody | None = None) -> _GhCall:
    """Run one `gh api` call; ``body`` is the parsed JSON, or ``None`` when there was none."""
    if payload is None:
        res = proc.gh("api", "-X", method, endpoint)
    else:
        res = proc.gh("api", "-X", method, endpoint, "--input", "-", stdin_text=json.dumps(asdict(payload)))
    body: object = None
    if res.out.strip():
        try:
            body = load_json(res.out)
        except json.JSONDecodeError:
            body = None
    return _GhCall(code=res.code, body=body, stderr=res.err.strip())


def _user_id(login: str) -> int | None:
    call = _gh_api("GET", f"users/{login}")
    user = try_decode(_GhUser, call.body) if call.code == 0 else None
    return user.id if user else None


def _deploy_reviewers(repo_name: str, environment: str) -> tuple[str, ...] | None:
    """User reviewer logins, or ``None`` when GitHub does not serve the protection rules for this repo."""
    call = _gh_api("GET", f"repos/{repo_name}/environments/{environment}/deploy_reviewers")
    served = try_decode(_GhDeployReviewers, call.body) if call.code == 0 else None
    if served is None:
        return None
    return tuple(entry.reviewer.login for entry in served.reviewers if entry.reviewer.login)


def fetch_current_environments(repo_name: str) -> dict[str, CurrentEnvironment]:
    """Read every environment on *repo_name*, tolerating unserved protection endpoints."""
    call = _gh_api("GET", f"repos/{repo_name}/environments")
    listed = try_decode(_GhEnvironmentList, call.body) if call.code == 0 else None
    if listed is None:
        return {}
    return {
        env.name: CurrentEnvironment(
            name=env.name, wait_timer=env.wait_timer, reviewers=_deploy_reviewers(repo_name, env.name)
        )
        for env in listed.environments
        if env.name
    }


def sync_repo_environments(
    repo_name: str,
    desired: dict[str, EnvironmentPolicy],
    *,
    dry_run: bool = False,
) -> EnvironmentSync:
    """Apply the plan, returning the actions it decided on and a log line per action."""
    current = fetch_current_environments(repo_name)
    actions = plan_environment_actions(desired, current)
    log: list[str] = []

    for action in actions:
        if not action.mutates:
            log.append(f"{action.kind}: {action.environment} — {action.detail}")
            continue
        if dry_run:
            log.append(f"dry-run {action.kind}: {action.environment} — {action.detail}")
            continue

        policy = desired[action.environment]
        if action.kind == "create":
            call = _gh_api(
                "PUT", f"repos/{repo_name}/environments/{action.environment}", _WaitTimerBody(policy.wait_timer)
            )
            log.append(
                f"created {action.environment}"
                if call.code == 0
                else f"FAILED create {action.environment}: {call.stderr}"
            )

        elif action.kind == "wait-timer":
            call = _gh_api(
                "PUT",
                f"repos/{repo_name}/environments/{action.environment}",
                _WaitTimerBody(policy.wait_timer),
            )
            log.append(
                f"updated wait_timer on {action.environment}"
                if call.code == 0
                else f"FAILED wait_timer {action.environment}: {call.stderr}"
            )

        elif action.kind == "add-reviewers" and policy.reviewers:
            reviewers: list[_ReviewerBody] = []
            for login in policy.reviewers:
                uid = _user_id(login)
                if uid is None:
                    log.append(f"FAILED add-reviewers {action.environment}: cannot resolve user {login}")
                    reviewers = []
                    break
                reviewers.append(_ReviewerBody(id=uid))
            if reviewers:
                call = _gh_api(
                    "PATCH",
                    f"repos/{repo_name}/environments/{action.environment}/deploy_reviewers",
                    _ReviewersBody(reviewers),
                )
                if call.code != 0:
                    log.append(f"FAILED add-reviewers {action.environment}: {call.stderr}")
                else:
                    log.append(f"configured reviewers on {action.environment}")

    return EnvironmentSync(actions=actions, log=log)
