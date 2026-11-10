"""Orca's agent permission modes (Settings → Agents), as policy and as checks.

Since Orca 1.4.255 an agent's launch command is its permission mode's flags plus its custom
arguments. The upgrade folded the old per-agent flags into one Full access default, and the runtime
cannot set modes (`settings.update` rejects `agentPermissionOverrides`), so rig checks them and names
the change to make in Orca's Settings. See `docs/orca.md` (Agent permissions).
"""

from __future__ import annotations

from dataclasses import dataclass, field
from pathlib import Path
from typing import Literal

from rig.estate.orca_runtime import OrcaRuntime, OrcaUnavailableError
from rig.workspace.orca_rpc import orca_runtime_metadata_path

__all__ = [
    "AgentCheck",
    "AgentPermissionsPolicy",
    "Permission",
    "custom_args_checks",
    "flags",
    "launch_checks",
    "live_launch_args",
    "permission_checks",
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
    for agent in sorted({*agents, *policy.agents}):
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
    for agent in sorted({*custom_args, *policy.agents}):
        added = flags(policy.wanted(agent), agent)
        if added is None:
            continue
        want = " ".join(sorted(f"{added} {custom_args.get(agent, '')}".split()))
        have = " ".join(sorted(launch.get(agent, "").split()))
        fix = "fix this agent's permission and custom arguments in Orca Settings → Agents"
        checks.append(AgentCheck(f"agent {agent}", "launch args", have, want, fix))
    return checks


def live_launch_args(user_data: Path | None = None) -> dict[str, str] | None:
    """Each agent's launch arguments from the running Orca, or None when it is not reachable.

    `settings.get` composes ``agentDefaultArgs`` with the permission flags, so these are not the
    custom arguments the backup stores, and must never be written back as them.
    """
    try:
        return OrcaRuntime.from_metadata(orca_runtime_metadata_path(user_data)).settings().agent_default_args or {}
    except OrcaUnavailableError:
        return None
