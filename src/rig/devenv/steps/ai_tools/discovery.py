"""Discover installed AI coding agents on the machine."""

from __future__ import annotations

import shutil
from dataclasses import dataclass


@dataclass
class AgentInfo:
    id: str
    display_name: str
    command: str
    is_installed: bool


KNOWN_AGENTS = [
    ("antigravity", "Google Antigravity (agy)", "agy"),
    ("claude", "Claude Code", "claude"),
    ("codex", "OpenAI Codex", "codex"),
    ("opencode", "OpenCode", "opencode"),
    ("github-copilot", "GitHub Copilot", "copilot"),
    ("cursor", "Cursor", "cursor"),
    ("aider", "Aider", "aider"),
    ("gemini", "Gemini CLI", "gemini"),
    ("windsurf", "Windsurf", "windsurf"),
]


def detect_installed_agents() -> list[AgentInfo]:
    """Check which AI coding agents are present on system PATH."""
    results: list[AgentInfo] = []
    for agent_id, name, cmd in KNOWN_AGENTS:
        installed = shutil.which(cmd) is not None
        results.append(AgentInfo(id=agent_id, display_name=name, command=cmd, is_installed=installed))
    return results
