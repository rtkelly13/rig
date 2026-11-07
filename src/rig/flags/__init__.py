"""Feature flags shared across the rig stack, with defaults in code and overrides in ``rig.db``.

Every flag is declared once in `FLAGS` with its default and what it switches.
`rig flags enable|disable <name>` stores an override in the `feature_flags`
table, and `rig flags reset <name>` removes it so the default applies again.
Any tool reads the effective state with `flag_states()` or `is_enabled()`.
"""

from __future__ import annotations

from rig.flags.registry import FLAGS, Flag, UnknownFlagError, flag_named
from rig.flags.store import FlagState, clear_override, effective_flags, flag_states, is_enabled, set_override

__all__ = [
    "FLAGS",
    "Flag",
    "FlagState",
    "UnknownFlagError",
    "clear_override",
    "effective_flags",
    "flag_named",
    "flag_states",
    "is_enabled",
    "set_override",
]
