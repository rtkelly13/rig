"""Condense strongly connected repositories so every original dependency remains explainable."""

from __future__ import annotations

import hashlib

from rig.connectivity.models import Component, ComponentEdge, Dag, Dependency, Project


def condense(projects: list[Project], dependencies: list[Dependency]) -> Dag:
    """Cycles become explicit cluster nodes; the projection is a deterministic DAG."""
    adjacency = {project.id: set[str]() for project in projects}
    for edge in dependencies:
        if edge.source in adjacency and edge.target in adjacency:
            adjacency[edge.source].add(edge.target)
    indices: dict[str, int] = {}
    low: dict[str, int] = {}
    active: set[str] = set()
    pending: list[str] = []
    components: list[Component] = []

    def visit(node: str) -> None:
        indices[node] = len(indices)
        low[node] = indices[node]
        pending.append(node)
        active.add(node)
        for target in sorted(adjacency[node]):
            if target not in indices:
                visit(target)
                low[node] = min(low[node], low[target])
            elif target in active:
                low[node] = min(low[node], indices[target])
        if low[node] != indices[node]:
            return
        members: list[str] = []
        while pending:
            member = pending.pop()
            active.remove(member)
            members.append(member)
            if member == node:
                break
        members.sort()
        cyclic = len(members) > 1 or node in adjacency[node]
        key = "cycle:" + hashlib.sha256("|".join(members).encode()).hexdigest()[:12] if cyclic else node
        components.append(Component(key, members, cyclic))

    for node in sorted(adjacency):
        if node not in indices:
            visit(node)
    components.sort(key=lambda component: component.id)
    owners = {member: component.id for component in components for member in component.members}
    unique = {
        (owners[edge.source], owners[edge.target])
        for edge in dependencies
        if owners[edge.source] != owners[edge.target]
    }
    return Dag(components, [ComponentEdge(source, target) for source, target in sorted(unique)])
