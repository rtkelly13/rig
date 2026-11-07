"""Typed snapshots and the versioned, explainable triage ordering."""

from __future__ import annotations

from dataclasses import dataclass
from datetime import datetime
from typing import Annotated, Literal

from pydantic import Field

from rig.triage.facts import Facts

Kind = Literal["pr", "issue"]
Action = Literal[
    "opened",
    "unread",
    "archived",
    "unarchived",
    "review_requested",
    "reviewed",
    "changes_requested",
    "deferred",
    "resumed",
    "note",
]


@dataclass(frozen=True, slots=True)
class Item:
    repo: str
    number: int
    kind: Kind
    title: str
    state: str
    created: str
    updated: str
    head: str = ""
    priority: int = 4
    readiness: int = 3
    lines: int = 0
    in_scope: bool = True
    body: str | None = None
    facts: Facts | None = None

    @property
    def ref(self) -> str:
        return f"{self.repo}#{self.number}"

    @property
    def url(self) -> str:
        route = "pull" if self.kind == "pr" else "issues"
        return f"https://github.com/{self.repo}/{route}/{self.number}"


@dataclass(frozen=True, slots=True, order=True)
class Rank:
    priority: int
    readiness: int
    lines: int
    created: datetime
    repo: str
    number: int


def rank(item: Item) -> Rank:
    """Priority P0..P3, readiness, lines, creation time, repo, number. No clock dependence."""
    return Rank(item.priority, item.readiness, item.lines, datetime.fromisoformat(item.created), item.repo, item.number)


@dataclass(frozen=True, slots=True)
class Interaction:
    repo: str
    number: Annotated[int, Field(gt=0)]
    action: Action
    head: str
    note: Annotated[str, Field(max_length=10000)] = ""


@dataclass(frozen=True, slots=True)
class Event:
    id: int
    at: str
    action: str
    head: str
    note: str
    actor: str


@dataclass(frozen=True, slots=True)
class Activity:
    key: str
    at: str
    actor: str
    action: str
    head: str
    note: str


@dataclass(frozen=True, slots=True)
class Entry:
    item: Item
    events: tuple[Event, ...]

    @property
    def deferred(self) -> bool:
        decisions = [event.action for event in self.events if event.action in ("deferred", "resumed")]
        return bool(decisions and decisions[-1] == "deferred")

    @property
    def archived(self) -> bool:
        """Archive is local; a new head or human GitHub discussion brings it back."""
        decisions = [event for event in self.events if event.action in ("archived", "unarchived")]
        if not decisions or decisions[-1].action != "archived" or decisions[-1].head != self.item.head:
            return False
        archived_at = datetime.fromisoformat(decisions[-1].at)
        return not any(
            event.action.startswith("github:")
            and "[bot]" not in event.actor
            and datetime.fromisoformat(event.at) > archived_at
            for event in self.events
        )
