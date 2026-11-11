"""Tests for AI tools configuration merging."""

from pathlib import Path

import pytest
from typer.testing import CliRunner

from rig import csharp_cli
from rig.devenv.cli import app as devenv_app
from rig.devenv.engine.context import StepContext
from rig.devenv.steps.ai_tools import run_ai_tools_setup


@pytest.fixture
def isolated_home(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> Path:
    home = tmp_path / "devenv-home"
    home.mkdir()
    monkeypatch.setenv("HOME", str(home))
    monkeypatch.setenv("USERPROFILE", str(home))
    return home


def _csharp_calls_with(code_for: dict[str, int]) -> tuple[list[list[str]], str | None]:
    """Run the step dry in the isolated home; each C# command exits as *code_for* says (default 0)."""
    calls: list[list[str]] = []

    def runner(command: list[str], _env: dict[str, str]) -> int:
        args = command[command.index("--") + 1 :]
        calls.append(args)
        return code_for.get(" ".join(args[:2]), 0)

    token = csharp_cli.RUNNER.set(runner)
    try:
        run_ai_tools_setup(StepContext(dry_run=True))
    except RuntimeError as failed:
        return calls, str(failed)
    finally:
        csharp_cli.RUNNER.reset(token)
    return calls, None


@pytest.mark.ci
@pytest.mark.usefixtures("isolated_home")
def test_a_failed_browser_profiles_fails_the_step_after_the_rest_runs():
    calls, failure = _csharp_calls_with({"browser profiles": 3})

    assert failure == "rig browser profiles exited 3"
    assert calls[0] == ["devenv", "claude", "--dry-run"]
    assert calls[1:5] == [
        ["devenv", "opencode", "--dry-run"],
        ["browser", "profiles", "--dry-run"],
        ["devenv", "estate-rules", "--dry-run"],
        ["devenv", "obsidian-plugins", "--dry-run"],
    ]
    assert calls[-1] == ["skills", "sync", "--system", "all", "--dry-run"]


@pytest.mark.ci
@pytest.mark.usefixtures("isolated_home")
def test_the_step_reports_every_failed_csharp_part():
    _, failure = _csharp_calls_with({"devenv claude": 4, "devenv opencode": 5, "browser profiles": 1, "skills sync": 2})

    assert failure == (
        "rig devenv claude exited 4; rig devenv opencode exited 5; "
        "rig browser profiles exited 1; rig skills sync exited 2"
    )


@pytest.mark.ci
def test_devenv_claude_is_handed_to_the_csharp_cli_whole(csharp_calls: list[list[str]]):
    result = CliRunner().invoke(devenv_app, ["claude", "--check", "--home", "/h"])

    assert result.exit_code == 0
    assert [call[call.index("--") + 1 :] for call in csharp_calls] == [["devenv", "claude", "--check", "--home", "/h"]]
