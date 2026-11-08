"""Virtual desktop reconciliation (IaC): the drift planner and the orchestration that applies it.

:func:`plan_virtual_desktops` computes the drift between live desktop names and the
declared ones. :func:`reconcile_virtual_desktops` applies it through a
:class:`VirtualDesktops` port; ``win32.desktops.Win32VirtualDesktops`` is the real machine.
Nothing here imports Win32.
"""

from __future__ import annotations

from collections.abc import Sequence
from dataclasses import dataclass, field
from typing import Protocol

from rig import tracing

_SCOPE = "desktop_manager.win32"


class VirtualDesktops(Protocol):
    """The virtual desktops reconciliation reads and changes."""

    def names(self) -> list[str]: ...
    def count(self) -> int: ...
    def retire(self, keep_count: int, retired_names: Sequence[str]) -> list[str]: ...
    def ensure_count(self, count: int, settle_seconds: float) -> None: ...
    def rename(self, desktop_index: int, name: str) -> bool: ...


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
    current_names: Sequence[str],
) -> DesktopReconciliationPlan:
    """Calculate the declarative drift between live virtual desktops and declared targets.

    This pure inspection computes which desktops must be retired, created, renamed,
    or left unchanged without performing any mutations.
    """
    targets = list(target_desktops)
    retired_set = {n.casefold() for n in retired_names if n}
    current = list(current_names)

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


def reconcile_virtual_desktops(
    target_desktops: Sequence[str],
    desktops: VirtualDesktops,
    *,
    retired_names: Sequence[str] = (),
    switch_delay_seconds: float = 0.65,
    dry_run: bool = False,
) -> DesktopReconciliationResult:
    """Reconcile Windows virtual desktops to converge to the declared target state (IaC).

    Ensures the exact declared virtual desktops exist and have the right names:
    1. Retires switched-off desktops matching ``retired_names``.
    2. Ensures the required desktop count exists (creating missing ones).
    3. Renames desktops 1..N to their declared target names (no-op if already matching).
    4. Detects and reports unmanaged surplus desktops.
    """
    targets = list(target_desktops)

    with tracing.span(
        "win32.reconcile_virtual_desktops",
        scope=_SCOPE,
        **{
            "desktops.target_count": len(targets),
            "desktops.dry_run": dry_run,
        },
    ) as s:
        plan = plan_virtual_desktops(targets, retired_names=retired_names, current_names=desktops.names())
        s.set(
            **{
                "desktops.has_drift": plan.has_drift,
                "desktops.to_create": len(plan.to_create),
                "desktops.to_rename": len(plan.to_rename),
                "desktops.to_retire": len(plan.to_retire),
            }
        )

        if dry_run:
            return DesktopReconciliationResult(
                plan=plan,
                retired=[],
                created=[],
                renamed=[],
                failed_renames=[],
                unchanged=plan.unchanged,
                unmanaged=plan.unmanaged,
                dry_run=True,
            )

        # 1. Retire switched-off desktops
        retired: list[str] = []
        if retired_names:
            retired = desktops.retire(len(targets), retired_names)

        # 2. Ensure desktop count
        before_count = desktops.count()
        desktops.ensure_count(len(targets), switch_delay_seconds)
        after_count = desktops.count()

        created: list[DesktopCreationAction] = []
        if after_count > before_count:
            for i in range(before_count + 1, min(len(targets), after_count) + 1):
                created.append(DesktopCreationAction(index=i, name=targets[i - 1]))

        # 3. Rename desktops
        renamed: list[DesktopRenameAction] = []
        failed_renames: list[DesktopRenameAction] = []
        unchanged: list[DesktopItem] = []

        current_names_after = desktops.names()
        for i, target_name in enumerate(targets, start=1):
            curr = current_names_after[i - 1] if i <= len(current_names_after) else ""
            if curr == target_name:
                unchanged.append(DesktopItem(index=i, name=target_name))
                continue
            ok = desktops.rename(i, target_name)
            action = DesktopRenameAction(
                index=i,
                current_name=curr,
                target_name=target_name,
                success=ok,
                error=None if ok else "Rename failed",
            )
            renamed.append(action)
            if not ok:
                failed_renames.append(action)

        unmanaged: list[DesktopItem] = []
        if len(current_names_after) > len(targets):
            for i in range(len(targets) + 1, len(current_names_after) + 1):
                unmanaged.append(DesktopItem(index=i, name=current_names_after[i - 1]))

        res = DesktopReconciliationResult(
            plan=plan,
            retired=retired,
            created=created,
            renamed=renamed,
            failed_renames=failed_renames,
            unchanged=unchanged,
            unmanaged=unmanaged,
            dry_run=False,
        )
        s.set(**{"desktops.converged": res.converged})
        return res
