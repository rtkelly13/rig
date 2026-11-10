"""`workspace orca sync` for agents: each one's launch line, sent through Orca's runtime.

A launch line is an agent's permission mode's flags joined to its custom arguments. Orca splits a
line it is sent into those two halves again, so one `settings.update` sets both.
See `docs/orca.md` (Agent permissions).
"""

from __future__ import annotations

from collections.abc import Callable

from rig.workspace.orca import OrcaPolicy, OrcaSnapshot, SyncAction
from rig.workspace.orca_agent_permissions import launch_lines

__all__ = ["LaunchWriter", "sync_agent_launch"]

_UNREACHABLE = "Orca's runtime could not be reached; these settings need Orca running"


type LaunchWriter = Callable[[dict[str, str]], dict[str, str] | None]
"""Sends launch lines merged onto the live ones; returns every line as Orca then serves it, or None
when Orca could not be reached."""


def sync_agent_launch(
    policy: OrcaPolicy, snapshot: OrcaSnapshot, *, apply: bool = False, write: LaunchWriter | None = None
) -> list[SyncAction]:
    """Plan, and with ``apply`` send, each policy agent's launch line: its mode's flags and custom arguments.

    Orca splits each line into the agent's permission mode and custom arguments, so one write sets
    both. A line Orca does not serve back as sent is an error: Orca keeps a mode when a line with no
    flags is sent unchanged, so moving such an agent back to Own is left to Orca's Settings.
    """
    if (want := policy.agent_permissions) is None or snapshot.launch_args is None:
        return []
    lines = launch_lines(want, policy.agent_args, snapshot.launch_args)
    if not lines:
        return []
    have = snapshot.launch_args
    if not apply or write is None:
        return [_launch_action(agent, have, line, False, "planned") for agent, line in sorted(lines.items())]
    served = write(lines)
    if served is None:
        return [_launch_action(agent, have, line, False, _UNREACHABLE) for agent, line in sorted(lines.items())]
    actions: list[SyncAction] = []
    for agent, line in sorted(lines.items()):
        now = served.get(agent, "")
        taken = sorted(now.split()) == sorted(line.split())
        detail = "" if taken else f"Orca serves {now!r}; set this agent in Orca Settings → Agents"
        actions.append(_launch_action(agent, have, line, taken, detail))
    return actions


def _launch_action(agent: str, have: dict[str, str], line: str, applied: bool, detail: str) -> SyncAction:
    action = "update" if applied or detail == "planned" else "error"
    return SyncAction(f"agent {agent}", action, "launch args", have.get(agent, ""), line, applied, detail)
