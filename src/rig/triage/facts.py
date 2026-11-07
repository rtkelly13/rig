"""Backward-compatible GitHub facts; absence means not fetched, never success."""

from __future__ import annotations

from dataclasses import dataclass, field


@dataclass(frozen=True, slots=True)
class Check:
    name: str
    state: str
    url: str


@dataclass(frozen=True, slots=True)
class Related:
    repo: str
    number: int
    title: str
    state: str
    kind: str


@dataclass(frozen=True, slots=True)
class StackMember:
    number: int
    title: str
    state: str
    position: int


@dataclass(frozen=True, slots=True)
class Stack:
    number: int
    size: int
    position: int
    base: str
    members: list[StackMember] = field(default_factory=list[StackMember])


@dataclass(frozen=True, slots=True)
class Facts:
    author: str = ""
    viewer_authored: bool = False
    assignees: list[str] = field(default_factory=list[str])
    labels: list[str] = field(default_factory=list[str])
    milestone: str = ""
    comments: int = 0
    review_decision: str = ""
    review_requested: bool = False
    reviewers: list[str] = field(default_factory=list[str])
    draft: bool = False
    mergeable: str = "UNKNOWN"
    base: str = ""
    head: str = ""
    cross_repository: bool = False
    checks_state: str = "UNKNOWN"
    checks: list[Check] = field(default_factory=list[Check])
    checks_total: int | None = None
    unresolved_threads: int = 0
    threads_total: int = 0
    state_reason: str = ""
    related: list[Related] = field(default_factory=list[Related])
    stack: Stack | None = None
    partial: list[str] = field(default_factory=list[str])
