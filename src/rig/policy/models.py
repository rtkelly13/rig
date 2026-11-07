"""Models for the estate command policy."""

from __future__ import annotations

from dataclasses import dataclass, field
from enum import StrEnum
from pathlib import Path


def _empty_strings() -> list[str]:
    return []


def _empty_rules() -> list[CommandRule]:
    return []


class CommandRisk(StrEnum):
    """Operational risk tier for a command invocation."""

    SAFE = "safe"
    LOCAL = "local"
    EXTERNAL = "external"
    DENIED = "denied"


class ProjectionStatus(StrEnum):
    """Outcome of projecting policy into one agent's native configuration."""

    APPLIED = "applied"
    UNCHANGED = "unchanged"
    DRY_RUN = "dry-run"
    UNSUPPORTED = "unsupported"
    FAILED = "failed"


@dataclass(frozen=True)
class CommandRule:
    """A glob pattern and its policy tier."""

    pattern: str
    risk: CommandRisk
    rationale: str = ""
    exclude_systems: tuple[str, ...] = ()
    """Agent surfaces this rule is not projected into, e.g. ``("claude",)``."""


@dataclass(frozen=True)
class ProjectCommand:
    """A command discovered from a project's checked-in manifest."""

    project: str
    manifest: Path
    name: str
    invocation: str
    risk: CommandRisk
    source: str


@dataclass
class Policy:
    """Loaded estate-wide policy configuration."""

    skills_allowlist: list[str] = field(default_factory=_empty_strings)
    command_rules: list[CommandRule] = field(default_factory=_empty_rules)


@dataclass(frozen=True)
class ProjectionResult:
    """Result of one native permission projection."""

    system: str
    path: Path | None
    status: ProjectionStatus
    detail: str
