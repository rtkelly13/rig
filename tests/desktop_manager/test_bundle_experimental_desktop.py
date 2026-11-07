"""The real bundle plan keeps the Lab desktop behind the `experimental-desktop` feature."""

from __future__ import annotations

from pathlib import Path

import pytest

from desktop_manager.windows_workspace import load_plan
from rig.devenv.config.loader import load_all_builtin_tools

pytestmark = pytest.mark.ci

BUNDLE_PLAN_PATH = Path(__file__).resolve().parents[2] / "desktop-manager" / "workspace-plan.bundle.json"


def test_lab_desktop_is_on_by_default_with_t3_code_and_a_claude_terminal():
    plan = load_plan(BUNDLE_PLAN_PATH)
    lab = plan.desktops[-1]
    assert lab.name == "Lab"
    active = [entry.name for entry in lab.launch if not entry.disabled]
    assert active == ["Lab T3 Code", "Lab Fork", "Lab Terminal"]


def test_orca_lives_only_on_the_work_desktop():
    plan = load_plan(BUNDLE_PLAN_PATH)
    orca = {
        desktop.name: [
            entry.name for entry in desktop.launch if not entry.disabled and entry.window_process_name == "Orca.exe"
        ]
        for desktop in plan.desktops
    }
    assert {name: entries for name, entries in orca.items() if entries} == {"Work": ["Work Orca", "Personal Orca"]}


def test_disabling_the_feature_drops_the_lab_desktop():
    plan = load_plan(BUNDLE_PLAN_PATH, disable_features=["experimental-desktop"])
    assert "Lab" not in [desktop.name for desktop in plan.desktops]


def test_lab_desktop_tooling_is_installable_through_devenv():
    tools = {tool.id: tool for tool in load_all_builtin_tools()}
    assert tools["t3code"].install.winget == "T3Tools.T3Code"
    assert tools["fork"].install.winget == "Fork.Fork"
