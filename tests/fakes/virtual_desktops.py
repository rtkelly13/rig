"""A ``VirtualDesktops`` held in a list, for reconciliation to run against.

Every change it is asked to make is applied to ``state`` and recorded in ``calls``, so a
test can assert on the end state and, for a dry run, that nothing was asked at all.
"""

from __future__ import annotations

from collections.abc import Sequence

from desktop_manager.desktop_reconcile import VirtualDesktops


class FakeVirtualDesktops:
    """Virtual desktops held in a list; records every change it is asked to make."""

    def __init__(self, names: list[str], *, failing_renames: set[int] | None = None) -> None:
        self.state: list[str] = list(names)
        self.calls: list[str] = []
        self._failing_renames: set[int] = failing_renames or set()

    def as_port(self) -> VirtualDesktops:
        return self

    def names(self) -> list[str]:
        return list(self.state)

    def count(self) -> int:
        return len(self.state)

    def retire(self, keep_count: int, retired_names: Sequence[str]) -> list[str]:
        self.calls.append(f"retire {list(retired_names)}")
        retired = [name for name in self.state if name in retired_names]
        self.state = [name for name in self.state if name not in retired_names]
        return retired

    def ensure_count(self, count: int, settle_seconds: float) -> None:
        self.calls.append(f"ensure {count}")
        while len(self.state) < count:
            self.state.append("")

    def rename(self, desktop_index: int, name: str) -> bool:
        self.calls.append(f"rename {desktop_index} {name}")
        if desktop_index in self._failing_renames:
            return False
        self.state[desktop_index - 1] = name
        return True
