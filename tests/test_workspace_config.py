from __future__ import annotations

import pathlib

import pytest

from rig.workspace_config import load_desktop_aliases, load_workspace_repo, workspace_config_path

pytestmark = pytest.mark.ci


def test_the_config_lives_in_workspace_home_when_it_is_set(
    tmp_path: pathlib.Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    monkeypatch.setenv("WORKSPACE_HOME", str(tmp_path))
    assert workspace_config_path() == tmp_path / "config.json"


def test_desktop_aliases_and_the_workspace_repo_read_from_tools(tmp_path: pathlib.Path) -> None:
    config = tmp_path / "config.json"
    _ = config.write_text(
        '{"tools": {"estate": {"workspace_repo": " workspace "},'
        ' "desktop": {"aliases": {"chrome-profile-work": "Profile 4", " ": "x"}}}}',
        encoding="utf-8",
    )
    assert load_desktop_aliases(config) == {"chrome-profile-work": "Profile 4"}
    assert load_workspace_repo(config) == "workspace"


@pytest.mark.parametrize("text", ["{}", '{"tools": {"desktop": {"aliases": ["a"]}, "estate": {"workspace_repo": 3}}}'])
def test_desktop_aliases_and_the_workspace_repo_default_to_unset(tmp_path: pathlib.Path, text: str) -> None:
    config = tmp_path / "config.json"
    _ = config.write_text(text, encoding="utf-8")
    assert load_desktop_aliases(config) == {}
    assert load_workspace_repo(config) is None
