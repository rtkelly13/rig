"""Pure virtual desktop reconciliation models and planner (IaC).

Computes declarative drift between live virtual desktop names and declared target names.
Pure and leaf: depends only on standard libraries.
"""

from __future__ import annotations

from collections.abc import Callable, Sequence
from dataclasses import dataclass, field


@dataclass(frozen=True, slots=True)
class DesktopItem:
    """A virtual desktop identified by 1-based index and name."""

    index: int
    name: str


@dataclass(frozen=True, slots=True)
class DesktopCreationAction:
    """Action to create a missing virtual desktop to satisfy declared state."""

    index: int
    name: str
    created: bool = True


@dataclass(frozen=True, slots=True)
class DesktopRenameAction:
    """Action to reconcile a virtual desktop's name."""

    index: int
    current_name: str
    target_name: str
    success: bool = True
    error: str | None = None


@dataclass(frozen=True, slots=True)
class DesktopReconciliationPlan:
    """Declarative drift diff between actual virtual desktops and desired plan."""

    target_desktops: list[str]
    current_desktops: list[str]
    to_retire: list[str] = field(default_factory=list)
    to_create: list[DesktopCreationAction] = field(default_factory=list)
    to_rename: list[DesktopRenameAction] = field(default_factory=list)
    unchanged: list[DesktopItem] = field(default_factory=list)
    unmanaged: list[DesktopItem] = field(default_factory=list)

    @property
    def has_drift(self) -> bool:
        return bool(self.to_retire or self.to_create or self.to_rename)


@dataclass(frozen=True, slots=True)
class DesktopReconciliationResult:
    """Outcome of applying virtual desktop reconciliation to live environment."""

    plan: DesktopReconciliationPlan
    retired: list[str] = field(default_factory=list)
    created: list[DesktopCreationAction] = field(default_factory=list)
    renamed: list[DesktopRenameAction] = field(default_factory=list)
    failed_renames: list[DesktopRenameAction] = field(default_factory=list)
    unchanged: list[DesktopItem] = field(default_factory=list)
    unmanaged: list[DesktopItem] = field(default_factory=list)
    dry_run: bool = False

    @property
    def converged(self) -> bool:
        return len(self.failed_renames) == 0


def plan_virtual_desktops(
    target_desktops: Sequence[str],
    *,
    retired_names: Sequence[str] = (),
    current_names: Sequence[str] | None = None,
    names_fn: Callable[[], list[str]] | None = None,
) -> DesktopReconciliationPlan:
    """Calculate the declarative drift between live virtual desktops and declared targets.

    This pure inspection computes which desktops must be retired, created, renamed,
    or left unchanged without performing any mutations.
    """
    targets = list(target_desktops)
    retired_set = {n.casefold() for n in retired_names if n}
    if current_names is not None:
        current = list(current_names)
    elif names_fn is not None:
        current = names_fn()
    else:
        from desktop_manager.win32 import desktops

        current = desktops.virtual_desktop_names()

    to_retire: list[str] = []
    for name in current:
        if name and name.casefold() in retired_set:
            to_retire.append(name)

    post_retire: list[str] = [name for name in current if not (name and name.casefold() in retired_set)]
    if current and not post_retire:
        post_retire = [current[0]]

    target_count = len(targets)
    effective_count = len(post_retire)

    to_create: list[DesktopCreationAction] = []
    if effective_count < target_count:
        for i in range(effective_count + 1, target_count + 1):
            to_create.append(DesktopCreationAction(index=i, name=targets[i - 1]))

    to_rename: list[DesktopRenameAction] = []
    unchanged: list[DesktopItem] = []

    for i, target_name in enumerate(targets, start=1):
        if i <= effective_count:
            curr = post_retire[i - 1]
            if curr == target_name:
                unchanged.append(DesktopItem(index=i, name=target_name))
            else:
                to_rename.append(DesktopRenameAction(index=i, current_name=curr, target_name=target_name))
        else:
            to_rename.append(DesktopRenameAction(index=i, current_name="", target_name=target_name))

    unmanaged: list[DesktopItem] = []
    if effective_count > target_count:
        for i in range(target_count + 1, effective_count + 1):
            unmanaged.append(DesktopItem(index=i, name=post_retire[i - 1]))

    return DesktopReconciliationPlan(
        target_desktops=targets,
        current_desktops=current,
        to_retire=to_retire,
        to_create=to_create,
        to_rename=to_rename,
        unchanged=unchanged,
        unmanaged=unmanaged,
    )
