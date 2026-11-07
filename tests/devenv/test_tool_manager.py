"""Tests for package managers and ToolManager abstraction."""

from unittest.mock import MagicMock

import pytest

from rig.devenv.config.models import ToolDefinition, ToolInstallSpec
from rig.devenv.engine.context import Platform
from rig.devenv.tool_manager import ToolManager


@pytest.mark.ci
def test_tool_manager_installed_via_check_cmd(monkeypatch: pytest.MonkeyPatch):
    monkeypatch.setattr("shutil.which", lambda cmd: "/usr/local/bin/git" if cmd == "git" else None)

    mgr = ToolManager(platform=Platform.MACOS)
    tool = ToolDefinition(
        id="git",
        name="Git",
        description="Version control",
        install=ToolInstallSpec(check_cmd="git", brew="git"),
    )

    assert mgr.is_tool_installed(tool)


@pytest.mark.ci
def test_tool_manager_install_via_brew(monkeypatch: pytest.MonkeyPatch):
    monkeypatch.setattr("shutil.which", lambda cmd: None)

    mgr = ToolManager(platform=Platform.MACOS)
    mock_brew = MagicMock()
    mock_brew.is_available.return_value = True
    mock_brew.is_installed.return_value = False
    mock_brew.install.return_value = True
    mgr.brew = mock_brew

    tool = ToolDefinition(
        id="ripgrep",
        name="ripgrep",
        description="Search tool",
        install=ToolInstallSpec(brew="ripgrep"),
    )

    assert not mgr.is_tool_installed(tool)
    success = mgr.install_tool(tool, silent=True)
    assert success
    mock_brew.install.assert_called_once_with("ripgrep", silent=True)


@pytest.mark.ci
def test_tool_manager_install_via_winget(monkeypatch: pytest.MonkeyPatch):
    monkeypatch.setattr("shutil.which", lambda cmd: None)

    mgr = ToolManager(platform=Platform.WINDOWS)
    mock_winget = MagicMock()
    mock_winget.is_available.return_value = True
    mock_winget.is_installed.return_value = False
    mock_winget.install.return_value = True
    mgr.winget = mock_winget

    tool = ToolDefinition(
        id="ripgrep",
        name="ripgrep",
        description="Search tool",
        install=ToolInstallSpec(winget="BurntSushi.ripgrep.MSVC"),
    )

    assert not mgr.is_tool_installed(tool)
    success = mgr.install_tool(tool, silent=True)
    assert success
    mock_winget.install.assert_called_once_with("BurntSushi.ripgrep.MSVC", silent=True)
