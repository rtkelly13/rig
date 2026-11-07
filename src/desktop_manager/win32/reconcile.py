from __future__ import annotations

from collections.abc import Callable, Sequence

from desktop_manager.desktop_reconcile import (
    DesktopCreationAction,
    DesktopItem,
    DesktopReconciliationPlan,
    DesktopReconciliationResult,
    DesktopRenameAction,
    plan_virtual_desktops,
)
from desktop_manager.win32 import desktops
from rig import tracing

_SCOPE = "desktop_manager.win32"

__all__ = [
    "DesktopCreationAction",
    "DesktopItem",
    "DesktopReconciliationPlan",
    "DesktopReconciliationResult",
    "DesktopRenameAction",
    "plan_virtual_desktops",
    "reconcile_virtual_desktops",
]


def reconcile_virtual_desktops(
    target_desktops: Sequence[str],
    *,
    retired_names: Sequence[str] = (),
    switch_delay_seconds: float = 0.65,
    dry_run: bool = False,
    retire_fn: Callable[[int, Sequence[str]], list[str]] | None = None,
    ensure_count_fn: Callable[[int, float], None] | None = None,
    rename_fn: Callable[[int, str], bool] | None = None,
    names_fn: Callable[[], list[str]] | None = None,
    count_fn: Callable[[], int] | None = None,
) -> DesktopReconciliationResult:
    """Reconcile Windows virtual desktops to converge to the declared target state (IaC).

    Ensures the exact declared virtual desktops exist and have the right names:
    1. Retires switched-off desktops matching ``retired_names``.
    2. Ensures the required desktop count exists (creating missing ones).
    3. Renames desktops 1..N to their declared target names (no-op if already matching).
    4. Detects and reports unmanaged surplus desktops.
    """
    targets = list(target_desktops)
    do_retire = retire_fn or desktops.retire_switched_off_desktops
    do_ensure_count = ensure_count_fn or desktops.ensure_desktop_count
    do_rename = rename_fn or desktops.rename_desktop
    get_names = names_fn or desktops.virtual_desktop_names
    get_count = count_fn or desktops.get_virtual_desktop_count

    with tracing.span(
        "win32.reconcile_virtual_desktops",
        scope=_SCOPE,
        **{
            "desktops.target_count": len(targets),
            "desktops.dry_run": dry_run,
        },
    ) as s:
        plan = plan_virtual_desktops(targets, retired_names=retired_names, names_fn=get_names)
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
            retired = do_retire(len(targets), retired_names)

        # 2. Ensure desktop count
        before_count = get_count()
        do_ensure_count(len(targets), switch_delay_seconds)
        after_count = get_count()

        created: list[DesktopCreationAction] = []
        if after_count > before_count:
            for i in range(before_count + 1, min(len(targets), after_count) + 1):
                created.append(DesktopCreationAction(index=i, name=targets[i - 1]))

        # 3. Rename desktops
        renamed: list[DesktopRenameAction] = []
        failed_renames: list[DesktopRenameAction] = []
        unchanged: list[DesktopItem] = []

        current_names_after = get_names()
        for i, target_name in enumerate(targets, start=1):
            curr = current_names_after[i - 1] if i <= len(current_names_after) else ""
            if curr == target_name:
                unchanged.append(DesktopItem(index=i, name=target_name))
                continue
            ok = do_rename(i, target_name)
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
