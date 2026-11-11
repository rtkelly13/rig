"""Data models for personal virtual monorepo estate management."""

from __future__ import annotations

from dataclasses import dataclass


@dataclass(frozen=True)
class ManagedRepoSpec:
    """Declared specification for a managed estate repository."""

    name: str
    repo: str
    description: str = ""
    domain: str = ""
    stack: str = ""
    branch: str | None = None
    enabled: bool = True
    orca_group: str | None = None
