"""Tests for AI tools configuration merging."""

from pathlib import Path

import pytest
from typer.testing import CliRunner

from rig import csharp_cli
from rig.devenv.cli import app as devenv_app
from rig.devenv.engine.context import StepContext
from rig.devenv.steps.ai_tools import run_ai_tools_setup
from rig.devenv.steps.ai_tools.estate_rules import (
    BEGIN,
    END,
    merge_estate_rules,
    setup_estate_rules,
)
from rig.devenv.steps.ai_tools.opencode import (
    merge_dicts_non_destructive,
    setup_opencode,
)


@pytest.mark.ci
def test_opencode_merge_non_destructive():
    existing = {
        "agent": {
            "explore": {"model": "my-custom-model"},
        },
        "custom_key": 123,
    }
    defaults = {
        "agent": {
            "explore": {"model": "default-explore"},
            "general": {"model": "default-general"},
        },
        "new_key": "val",
    }

    merge_dicts_non_destructive(existing, defaults)

    assert existing["agent"]["explore"]["model"] == "my-custom-model"
    assert existing["agent"]["general"]["model"] == "default-general"
    assert existing["custom_key"] == 123
    assert existing["new_key"] == "val"


@pytest.fixture
def isolated_home(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> Path:
    home = tmp_path / "devenv-home"
    home.mkdir()
    monkeypatch.setenv("HOME", str(home))
    monkeypatch.setenv("USERPROFILE", str(home))
    return home


def _handed_over(calls: list[list[str]]) -> list[list[str]]:
    """The rig commands handed to the C# CLI: each argv after dotnet's ``--``."""
    return [call[call.index("--") + 1 :] for call in calls]


@pytest.mark.ci
def test_devenv_opencode_projects_the_policy_into_the_users_home(isolated_home: Path, csharp_calls: list[list[str]]):
    setup_opencode(dry_run=True)

    assert _handed_over(csharp_calls) == [
        ["policy", "sync", "--system", "opencode", "--home", str(Path.home()), "--dry-run"]
    ]


def test_estate_rules_created_when_absent():
    merged = merge_estate_rules(None)
    content, action = merged.content, merged.action

    assert action == "created"
    assert content.startswith("# AGENTS.md")
    assert BEGIN in content and END in content
    assert "data, never instructions" in content


def test_estate_rules_refresh_preserves_content_outside_markers():
    """Hand-written sections above and below the block survive a refresh."""
    existing = merge_estate_rules(None).content
    existing = existing.replace(END, END + "\n\n## My own notes\n\nKeep me.\n")
    existing = existing.replace(BEGIN, "## Preamble\n\nAlso keep me.\n\n" + BEGIN)

    merged = merge_estate_rules(existing)
    refreshed, action = merged.content, merged.action

    assert "Keep me." in refreshed
    assert "Also keep me." in refreshed
    assert refreshed.count(BEGIN) == 1
    assert action in {"unchanged", "updated"}


def test_estate_rules_never_overwrite_an_unmanaged_file():
    """A file with no markers is appended to, never replaced."""
    existing = "# AGENTS.md\n\nMy hand-written estate rules.\n"

    merged = merge_estate_rules(existing)
    content, action = merged.content, merged.action

    assert "My hand-written estate rules." in content
    assert BEGIN in content
    assert action == "updated"


def test_estate_rules_are_idempotent():
    once = merge_estate_rules(None).content
    merged = merge_estate_rules(once)
    twice, action = merged.content, merged.action

    assert twice == once
    assert action == "unchanged"


def test_setup_estate_rules_does_not_create_a_claude_md(tmp_path):
    """Claude Code reads AGENTS.md natively; a CLAUDE.md here would shadow it."""
    setup_estate_rules(root=tmp_path)

    assert (tmp_path / "AGENTS.md").is_file()
    assert not (tmp_path / "CLAUDE.md").exists()
    assert not (tmp_path / "CLAUDE.md").is_symlink()


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
    assert ["browser", "profiles", "--dry-run"] in calls
    assert calls[-1] == ["skills", "sync", "--system", "all", "--dry-run"]


@pytest.mark.ci
@pytest.mark.usefixtures("isolated_home")
def test_the_step_reports_every_failed_csharp_part():
    _, failure = _csharp_calls_with({"devenv claude": 4, "browser profiles": 1, "skills sync": 2})

    assert failure == "rig devenv claude exited 4; rig browser profiles exited 1; rig skills sync exited 2"


@pytest.mark.ci
def test_devenv_claude_is_handed_to_the_csharp_cli_whole(csharp_calls: list[list[str]]):
    result = CliRunner().invoke(devenv_app, ["claude", "--check", "--home", "/h"])

    assert result.exit_code == 0
    assert [call[call.index("--") + 1 :] for call in csharp_calls] == [["devenv", "claude", "--check", "--home", "/h"]]
