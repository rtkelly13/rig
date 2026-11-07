"""`desktop-manager name-desktops` retires leftovers, ensures desktop count, and names every planned desktop."""

from __future__ import annotations

from pathlib import Path

import pytest
import typer

from desktop_manager import cli_machine
from desktop_manager.models import DesktopPlan
from desktop_manager.plan import apply_feature_flags
from desktop_manager.windows_workspace import build_sample_plan


def test_tops_the_count_back_up_after_retiring(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    plan = build_sample_plan(tmp_path, terminal_count=1)
    plan.features = {"personal-desktop": False}
    plan.desktops.append(DesktopPlan(name="Personal", feature="personal-desktop"))
    plan = apply_feature_flags(plan)
    config = tmp_path / "workspace.json"
    config.write_text("{}", encoding="utf-8")
    monkeypatch.setattr("desktop_manager.windows_workspace.load_plan", lambda _path: plan)
    calls: list[str] = []

    def retire(keep_count: int, names: list[str]) -> list[str]:
        calls.append(f"retire {keep_count} {list(names)}")
        return ["Personal"]

    monkeypatch.setattr("desktop_manager.win32.desktops.retire_switched_off_desktops", retire)
    monkeypatch.setattr(
        "desktop_manager.win32.desktops.ensure_desktop_count",
        lambda count, _delay: calls.append(f"ensure {count}"),
    )
    monkeypatch.setattr(
        "desktop_manager.win32.desktops.rename_desktop",
        lambda index, name: calls.append(f"rename {index} {name}") or True,
    )

    # Past the Windows-only gate: every win32 call is faked, so this runs on any CI runner.
    cli_machine.name_desktops.__wrapped__(config=config, dry_run=False)

    assert calls == ["retire 2 ['Personal']", "ensure 2", "rename 1 Work", "rename 2 Admin"]


def test_ensures_desktop_count_even_without_retirements(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    """Cold start without retired desktops still ensures the required count exists."""
    plan = build_sample_plan(tmp_path, terminal_count=1)
    config = tmp_path / "workspace.json"
    config.write_text("{}", encoding="utf-8")
    monkeypatch.setattr("desktop_manager.windows_workspace.load_plan", lambda _path: plan)
    calls: list[str] = []

    monkeypatch.setattr(
        "desktop_manager.win32.desktops.ensure_desktop_count",
        lambda count, _delay: calls.append(f"ensure {count}"),
    )
    monkeypatch.setattr(
        "desktop_manager.win32.desktops.rename_desktop",
        lambda index, name: calls.append(f"rename {index} {name}") or True,
    )

    cli_machine.name_desktops.__wrapped__(config=config, dry_run=False)

    assert calls == ["ensure 2", "rename 1 Work", "rename 2 Admin"]


def test_name_desktops_dry_run_calls_no_win32_mutations(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    """Dry run performs no win32 mutations."""
    plan = build_sample_plan(tmp_path, terminal_count=1)
    config = tmp_path / "workspace.json"
    config.write_text("{}", encoding="utf-8")
    monkeypatch.setattr("desktop_manager.windows_workspace.load_plan", lambda _path: plan)
    calls: list[str] = []

    monkeypatch.setattr(
        "desktop_manager.win32.desktops.retire_switched_off_desktops",
        lambda *_args: calls.append("retire") or [],
    )
    monkeypatch.setattr(
        "desktop_manager.win32.desktops.ensure_desktop_count",
        lambda *_args: calls.append("ensure"),
    )
    monkeypatch.setattr(
        "desktop_manager.win32.desktops.rename_desktop",
        lambda *_args: calls.append("rename") or True,
    )

    cli_machine.name_desktops.__wrapped__(config=config, dry_run=True)

    assert calls == []


def test_name_desktops_exits_on_rename_failure(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    """When a desktop rename fails, CLI exits with non-zero code."""
    plan = build_sample_plan(tmp_path, terminal_count=1)
    config = tmp_path / "workspace.json"
    config.write_text("{}", encoding="utf-8")
    monkeypatch.setattr("desktop_manager.windows_workspace.load_plan", lambda _path: plan)

    monkeypatch.setattr(
        "desktop_manager.win32.desktops.ensure_desktop_count",
        lambda _count, _delay: None,
    )
    monkeypatch.setattr(
        "desktop_manager.win32.desktops.rename_desktop",
        lambda index, _name: index != 2,
    )

    with pytest.raises(typer.Exit) as exc_info:
        cli_machine.name_desktops.__wrapped__(config=config, dry_run=False)

    assert exc_info.value.exit_code == 1
