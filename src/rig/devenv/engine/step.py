"""One setup step: a named function plus how it is confirmed."""

from __future__ import annotations

from collections.abc import Callable
from dataclasses import dataclass

from .context import StepContext

StepFunc = Callable[[StepContext], None]


@dataclass(frozen=True, slots=True)
class Step:
    """A setup step. ``devenv setup`` runs an ordered list of these."""

    name: str
    description: str
    func: StepFunc
    default_confirm: bool = True
    confirm_message: str | None = None
