"""`rig browser setup` is the only agent-browser surface, and devenv's shortcuts pass real profile paths."""

from __future__ import annotations

import sys
from pathlib import Path

import pytest
from typer.testing import CliRunner

from rig.devenv.steps.ai_tools import agent_browser as devenv_step
from rig.rig_cli import app

pytestmark = pytest.mark.ci


def test_rig_browser_offers_only_setup() -> None:
    result = CliRunner().invoke(app, ["browser", "--help"])

    assert result.exit_code == 0, result.output
    assert "setup" in result.output
    assert "config" not in result.output
    assert "run" not in result.output.split("Commands")[-1]


@pytest.mark.skipif(sys.platform == "win32", reason="checks the zsh snippet")
def test_shortcuts_pass_profile_directories_and_leave_the_default_alone(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    monkeypatch.setattr(Path, "home", lambda: tmp_path)
    zshrc = tmp_path / ".zshrc"
    _ = zshrc.write_text("# existing\n", encoding="utf-8")

    devenv_step.inject_agent_browser_shell_helpers()
    devenv_step.inject_agent_browser_shell_helpers()

    text = zshrc.read_text(encoding="utf-8")
    assert text.count("# Agent Browser Helpers") == 1
    assert "--profile $HOME/.agent-browser-profiles/dev" in text
    assert "--profile $HOME/.agent-browser-profiles/personal" in text
    assert "AGENT_BROWSER_PROFILE" not in text
    assert "uv run agent-browser" not in text
