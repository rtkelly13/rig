"""The declared feature flags and their defaults."""

from __future__ import annotations

from dataclasses import dataclass


class UnknownFlagError(ValueError):
    """Raised for a flag name that `FLAGS` does not declare."""


@dataclass(frozen=True, slots=True)
class Flag:
    name: str
    default: bool
    description: str


FLAGS: tuple[Flag, ...] = (
    Flag("orca-sessions", True, "Work desktop: the Work and Personal Orca windows."),
    Flag("wt-terminals", False, "Work desktop: Windows Terminal sessions each running `claude`."),
    Flag("warp-terminals", False, "Work desktop: the earlier Warp terminal sessions."),
    Flag("work-chrome", True, "Work desktop screen 2: the work-profile Chrome window."),
    Flag("work-fork", False, "Work desktop screen 2: the Fork window, now on the Review desktop."),
    Flag("personal-desktop", False, "The Personal virtual desktop."),
    Flag("experimental-desktop", True, "The Lab virtual desktop."),
    Flag(
        "claude-session-restore",
        False,
        "At sign-in, resume cut-off Claude sessions and start `claude` in empty terminals.",
    ),
)

_BY_NAME = {flag.name: flag for flag in FLAGS}


def flag_named(name: str) -> Flag:
    flag = _BY_NAME.get(name)
    if flag is None:
        known = ", ".join(sorted(_BY_NAME))
        raise UnknownFlagError(f"Unknown flag: {name}. Declared flags: {known}")
    return flag
