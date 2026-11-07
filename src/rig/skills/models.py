"""Domain models for AI agent skill symlink management and auditing."""

from __future__ import annotations

from enum import StrEnum
from pathlib import Path
from typing import ClassVar

from pydantic import BaseModel, ConfigDict, Field


class CliSystem(StrEnum):
    """Supported AI coding agent CLI systems."""

    OPENCODE = "opencode"
    CLAUDE = "claude"
    CODEX = "codex"
    ANTIGRAVITY = "antigravity"
    AGENTS = "agents"


CLI_SYSTEM_DISPLAY_NAMES: dict[CliSystem, str] = {
    CliSystem.OPENCODE: "OpenCode",
    CliSystem.CLAUDE: "Claude Code",
    CliSystem.CODEX: "OpenAI Codex",
    CliSystem.ANTIGRAVITY: "Google Antigravity",
    CliSystem.AGENTS: "Cross-Agent Standard",
}

# The four primary systems explicitly configured by user
DEFAULT_PRIMARY_SYSTEMS: list[CliSystem] = [
    CliSystem.OPENCODE,
    CliSystem.CLAUDE,
    CliSystem.CODEX,
    CliSystem.ANTIGRAVITY,
]


class SymlinkStatus(StrEnum):
    """Validation status for an individual skill symlink in a target directory."""

    VALID = "valid"
    BROKEN = "broken"
    DRIFTED = "drifted"
    COLLISION = "collision"
    MISSING = "missing"


class SkillManifest(BaseModel):
    """Metadata for a discovered skill package."""

    model_config: ClassVar[ConfigDict] = ConfigDict(frozen=True)

    name: str
    description: str
    folder_path: Path
    skill_md_path: Path
    source_name: str = "shared-utilities"


class SystemSkillStatus(BaseModel):
    """Symlink status report for a single CLI system."""

    model_config: ClassVar[ConfigDict] = ConfigDict(arbitrary_types_allowed=True)

    system: CliSystem
    display_name: str
    target_dir: Path
    dir_exists: bool
    total_links: int = 0
    valid_links: list[str] = Field(default_factory=list)
    broken_links: list[str] = Field(default_factory=list)
    drifted_links: list[str] = Field(default_factory=list)
    missing_skills: list[str] = Field(default_factory=list)
    stale_links: list[str] = Field(default_factory=list)
    collisions: list[str] = Field(default_factory=list)

    @property
    def is_healthy(self) -> bool:
        """Returns True if target directory exists, all skills are valid, and no broken/stale links exist."""
        return (
            self.dir_exists
            and len(self.missing_skills) == 0
            and len(self.broken_links) == 0
            and len(self.drifted_links) == 0
            and len(self.stale_links) == 0
            and len(self.collisions) == 0
        )


def _default_discovered_skills() -> list[SkillManifest]:
    return []


def _default_systems() -> dict[CliSystem, SystemSkillStatus]:
    return {}


class SkillsAuditReport(BaseModel):
    """Aggregate audit report across all managed AI agent CLI systems."""

    model_config: ClassVar[ConfigDict] = ConfigDict(arbitrary_types_allowed=True)

    discovered_skills: list[SkillManifest] = Field(default_factory=_default_discovered_skills)
    systems: dict[CliSystem, SystemSkillStatus] = Field(default_factory=_default_systems)
    allowlist_active: bool = False

    @property
    def is_clean(self) -> bool:
        """Returns True if every inspected system is completely healthy and synced."""
        return all(status.is_healthy for status in self.systems.values())
