"""A clock that only moves when something sleeps, for loops that take `monotonic` and `sleep` callables."""

from __future__ import annotations

from dataclasses import dataclass, field


@dataclass
class FakeClock:
    """`sleep` advances `monotonic` by exactly what was asked, and records it."""

    now: float = 0.0
    sleeps: list[float] = field(default_factory=list[float])

    def monotonic(self) -> float:
        return self.now

    def sleep(self, seconds: float) -> None:
        self.sleeps.append(seconds)
        self.now += seconds
