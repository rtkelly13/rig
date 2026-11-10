"""Orca's agent permission modes (Settings → Agents), as policy and as checks.

Since Orca 1.4.255 an agent's launch command is its permission mode's flags plus its custom
arguments. `settings.update` rejects `agentPermissionOverrides`, but it takes a whole launch line in
`agentDefaultArgs` and splits it again: the permission flags become the agent's mode and the rest
its custom arguments. So sync sends each agent's composed line, and the doctor checks both halves.
See `docs/orca.md` (Agent permissions).
"""

from __future__ import annotations

from dataclasses import dataclass, field
from pathlib import Path
from typing import Literal

from rig.estate.orca_runtime import ORCA_UNAVAILABLE_EXIT, OrcaRuntime, OrcaUnavailableError
from rig.workspace.orca_rpc import orca_runtime_metadata_path

__all__ = [
    "ORCA_UNAVAILABLE_EXIT",
    "AgentCheck",
    "AgentPermissionsPolicy",
    "Permission",
    "custom_args_checks",
    "flags",
    "configurable",
    "launch_checks",
    "launch_lines",
    "live_launch_args",
    "permission_checks",
    "set_live_launch_args",
]

type Permission = Literal["own", "ask", "accept-edits", "auto", "full-access"]

_LABEL: dict[Permission, str] = {
    "own": "Own",
    "ask": "Ask",
    "accept-edits": "Accept edits",
    "auto": "Auto",
    "full-access": "Full access",
}

_CLAUDE_FAMILY = ("claude", "claude-agent-teams")

#: The flags each mode adds, from Orca 1.4.255's `ARGS_BY_CHOICE`, `AUTO_TUI_AGENT_ARGS` and
#: `YOLO_TUI_AGENT_ARGS` (src/shared/agent-launch/permissions/). Only agents rig has a policy for are
#: listed; a mode an agent is missing from is not checked at launch. ``own`` adds nothing for any agent.
_FLAGS: dict[Permission, dict[str, str]] = {
    "ask": {
        **dict.fromkeys(_CLAUDE_FAMILY, "--permission-mode default"),
        "codex": "-a on-request -s read-only -c approvals_reviewer=user",
    },
    "accept-edits": dict.fromkeys(_CLAUDE_FAMILY, "--permission-mode acceptEdits"),
    "auto": {**dict.fromkeys(_CLAUDE_FAMILY, "--permission-mode auto"), "codex": "--approve-for-me"},
    "full-access": {
        **dict.fromkeys((*_CLAUDE_FAMILY, "openclaude", "antigravity"), "--dangerously-skip-permissions"),
        "codex": "--dangerously-bypass-approvals-and-sandbox",
        "copilot": "--yolo",
        "gemini": "--yolo",
    },
}


#: Agents with a permission choice in Orca 1.4.255: `hasConfigurableTuiAgentPermissions`, which is
#: the keys of `YOLO_TUI_AGENT_ARGS` and `YOLO_TUI_AGENT_ENV`. Orca keeps an override for others
#: (OpenCode, omp) but launches them the same in every mode and shows no choice, so it is not checked.
_CONFIGURABLE = frozenset(
    {
        "claude", "codebuddy", "claude-agent-teams", "openclaude", "codex", "qoder", "qoder-cn", "gemini",
        "antigravity", "aider", "amp", "kiro", "crush", "autohand", "cline", "command-code", "continue",
        "cursor", "kimi", "muse", "zcode", "mistral-vibe", "qwen-code", "rovo", "hermes", "copilot", "grok",
        "devin", "ante", "trae", "droid", "goose",
    }
)  # fmt: skip


def configurable(agent: str) -> bool:
    """Whether Orca offers ``agent`` a permission mode at all."""
    return agent in _CONFIGURABLE


def flags(mode: Permission, agent: str) -> str | None:
    """The flags Orca adds to ``agent``'s launch line in ``mode``, or None when rig does not know them."""
    return "" if mode == "own" else _FLAGS[mode].get(agent)


@dataclass(slots=True, frozen=True)
class AgentPermissionsPolicy:
    """The permission mode Orca should launch each agent with; agents not named take ``default``."""

    default: Permission = "own"
    agents: dict[str, Permission] = field(default_factory=dict[str, Permission])

    def wanted(self, agent: str) -> Permission:
        return self.agents.get(agent, self.default)


@dataclass(slots=True, frozen=True)
class AgentCheck:
    subject: str
    check: str
    have: str
    want: str
    fix: str

    @property
    def ok(self) -> bool:
        return self.have == self.want


