"""Tests for the personal gh scope overlay step."""

from pathlib import Path

import pytest

from rig.devenv.engine.context import StepContext, get_current_platform
from rig.devenv.steps import github_cli
from rig.devenv.steps.github_cli import OVERLAY_SOURCE, OverlayState, load_overlay, overlay_state


@pytest.fixture
def target(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> Path:
    monkeypatch.setenv("XDG_CONFIG_HOME", str(tmp_path))
    return tmp_path / "devenv" / "gh-scopes.json"


@pytest.mark.ci
def test_bundled_overlay_validates_with_unique_scopes():
    overlay = load_overlay()
    scopes = [e.scope for e in overlay.required + overlay.optional + overlay.disallowed]
    assert scopes
    assert len(scopes) == len(set(scopes))


@pytest.mark.ci
def test_overlay_target_honours_xdg_config_home(target: Path):
    assert github_cli.overlay_target() == target


@pytest.mark.ci
def test_overlay_state_transitions(target: Path):
    assert overlay_state(target=target) is OverlayState.MISSING
    target.parent.mkdir(parents=True)
    target.write_text('{"required": []}', encoding="utf-8")
    assert overlay_state(target=target) is OverlayState.OUT_OF_DATE
    target.write_text(OVERLAY_SOURCE.read_text(encoding="utf-8"), encoding="utf-8")
    assert overlay_state(target=target) is OverlayState.IN_SYNC


@pytest.mark.ci
def test_step_installs_overlay(target: Path):
    github_cli.run_github_cli_setup(StepContext(platform=get_current_platform(), mode="full"))
    assert overlay_state(target=target) is OverlayState.IN_SYNC


@pytest.mark.ci
def test_step_dry_run_writes_nothing(target: Path):
    github_cli.run_github_cli_setup(StepContext(platform=get_current_platform(), mode="full", dry_run=True))
    assert not target.exists()
