"""Tests for the step context and the ordered setup step list."""

import pytest

from rig.devenv.cli import SETUP_STEPS
from rig.devenv.engine.context import Platform, StepContext


@pytest.mark.ci
def test_step_context_properties():
    ctx_win = StepContext(platform=Platform.WINDOWS)
    assert ctx_win.is_windows
    assert not ctx_win.is_macos

    ctx_mac = StepContext(platform=Platform.MACOS)
    assert ctx_mac.is_macos
    assert not ctx_mac.is_windows


@pytest.mark.ci
def test_setup_steps_run_in_declared_order():
    assert [s.name for s in SETUP_STEPS] == [
        "AI Coding Tools & Skills",
        "Font Installation",
        "Git & SSH Setup",
        "GitHub CLI Scopes",
        "Terminal Customization",
        "Runtimes",
        "Development Tools",
        "VS Code Settings",
    ]


@pytest.mark.ci
def test_only_terminal_customization_is_skipped_by_express_mode():
    assert [s.name for s in SETUP_STEPS if not s.default_confirm] == ["Terminal Customization"]
