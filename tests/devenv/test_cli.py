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
