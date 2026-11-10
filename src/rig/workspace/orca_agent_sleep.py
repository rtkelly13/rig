"""Orca's experimental Agent sleep (`experimentalAgentHibernation`), as policy and as a check.

The runtime's `settings.update` rejects these keys and Orca's database must not be edited while it
runs, so rig only checks the setting and names the change to make in Orca's Settings. See
`docs/orca.md` (Agent sleep).
"""

from __future__ import annotations

from dataclasses import dataclass
from typing import Annotated

from pydantic import Field

__all__ = ["AgentSleepCheck", "AgentSleepPolicy", "agent_sleep_check"]

#: Orca's default idle window, used for a missing or out-of-range value.
_DEFAULT_MINUTES = 30


@dataclass(slots=True, frozen=True)
class AgentSleepPolicy:
    """Orca's experimental Agent sleep: stop idle agent terminals, resume them when opened."""

    enabled: bool
    #: Orca accepts 1 minute to 24 hours, and falls back to 30 minutes outside that range.
    idle_minutes: Annotated[int, Field(ge=1, le=24 * 60)] = _DEFAULT_MINUTES


@dataclass(slots=True, frozen=True)
class AgentSleepCheck:
    have: str
    want: str
    fix: str

    @property
    def ok(self) -> bool:
        return self.have == self.want


def agent_sleep_check(want: AgentSleepPolicy, enabled: bool | None, idle_ms: int | None) -> AgentSleepCheck:
    """Agent sleep as Orca has it and as the policy wants it, both written ``off`` or ``on, 30m idle``."""
    # Orca reads a missing or out-of-range window as its 30-minute default.
    in_range = idle_ms is not None and 60_000 <= idle_ms <= 86_400_000
    idle_minutes = idle_ms // 60_000 if idle_ms is not None and in_range else _DEFAULT_MINUTES
    have = f"on, {idle_minutes}m idle" if enabled is True else "off"
    if not want.enabled:
        return AgentSleepCheck(have, "off", "Orca Settings → Experimental: turn Agent sleep off")
    fix = f"Orca Settings → Experimental: turn Agent sleep on with a {want.idle_minutes} minute idle window"
    return AgentSleepCheck(have, f"on, {want.idle_minutes}m idle", fix)
