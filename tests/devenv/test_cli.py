"""CLI execution tests."""

import pytest
from typer.testing import CliRunner

from rig.devenv.cli import app

runner = CliRunner()


@pytest.mark.ci
def test_devenv_help():
    result = runner.invoke(app, ["--help"])
    assert result.exit_code == 0
    assert "Unified development environment manager" in result.stdout


@pytest.mark.ci
def test_devenv_doctor():
    result = runner.invoke(app, ["doctor"])
    # Doctor runs and prints output
    assert "Development Environment Doctor" in result.stdout


@pytest.mark.ci
@pytest.mark.parametrize("args", [["setup", "--mode", "agent", "--dry-run"], ["ai", "--dry-run"]])
def test_setup_and_ai_hand_over_to_rig_devenv(csharp_calls: list[list[str]], args: list[str]):
    runner.invoke(app, args)

    assert [call[call.index("--") + 1 :] for call in csharp_calls] == [["devenv", *args]]
