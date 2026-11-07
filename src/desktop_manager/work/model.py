"""The work-item shapes: the facts gathered from git, GitHub and Claude, and the joined item.

Everything here is plain data. Gathering lives in `git_facts`, `prs` and `sessions`; joining
and the lifecycle rules live in `join`; the JSON schema lives in `serialize`.
"""

from __future__ import annotations

import datetime as dt
from dataclasses import dataclass
from enum import StrEnum
from pathlib import Path


class WorkState(StrEnum):
    """Where an item is in its life. Precedence is documented on `join.derive_state`."""

    ACTIVE = "active"
    MERGED = "merged"
    CLOSED = "closed"
    STALE = "stale"
    TRUNK = "trunk"
    UNATTACHED = "unattached"


class PrStatus(StrEnum):
    OPEN = "open"
    DRAFT = "draft"
    MERGED = "merged"
    CLOSED = "closed"


class PrLookup(StrEnum):
    """Whether the PR field can be trusted to mean "no PR" when it is empty."""

    #: GitHub answered (from the network or the cache): an empty `pr` really means none.
    OK = "ok"
    #: Not asked: a trunk branch, a detached head, the unattached bucket, or `--offline`.
    SKIPPED = "skipped"
    #: Asked and failed (no gh, not authenticated, offline, not a GitHub remote).
    UNAVAILABLE = "unavailable"


class SessionState(StrEnum):
    #: Running now and working.
    LIVE = "live"
    #: Running now and idle at the prompt.
    WAITING_ON_USER = "waiting-on-user"
    #: Not running; the process died while a tool call or a prompt was in flight.
    DIED_MID_TASK = "died-mid-task"
    #: Not running; the process died (window closed, crash) after Claude had replied.
    DIED_IDLE = "died-idle"
    #: Not running; Claude Code exited normally.
    ENDED_CLEAN = "ended-clean"
    #: The user ran `/clear`, which says they were done with it.
    CLEARED = "cleared"


#: Session states that mean a Claude process is attached to the work right now.
RUNNING_STATES = frozenset({SessionState.LIVE, SessionState.WAITING_ON_USER})


# -- facts gathered from git ----------------------------------------------------


@dataclass(frozen=True, slots=True)
class Checkout:
    """One working tree: a repo's main checkout or one of its linked worktrees."""

    path: Path
    #: The branch checked out; empty for a detached HEAD.
    branch: str
    is_main_checkout: bool
    #: Modified, staged or untracked files. None when the status check was skipped or failed.
    dirty: bool | None = None


@dataclass(frozen=True, slots=True)
class BranchRef:
    """One local branch, from a single `git for-each-ref` per repo."""

    name: str
    last_commit: dt.datetime | None = None
    upstream: str = ""
    upstream_gone: bool = False
    ahead: int | None = None
    behind: int | None = None
    #: Commits reachable from the branch that are on no remote-tracking ref (bounded walk).
    unpushed: int = 0


@dataclass(frozen=True, slots=True)
class RepoFacts:
    """Everything git said about one clone."""

    path: Path
    #: Display name: the clone's path relative to the code root, with forward slashes.
    name: str
    default_branch: str
    checkouts: tuple[Checkout, ...]
    branches: tuple[BranchRef, ...] = ()
    #: `owner/name` parsed from the origin URL; empty when it is not a GitHub remote.
    github_slug: str = ""


@dataclass(frozen=True, slots=True)
class BranchKey:
    """A (repo, branch) pair: the identity of a work item."""

    repo_path: Path
    branch: str


# -- facts gathered from GitHub -------------------------------------------------


@dataclass(frozen=True, slots=True)
class PrInfo:
    number: int
    status: PrStatus
    url: str
    updated_at: dt.datetime | None = None


# -- facts gathered from Claude -------------------------------------------------


@dataclass(frozen=True, slots=True)
class SessionFacts:
    """One Claude Code session, classified, before attribution."""

    session_id: str
    title: str
    state: SessionState
    last_activity: dt.datetime | None
    #: Where the session was when it last did anything. Attribution uses this.
    last_cwd: str | None
    #: Where the session started; `claude --resume` must run from here.
    start_cwd: str | None
    prompt_count: int
    pid: int | None = None


@dataclass(frozen=True, slots=True)
class Attribution:
    """Which (repo, branch) a session belongs to; repo None means the unattached bucket."""

    repo_path: Path | None
    branch: str = ""
    #: Why it landed where it did, for the unattached bucket mostly.
    reason: str = ""


# -- the joined item -----------------------------------------------------------


@dataclass(frozen=True, slots=True)
class WorkItem:
    #: Stable identifier: `<repo>:<branch>`, or `unattached`.
    key: str
    repo: str | None
    repo_path: Path | None
    branch: str | None
    ticket: str | None
    is_trunk: bool
    state: WorkState
    #: Human-readable facts that decided `state`, most important first.
    reasons: tuple[str, ...]
    checkouts: tuple[Checkout, ...]
    branch_ref: BranchRef | None
    pr: PrInfo | None
    pr_lookup: PrLookup
    sessions: tuple[SessionFacts, ...]
    last_activity: dt.datetime | None

    @property
    def dirty(self) -> bool:
        return any(c.dirty for c in self.checkouts)

    @property
    def unpushed(self) -> int:
        return self.branch_ref.unpushed if self.branch_ref else 0

    @property
    def running_sessions(self) -> tuple[SessionFacts, ...]:
        return tuple(s for s in self.sessions if s.state in RUNNING_STATES)


@dataclass(frozen=True, slots=True)
class WorkReport:
    generated_at: dt.datetime
    code_root: Path
    items: tuple[WorkItem, ...]
    #: Degradations worth knowing about: a repo gh could not answer for, and so on.
    warnings: tuple[str, ...] = ()
