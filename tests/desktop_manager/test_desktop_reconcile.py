"""Unit tests for declarative virtual desktop reconciliation (IaC)."""

from __future__ import annotations

import pytest

from desktop_manager.win32.reconcile import (
    DesktopCreationAction,
    DesktopItem,
    DesktopRenameAction,
    plan_virtual_desktops,
    reconcile_virtual_desktops,
)


@pytest.mark.ci
def test_plan_virtual_desktops_cold_start() -> None:
    """A fresh system with 1 unnamed desktop needs creation and naming for all planned desktops."""
    plan = plan_virtual_desktops(
        target_desktops=["Review", "Work", "Admin"],
        current_names=[""],
    )

    assert plan.has_drift is True
    assert plan.to_retire == []
    assert plan.to_create == [
        DesktopCreationAction(index=2, name="Work"),
        DesktopCreationAction(index=3, name="Admin"),
    ]
    assert plan.to_rename == [
        DesktopRenameAction(index=1, current_name="", target_name="Review"),
        DesktopRenameAction(index=2, current_name="", target_name="Work"),
        DesktopRenameAction(index=3, current_name="", target_name="Admin"),
    ]
    assert plan.unchanged == []
    assert plan.unmanaged == []


@pytest.mark.ci
def test_plan_virtual_desktops_no_drift_when_all_match() -> None:
    """When actual desktops match declared state in count and names, no drift is detected."""
    plan = plan_virtual_desktops(
        target_desktops=["Review", "Work", "Admin"],
        current_names=["Review", "Work", "Admin"],
    )

    assert plan.has_drift is False
    assert plan.to_retire == []
    assert plan.to_create == []
    assert plan.to_rename == []
    assert plan.unchanged == [
        DesktopItem(index=1, name="Review"),
        DesktopItem(index=2, name="Work"),
        DesktopItem(index=3, name="Admin"),
    ]
    assert plan.unmanaged == []


@pytest.mark.ci
def test_plan_virtual_desktops_detects_single_name_drift() -> None:
    """When desktop 1 name was dropped or reset, drift detects only that rename."""
    plan = plan_virtual_desktops(
        target_desktops=["Review", "Work", "Admin"],
        current_names=["Desktop 1", "Work", "Admin"],
    )

    assert plan.has_drift is True
    assert plan.to_retire == []
    assert plan.to_create == []
    assert plan.to_rename == [
        DesktopRenameAction(index=1, current_name="Desktop 1", target_name="Review"),
    ]
    assert plan.unchanged == [
        DesktopItem(index=2, name="Work"),
        DesktopItem(index=3, name="Admin"),
    ]
    assert plan.unmanaged == []


@pytest.mark.ci
def test_plan_virtual_desktops_identifies_retired_desktops() -> None:
    """Switched-off plan desktops are identified for retirement."""
    plan = plan_virtual_desktops(
        target_desktops=["Review", "Work", "Admin"],
        retired_names=["Personal"],
        current_names=["Review", "Personal", "Work", "Admin"],
    )

    assert plan.has_drift is True
    assert plan.to_retire == ["Personal"]
    assert plan.unchanged == [
        DesktopItem(index=1, name="Review"),
        DesktopItem(index=2, name="Work"),
        DesktopItem(index=3, name="Admin"),
    ]


@pytest.mark.ci
def test_plan_virtual_desktops_identifies_unmanaged_surplus() -> None:
    """Desktops beyond the declared target count are identified as unmanaged."""
    plan = plan_virtual_desktops(
        target_desktops=["Review", "Work"],
        current_names=["Review", "Work", "Extra1", "Extra2"],
    )

    assert plan.unchanged == [
        DesktopItem(index=1, name="Review"),
        DesktopItem(index=2, name="Work"),
    ]
    assert plan.unmanaged == [
        DesktopItem(index=3, name="Extra1"),
        DesktopItem(index=4, name="Extra2"),
    ]


@pytest.mark.ci
def test_reconcile_virtual_desktops_dry_run_performs_no_mutations() -> None:
    """Dry run calculates drift and returns reconciliation result without calling mutations."""
    mutations: list[str] = []

    result = reconcile_virtual_desktops(
        target_desktops=["Review", "Work", "Admin"],
        retired_names=["Personal"],
        dry_run=True,
        retire_fn=lambda _count, names: mutations.append(f"retire {list(names)}") or [],
        ensure_count_fn=lambda count, _delay: mutations.append(f"ensure {count}"),
        rename_fn=lambda idx, name: mutations.append(f"rename {idx} {name}") or True,
        names_fn=lambda: ["Review", "Personal"],
        count_fn=lambda: 2,
    )

    assert result.dry_run is True
    assert mutations == []
    assert result.plan.has_drift is True
    assert result.plan.to_retire == ["Personal"]


@pytest.mark.ci
def test_reconcile_virtual_desktops_converges_live_environment() -> None:
    """Live reconciliation retires, ensures desktop count, and renames mismatched desktops."""
    calls: list[str] = []
    current_state = ["Desktop 1", "Personal"]

    def fake_retire(_keep_count: int, names: list[str]) -> list[str]:
        calls.append(f"retire {list(names)}")
        current_state.remove("Personal")
        return ["Personal"]

    def fake_ensure_count(count: int, _delay: float) -> None:
        calls.append(f"ensure {count}")
        while len(current_state) < count:
            current_state.append("")

    def fake_rename(idx: int, name: str) -> bool:
        calls.append(f"rename {idx} {name}")
        current_state[idx - 1] = name
        return True

    result = reconcile_virtual_desktops(
        target_desktops=["Review", "Work", "Admin"],
        retired_names=["Personal"],
        dry_run=False,
        retire_fn=fake_retire,
        ensure_count_fn=fake_ensure_count,
        rename_fn=fake_rename,
        names_fn=lambda: list(current_state),
        count_fn=lambda: len(current_state),
    )

    assert result.dry_run is False
    assert result.converged is True
    assert result.retired == ["Personal"]
    assert len(result.created) == 2
    assert [r.target_name for r in result.renamed] == ["Review", "Work", "Admin"]
    assert calls == [
        "retire ['Personal']",
        "ensure 3",
        "rename 1 Review",
        "rename 2 Work",
        "rename 3 Admin",
    ]


@pytest.mark.ci
def test_reconcile_virtual_desktops_reports_rename_failures() -> None:
    """When a desktop rename fails, it is recorded in failed_renames and converged is False."""
    current_state = ["", "", ""]

    def failing_rename(idx: int, name: str) -> bool:
        if idx == 2:
            return False
        current_state[idx - 1] = name
        return True

    result = reconcile_virtual_desktops(
        target_desktops=["Review", "Work", "Admin"],
        dry_run=False,
        retire_fn=lambda _c, _n: [],
        ensure_count_fn=lambda _c, _d: None,
        rename_fn=failing_rename,
        names_fn=lambda: list(current_state),
        count_fn=lambda: len(current_state),
    )

    assert result.converged is False
    assert len(result.failed_renames) == 1
    assert result.failed_renames[0].index == 2
    assert result.failed_renames[0].target_name == "Work"
    assert result.failed_renames[0].success is False
