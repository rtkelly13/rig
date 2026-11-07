"""Tests for system setup steps (runtimes, git, fonts)."""

from pathlib import Path

import pytest

from rig.devenv.config.models import DevenvConfig, GitIdentity
from rig.devenv.engine.context import StepContext
from rig.devenv.steps.git import apply_conditional_git_includes, run_git_setup
from rig.devenv.steps.runtimes import detect_required_python_version


@pytest.mark.ci
def test_detect_required_python_version_default(tmp_path: Path, monkeypatch: pytest.MonkeyPatch):
    monkeypatch.chdir(tmp_path)
    assert detect_required_python_version() == "3.13"


@pytest.mark.ci
def test_detect_required_python_version_from_pyproject(tmp_path: Path, monkeypatch: pytest.MonkeyPatch):
    monkeypatch.chdir(tmp_path)
    pyproject = tmp_path / "pyproject.toml"
    pyproject.write_text('[project]\nrequires-python = ">=3.12"\n', encoding="utf-8")
    assert detect_required_python_version() == "3.12"


@pytest.mark.ci
def test_apply_conditional_git_includes(tmp_path: Path, monkeypatch: pytest.MonkeyPatch):
    monkeypatch.setattr("pathlib.Path.home", lambda: tmp_path)

    identities = [
        GitIdentity(
            id="personal",
            name="Personal Dev",
            email="personal@example.com",
            match_directory="~/code/personal/",
        ),
        GitIdentity(
            id="work",
            name="Work Dev",
            email="work@example.com",
            match_directory="~/code/work/",
        ),
    ]

    apply_conditional_git_includes(identities, dry_run=False)

    gitconfig = tmp_path / ".gitconfig"
    assert gitconfig.is_file()
    content = gitconfig.read_text(encoding="utf-8")
    assert 'includeIf "gitdir:~/code/personal/"' in content
    assert 'includeIf "gitdir:~/code/work/"' in content

    personal_profile = tmp_path / ".gitconfig-personal"
    assert personal_profile.is_file()
    assert "personal@example.com" in personal_profile.read_text(encoding="utf-8")


def _identity() -> GitIdentity:
    return GitIdentity(id="personal", name="Dev", email="dev@example.com", match_directory="~/code/personal/")


@pytest.mark.ci
def test_conditional_includes_dry_run_writes_nothing(tmp_path: Path, monkeypatch: pytest.MonkeyPatch):
    monkeypatch.setattr("pathlib.Path.home", lambda: tmp_path)

    apply_conditional_git_includes([_identity()], dry_run=True)

    assert list(tmp_path.iterdir()) == []


@pytest.mark.ci
def test_git_setup_dry_run_saves_no_config_and_generates_no_key(tmp_path: Path, monkeypatch: pytest.MonkeyPatch):
    """Full-mode dry run walks every prompt but writes nothing: no config, no ~/.gitconfig, no ssh-keygen."""
    monkeypatch.setattr("pathlib.Path.home", lambda: tmp_path)
    monkeypatch.setattr("rig.devenv.steps.git.get_git_config_val", lambda key: "Dev" if key == "user.name" else "d@x")
    monkeypatch.setattr("rig.devenv.steps.git.load_user_config", DevenvConfig)
    monkeypatch.setattr("rig.devenv.steps.git.Confirm.ask", lambda *_a, **_k: True)
    monkeypatch.setattr("rig.devenv.steps.git.Prompt.ask", lambda *_a, **kw: kw.get("default") or "x")

    def no_save(_cfg: DevenvConfig) -> None:
        raise AssertionError("dry run saved the devenv config")

    def no_subprocess(cmd: list[str], **_kwargs: object) -> None:
        raise AssertionError(f"dry run ran {cmd}")

    monkeypatch.setattr("rig.devenv.steps.git.save_user_config", no_save)
    monkeypatch.setattr("rig.devenv.steps.git.subprocess.run", no_subprocess)

    run_git_setup(StepContext(mode="full", dry_run=True))

    assert list(tmp_path.iterdir()) == []
