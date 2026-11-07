"""What happened to each session a backend resumed or started fresh."""

from __future__ import annotations

from dataclasses import dataclass

from desktop_manager.claude_sessions.plan import Slot
from desktop_manager.claude_sessions.startup import FreshTarget


@dataclass(frozen=True)
class Outcome:
    slot: Slot
    ok: bool
    detail: str
    pid: int | None = None


@dataclass(frozen=True, slots=True)
class FreshOutcome:
    target: FreshTarget
    ok: bool
    detail: str
