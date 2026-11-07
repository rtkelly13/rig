"""The one shape every GitHub-side governance feature has: desired → observed → plan → apply.

A :class:`Resource` declares what a repo should look like (from `config/repo_policies.json`), reads
what GitHub holds through the :class:`~rig.proc.GitHubApi` port, diffs the two into typed
:class:`Change` rows with no I/O, and applies those rows through the same port. Applying is
idempotent: a second plan after a successful apply is empty. See docs/target-architecture.md.

`repo-governance plan` (:mod:`.cli_plan`) walks every resource in :data:`RESOURCES` for each target.
"""

from __future__ import annotations

import pathlib
from collections.abc import Callable, Sequence
from dataclasses import dataclass, field
from typing import Literal, Protocol

from pydantic import ValidationError

from rig.proc import ApiRequest, GitHubApi, Ran
from rig.repo_governance.policy_config import PolicyConfig
from rig.typed_json import decode_json

__all__ = [
    "Change",
    "GitHubReadError",
    "Planned",
    "Planner",
    "RepoPlan",
    "Resource",
    "Target",
    "bind",
    "is_not_found",
    "read_json",
    "send",
]

#: ``apply``: a write ``plan --apply`` makes. ``refused``: a safety gate stops the write until a person
#: acts. ``note``: worth knowing, never written and not drift (an environment GitHub created itself).
type ChangeKind = Literal["apply", "refused", "note"]


@dataclass(frozen=True, slots=True)
class Change:
    """One difference between the declared and the observed state of one resource.

    *subject* names the thing inside the resource the change is about (an environment, a label);
    it is empty when the resource is one thing (the ruleset, the repo's settings).
    """

    resource: str
    action: str
    detail: str
    kind: ChangeKind = "apply"
    subject: str = ""

    @property
    def is_drift(self) -> bool:
        return self.kind != "note"


@dataclass(frozen=True, slots=True)
class Target:
    """A repo to plan: its local checkout (workflows are read from it) and its GitHub ``owner/name``."""

    path: pathlib.Path
    slug: str


class GitHubReadError(Exception):
    """GitHub could not be read, so no plan can be trusted for this resource."""


class Resource[D, O](Protocol):
    """One kind of GitHub state. ``desired`` returns None when the repo's policy declares nothing."""

    @property
    def name(self) -> str: ...

    def desired(self, cfg: PolicyConfig, target: Target) -> D | None: ...

    def read(self, api: GitHubApi, target: Target) -> O: ...

    def diff(self, desired: D, observed: O) -> list[Change]: ...

    def apply(self, api: GitHubApi, target: Target, desired: D, changes: Sequence[Change]) -> list[str]:
        """Make the ``apply`` changes; returns one line per write that failed."""
        ...


@dataclass(frozen=True, slots=True)
class Planned:
    """One resource's plan for one repo, with the apply step bound to what was planned."""

    resource: str
    changes: list[Change]
    apply: Callable[[], list[str]]
    error: str | None = None


class Planner(Protocol):
    @property
    def name(self) -> str: ...

    def plan(self, cfg: PolicyConfig, api: GitHubApi, target: Target) -> Planned: ...


def _nothing() -> list[str]:
    return []


@dataclass(frozen=True, slots=True)
class _Bound[D, O]:
    resource: Resource[D, O]

    @property
    def name(self) -> str:
        return self.resource.name

    def plan(self, cfg: PolicyConfig, api: GitHubApi, target: Target) -> Planned:
        desired = self.resource.desired(cfg, target)
        if desired is None:
            return Planned(self.name, [], _nothing)
        try:
            observed = self.resource.read(api, target)
        except GitHubReadError as exc:
            return Planned(self.name, [], _nothing, error=str(exc))
        changes = self.resource.diff(desired, observed)
        resource = self.resource

        def apply() -> list[str]:
            return resource.apply(api, target, desired, changes) if any(c.kind == "apply" for c in changes) else []

        return Planned(self.name, changes, apply)


def bind[D, O](resource: Resource[D, O]) -> Planner:
    """Erase a resource's state types so resources of different kinds share one list."""
    return _Bound(resource)


@dataclass(frozen=True, slots=True)
class RepoPlan:
    """Everything the plan found for one repo. This, not the rendered table, is what callers branch on."""

    repo: str
    changes: list[Change] = field(default_factory=list[Change])
    errors: list[str] = field(default_factory=list[str])

    @property
    def drifted(self) -> bool:
        return any(change.is_drift for change in self.changes)


def read_json[T](api: GitHubApi, kind: type[T], route: str) -> T:
    """GET *route* and decode it as *kind*; raises :class:`GitHubReadError` when either step fails."""
    ran = api(ApiRequest("GET", route))
    if not ran.ok:
        raise GitHubReadError(f"GET {route}: {ran.err.strip() or f'exit {ran.code}'}")
    try:
        return decode_json(kind, ran.out)
    except ValidationError as exc:
        raise GitHubReadError(f"GET {route}: unreadable response ({exc.error_count()} error(s))") from exc


def send(api: GitHubApi, method: str, route: str, body: str | None = None) -> Ran:
    """One write through the port."""
    return api(ApiRequest(method, route, body))


def is_not_found(ran: Ran) -> bool:
    """Whether a failed call was GitHub's 404, which for some routes means "not configured"."""
    return "HTTP 404" in ran.err or "Not Found" in ran.err
