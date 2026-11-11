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
def test_the_gh_setup_step_hands_over_to_rig_devenv_gh(csharp_calls: list[list[str]]):
    from rig.devenv.cli import SETUP_STEPS
    from rig.devenv.engine.context import StepContext, get_current_platform

    step = next(step for step in SETUP_STEPS if step.name == "GitHub CLI Scopes")
    step.func(StepContext(platform=get_current_platform(), mode="full", dry_run=True))

    assert [call[call.index("--") + 1 :] for call in csharp_calls] == [["devenv", "gh", "--dry-run"]]


@pytest.mark.ci
def test_the_fonts_setup_step_hands_over_to_rig_devenv_fonts(csharp_calls: list[list[str]]):
    from rig.devenv.cli import SETUP_STEPS
    from rig.devenv.engine.context import StepContext, get_current_platform

    step = next(step for step in SETUP_STEPS if step.name == "Font Installation")
    step.func(StepContext(platform=get_current_platform(), mode="full"))

    assert [call[call.index("--") + 1 :] for call in csharp_calls] == [["devenv", "fonts"]]


@pytest.mark.ci
def test_the_gh_setup_step_fails_when_rig_devenv_gh_does():
    from rig import csharp_cli
    from rig.devenv.cli import SETUP_STEPS
    from rig.devenv.engine.context import StepContext, get_current_platform

    def failing(_command: list[str], _env: dict[str, str]) -> int:
        return 4

    step = next(step for step in SETUP_STEPS if step.name == "GitHub CLI Scopes")
    token = csharp_cli.RUNNER.set(failing)
    try:
        with pytest.raises(RuntimeError, match="rig devenv gh exited 4"):
            step.func(StepContext(platform=get_current_platform(), mode="full"))
    finally:
        csharp_cli.RUNNER.reset(token)


@pytest.mark.ci
def test_the_tools_setup_step_hands_over_its_mode(csharp_calls: list[list[str]]):
    from rig.devenv.cli import SETUP_STEPS
    from rig.devenv.engine.context import StepContext, get_current_platform

    step = next(step for step in SETUP_STEPS if step.name == "Development Tools")
    step.func(StepContext(platform=get_current_platform(), mode="express", dry_run=True))
    step.func(StepContext(platform=get_current_platform(), mode="full", use_silent_mode=False))

    assert [call[call.index("--") + 1 :] for call in csharp_calls] == [
        ["devenv", "tools", "--dry-run", "--mode", "express"],
        ["devenv", "tools", "--mode", "full", "--interactive"],
    ]


@pytest.mark.ci
def test_the_runtimes_setup_step_hands_over_to_rig_devenv_runtimes(csharp_calls: list[list[str]]):
    from rig.devenv.cli import SETUP_STEPS
    from rig.devenv.engine.context import StepContext, get_current_platform

    step = next(step for step in SETUP_STEPS if step.name == "Runtimes")
    step.func(StepContext(platform=get_current_platform(), mode="full"))

    assert [call[call.index("--") + 1 :] for call in csharp_calls] == [["devenv", "runtimes"]]


@pytest.mark.ci
def test_the_git_setup_step_hands_over_its_mode(csharp_calls: list[list[str]]):
    from rig.devenv.cli import SETUP_STEPS
    from rig.devenv.engine.context import StepContext, get_current_platform

    step = next(step for step in SETUP_STEPS if step.name == "Git & SSH Setup")
    step.func(StepContext(platform=get_current_platform(), mode="express", dry_run=True))
    step.func(StepContext(platform=get_current_platform(), mode="full", use_silent_mode=False))

    assert [call[call.index("--") + 1 :] for call in csharp_calls] == [
        ["devenv", "git", "--dry-run", "--mode", "express"],
        ["devenv", "git", "--mode", "full"],
    ]


@pytest.mark.ci
def test_the_terminal_setup_step_hands_over_to_rig_devenv_terminal(csharp_calls: list[list[str]]):
    from rig.devenv.cli import SETUP_STEPS
    from rig.devenv.engine.context import StepContext, get_current_platform

    step = next(step for step in SETUP_STEPS if step.name == "Terminal Customization")
    step.func(StepContext(platform=get_current_platform(), mode="full", dry_run=True))

    assert [call[call.index("--") + 1 :] for call in csharp_calls] == [["devenv", "terminal", "--dry-run"]]
