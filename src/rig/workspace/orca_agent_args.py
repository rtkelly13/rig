"""`workspace orca sync` for the arguments Orca adds when it launches each agent (`agentDefaultArgs`).

The policy rule lives in `docs/orca.md`: an agent uses its own auto mode where it has a workable one,
and a skip-permissions flag only when it prompts on every step and has none.
"""

from __future__ import annotations

from typing import Protocol

from rig.estate.orca_runtime import OrcaUnavailableError
from rig.workspace.orca import OrcaPolicy, SyncAction

__all__ = ["AgentArgsClient", "sync_agent_args"]

_FIELD = "default args"


class AgentArgsClient(Protocol):
    """The two runtime calls agent-argument sync needs; `OrcaRuntime` is the real one."""

    def agent_default_args(self) -> dict[str, str]: ...

    def set_agent_default_args(self, args: dict[str, str]) -> dict[str, str]: ...


def sync_agent_args(policy: OrcaPolicy, client: AgentArgsClient, *, apply: bool = False) -> list[SyncAction]:
    """Plan, and with ``apply`` make, each policy agent's default arguments in the running Orca.

    `settings.update` replaces the whole map, so agents the policy leaves out are sent back
    unchanged. Orca strips flags it knows an agent rejects; a value it does not keep as sent is an
    error, not a success. Raises `OrcaUnavailableError` when Orca cannot be read at all.
    """
    have = client.agent_default_args()
    diffs = [
        (agent, have.get(agent, ""), want)
        for agent, want in sorted(policy.agent_args.items())
        if have.get(agent, "").strip() != want.strip()
    ]
    if not diffs or not apply:
        return [SyncAction(f"agent {agent}", "update", _FIELD, old, new, False, "planned") for agent, old, new in diffs]
    try:
        stored = client.set_agent_default_args({**have, **policy.agent_args})
    except OrcaUnavailableError as e:
        return [SyncAction(f"agent {agent}", "error", _FIELD, old, new, False, str(e)) for agent, old, new in diffs]
    actions: list[SyncAction] = []
    for agent, old, new in diffs:
        kept = stored.get(agent, "").strip() == new.strip()
        detail = "" if kept else f'Orca stored "{stored.get(agent, "")}"; it strips flags this agent does not accept'
        actions.append(SyncAction(f"agent {agent}", "update" if kept else "error", _FIELD, old, new, kept, detail))
    return actions
