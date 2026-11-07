"""Structured package relationships and their evidence; runtime state stays outside the vault."""

from __future__ import annotations

from dataclasses import dataclass, field


@dataclass(frozen=True, slots=True)
class Publication:
    registry: str
    package: str
    repo: str
    evidence: str


@dataclass(frozen=True, slots=True)
class Consumption:
    registry: str
    package: str
    repo: str
    requirement: str
    scope: str
    evidence: str


@dataclass(frozen=True, slots=True)
class Dependency:
    source: str
    target: str
    registry: str
    package: str
    requirement: str
    scope: str
    evidence: str


@dataclass(frozen=True, slots=True)
class Project:
    id: str
    name: str
    description: str
    domain: str
    stack: str
    enabled: bool
    scanned: bool
    manifests: int
    packages: list[str] = field(default_factory=list[str])
    warnings: list[str] = field(default_factory=list[str])


@dataclass(frozen=True, slots=True)
class Component:
    id: str
    members: list[str]
    cyclic: bool


@dataclass(frozen=True, slots=True)
class ComponentEdge:
    source: str
    target: str


@dataclass(frozen=True, slots=True)
class Graph:
    projects: list[Project]
    dependencies: list[Dependency]
    components: list[Component]
    edges: list[ComponentEdge]
    scanned_at: str
    warnings: list[str]


@dataclass(frozen=True, slots=True)
class Dag:
    components: list[Component]
    edges: list[ComponentEdge]
