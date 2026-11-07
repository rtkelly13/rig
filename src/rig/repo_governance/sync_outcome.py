"""The result shape shared by the governance sync commands."""

from __future__ import annotations

from dataclasses import dataclass


@dataclass(frozen=True, slots=True)
class SyncOutcome:
    """Whether a sync changed (or, on a dry run, would change) anything, and a line per change."""

    changed: bool
    messages: list[str]