def custom_args_checks(want: dict[str, str], stored: dict[str, str]) -> list[AgentCheck]:
    """Each policy agent's custom arguments as Orca stores them (`agentDefaultArgs`), against the policy."""
    checks: list[AgentCheck] = []
    for agent, args in sorted(want.items()):
        fix = f'Orca Settings → Agents → {agent}: set custom arguments to "{args.strip()}"'
        have = stored.get(agent, "").strip()
        checks.append(AgentCheck(f"agent {agent}", "custom args", have, args.strip(), fix))
    return checks


def permission_checks(
    policy: AgentPermissionsPolicy, agents: list[str], default: str | None, overrides: dict[str, str] | None
) -> list[AgentCheck]:
    """The default mode, and the mode of each policy agent and each ``agents`` entry, as Orca stores them.

    Pass Orca's override keys as ``agents``, so an override the policy does not expect is caught.
    Agents Orca offers no mode for are left out: their stored override changes nothing.
    """
    stored_default = default or "own"
    stored = overrides or {}
    checks = [
        AgentCheck(
            "settings",
            "permission default",
            stored_default,
            policy.default,
            f"Orca Settings → Agents: set the default permission to {_LABEL[policy.default]}",
        )
    ]
    for agent in sorted(a for a in {*agents, *policy.agents} if configurable(a)):
        want = policy.wanted(agent)
        fix = f"Orca Settings → Agents → {agent}: set permission to {_LABEL[want]}"
        checks.append(AgentCheck(f"agent {agent}", "permission", stored.get(agent, stored_default), want, fix))
    return checks


def launch_checks(
    policy: AgentPermissionsPolicy, custom_args: dict[str, str], launch: dict[str, str]
) -> list[AgentCheck]:
    """What the running Orca launches each policy agent with, against its mode's flags plus its custom arguments.

    ``launch`` is `settings.get`'s ``agentDefaultArgs``, which the runtime serves already composed.
    Compared as sets of tokens, since the order Orca joins the two in is not part of the policy.
    """
    checks: list[AgentCheck] = []
    for agent, line in sorted(_wanted_lines(policy, custom_args).items()):
        want = " ".join(sorted(line.split()))
        have = " ".join(sorted(launch.get(agent, "").split()))
        fix = "run `workspace orca sync --apply`, or set this agent in Orca Settings → Agents"
        checks.append(AgentCheck(f"agent {agent}", "launch args", have, want, fix))
    return checks


def _wanted_lines(policy: AgentPermissionsPolicy, custom_args: dict[str, str]) -> dict[str, str]:
    """Each policy agent's whole launch line; agents whose mode rig has no flags for are left out."""
    lines: dict[str, str] = {}
    for agent in {*custom_args, *policy.agents}:
        added = flags(policy.wanted(agent), agent) if configurable(agent) else ""
        if added is not None:
            lines[agent] = f"{added} {custom_args.get(agent, '').strip()}".strip()
    return lines


def launch_lines(policy: AgentPermissionsPolicy, custom_args: dict[str, str], launch: dict[str, str]) -> dict[str, str]:
    """The launch line to send for each policy agent whose live one differs, for `settings.update`.

    Orca splits a line it is sent: permission flags set the agent's mode and the rest its custom
    arguments, and a changed line with no flags sets Own. Send the whole live map with these merged
    in, since Orca replaces ``agentDefaultArgs`` whole; lines that equal the live one change nothing.
    """
    return {
        agent: line
        for agent, line in _wanted_lines(policy, custom_args).items()
        if sorted(line.split()) != sorted(launch.get(agent, "").split())
    }


def live_launch_args(user_data: Path | None = None) -> dict[str, str] | None:
    """Each agent's launch arguments from the running Orca, or None when it is not reachable.

    `settings.get` composes ``agentDefaultArgs`` with the permission flags, so these are not the
    custom arguments the backup stores, and must never be written back as them.
    """
    try:
        return OrcaRuntime.from_metadata(orca_runtime_metadata_path(user_data)).settings().agent_default_args or {}
    except OrcaUnavailableError:
        return None


def set_live_launch_args(lines: dict[str, str], user_data: Path | None = None) -> dict[str, str] | None:
    """Send ``lines`` merged onto the running Orca's launch lines; returns every line as Orca then
    serves it, or None when Orca could not be reached.
    """
    try:
        runtime = OrcaRuntime.from_metadata(orca_runtime_metadata_path(user_data))
        have = runtime.settings().agent_default_args or {}
        return runtime.set_agent_default_args({**have, **lines})
    except OrcaUnavailableError:
        return None
