from __future__ import annotations

from pathlib import Path

import platformdirs
import pytest

from rig import roots

pytestmark = pytest.mark.ci


def _code_root(tmp_path: Path) -> Path:
    root = tmp_path / "code"
    (root / "personal").mkdir(parents=True)
    (root / "AGENTS.md").write_text("# Code root\n", encoding="utf-8")
    return root


@pytest.fixture(autouse=True)
def no_code_root_env(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.delenv(roots.CODE_ROOT_ENV, raising=False)


def test_checkout_inside_a_worktree_resolves_the_code_root(tmp_path: Path) -> None:
    root = _code_root(tmp_path)
    # Every level between here and the root has an AGENTS.md of its own, as real repos do.
    rig = root / "personal" / "rig"
    worktree = rig / "temp" / "rig" / "porbeagle"
    start = worktree / "src" / "rig"
    start.mkdir(parents=True)
    for repo in (root / "personal", rig, worktree):
        (repo / "AGENTS.md").write_text("# Repo\n", encoding="utf-8")

    assert roots.code_root(start=start, home=tmp_path / "home") == root


def test_env_override_wins_over_the_marker(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    root = _code_root(tmp_path)
    elsewhere = tmp_path / "elsewhere"
    monkeypatch.setenv(roots.CODE_ROOT_ENV, str(elsewhere))

    assert roots.code_root(start=root / "personal", home=tmp_path / "home") == elsewhere


def test_agents_md_without_personal_is_not_the_marker(tmp_path: Path) -> None:
    repo = tmp_path / "repo"
    repo.mkdir()
    (repo / "AGENTS.md").write_text("# Repo\n", encoding="utf-8")

    assert roots.find_code_root(repo) is None


def test_no_marker_falls_back_to_home_code(tmp_path: Path) -> None:
    home = tmp_path / "home"
    (home / "code").mkdir(parents=True)
    start = tmp_path / "runtime" / "copy"
    start.mkdir(parents=True)

    assert roots.code_root(start=start, home=home) == home / "code"


def test_no_marker_and_no_home_code_still_names_home_code(tmp_path: Path) -> None:
    home = tmp_path / "home"
    home.mkdir()
    drive_code = Path(home.anchor) / "code"
    if drive_code.is_dir():
        pytest.skip(f"{drive_code} exists on this machine and is the drive fallback")

    assert roots.home_code_root(home) == home / "code"


def test_personal_folder_follows_the_code_root(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    root = _code_root(tmp_path)
    monkeypatch.setenv(roots.CODE_ROOT_ENV, str(root))

    assert roots.personal_dir() == root / "personal"


def test_managed_projects_config_is_this_checkouts() -> None:
    config = roots.managed_projects_config()

    assert config.is_file()
    assert config.as_posix().endswith("config/managed-projects.json")


def test_app_data_dir_honours_localappdata_on_every_platform(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setenv("LOCALAPPDATA", str(tmp_path))

    assert roots.app_data_dir() == tmp_path / "shared-utilities"


def test_app_data_dir_uses_platformdirs_without_an_author_folder(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.delenv("LOCALAPPDATA", raising=False)

    found = roots.app_data_dir()

    assert found == Path(platformdirs.user_data_dir("shared-utilities", appauthor=False))
    assert found.name == "shared-utilities"
    assert found.parent.name != "shared-utilities"


def test_runtime_base_dir_delegates_to_app_data_dir(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    from desktop_manager import runtime

    monkeypatch.setenv("LOCALAPPDATA", str(tmp_path))

    assert runtime.base_dir() == roots.app_data_dir()
