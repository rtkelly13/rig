from __future__ import annotations

import pathlib

import pytest

from rig.workspace_config import load_desktop_aliases, load_skip_repos, load_workspace_repo, workspace_config_path

pytestmark = pytest.mark.ci


def test_the_config_lives_in_workspace_home_when_it_is_set(
    tmp_path: pathlib.Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    monkeypatch.setenv("WORKSPACE_HOME", str(tmp_path))
    assert workspace_config_path() == tmp_path / "config.json"


def test_load_skip_repos_reads_the_names_under_tools_estate(tmp_path: pathlib.Path) -> None:
    config = tmp_path / "config.json"
    _ = config.write_text(
        '{"tools": {"orca": {"repos": {}}, "estate": {"skip_repos": ["hearth", " blobflags ", ""]}}}',
        encoding="utf-8",
    )
    assert load_skip_repos(config) == frozenset({"hearth", "blobflags"})


@pytest.mark.parametrize(
    "text",
    [None, "not json", "{}", '{"tools": {}}', '{"tools": {"estate": {"skip_repos": "hearth"}}}'],
)
def test_load_skip_repos_is_empty_when_the_file_is_missing_or_lists_nothing(
    tmp_path: pathlib.Path, text: str | None
) -> None:
    config = tmp_path / "config.json"
    if text is not None:
        _ = config.write_text(text, encoding="utf-8")
    assert load_skip_repos(config) == frozenset()


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
