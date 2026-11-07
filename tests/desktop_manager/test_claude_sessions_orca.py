"""Sessions in repos set up for Orca are left for Orca to resume."""

from __future__ import annotations

import json
from datetime import UTC, datetime, timedelta
from pathlib import Path

import pytest

from desktop_manager.claude_sessions.discovery import discover
from desktop_manager.claude_sessions.orca import OrcaFolders, default_worktree_root, load_orca_folders

pytestmark = pytest.mark.ci

NOW = datetime.now(UTC)


def _write_config(path: Path, orca: object) -> Path:
    path.write_text(json.dumps({"tools": {"orca": orca}, "version": 1}), encoding="utf-8")
    return path


def _transcript(home: Path, session_id: str, cwd: Path) -> None:
    entry = {
        "type": "user",
        "entrypoint": "cli",
        "cwd": str(cwd),
        "timestamp": NOW.isoformat().replace("+00:00", "Z"),
        "message": {"role": "user", "content": "hi"},
    }
    path = home / "projects" / "C--code" / f"{session_id}.jsonl"
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(entry) + "\n", encoding="utf-8")


class TestLoadOrcaFolders:
    def test_relative_keys_resolve_against_the_code_root(self, tmp_path: Path) -> None:
        config = _write_config(tmp_path / "config.json", {"repos": {"personal/a": {}}})

        assert load_orca_folders(config, code_root=tmp_path).roots == (
            tmp_path / "personal" / "a",
            default_worktree_root(),
        )

    def test_keys_that_are_not_relative_are_ignored(self, tmp_path: Path) -> None:
        repos = {"C:/code/a": {}, "/abs": {}, "../out": {}, "": {}, "personal/b": {}}
        config = _write_config(tmp_path / "config.json", {"repos": repos})

        assert load_orca_folders(config, code_root=tmp_path).roots == (
            tmp_path / "personal" / "b",
            default_worktree_root(),
        )

    @pytest.mark.parametrize(
        "orca", [None, {}, {"repos": {}}, {"repos": ["personal/a"]}, {"repos": {"C:/code/a": {}}}, "orca"]
    )
    def test_no_listed_repos_means_orca_owns_nothing(self, tmp_path: Path, orca: object) -> None:
        assert load_orca_folders(_write_config(tmp_path / "config.json", orca), code_root=tmp_path).roots == ()

    def test_missing_or_broken_config_owns_nothing(self, tmp_path: Path) -> None:
        broken = tmp_path / "broken.json"
        broken.write_text("{", encoding="utf-8")

        assert load_orca_folders(tmp_path / "missing.json").roots == ()
        assert load_orca_folders(broken).roots == ()

    def test_reads_workspace_home_and_code_root(self, tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
        monkeypatch.setenv("WORKSPACE_HOME", str(tmp_path))
        monkeypatch.setenv("CODE_ROOT", str(tmp_path / "code"))
        _write_config(tmp_path / "config.json", {"repos": {"personal/a": {}}})

        assert tmp_path / "code" / "personal" / "a" in load_orca_folders().roots


class TestOwner:
    def test_the_repo_and_folders_inside_it_are_owned(self, tmp_path: Path) -> None:
        folders = OrcaFolders(roots=(tmp_path / "repo",))

        assert folders.owner(str(tmp_path / "repo")) == tmp_path / "repo"
        assert folders.owner(str(tmp_path / "repo" / ".worktrees" / "x")) == tmp_path / "repo"

    def test_a_sibling_with_the_same_prefix_is_not_owned(self, tmp_path: Path) -> None:
        folders = OrcaFolders(roots=(tmp_path / "repo",))

        assert folders.owner(str(tmp_path / "repo-other")) is None
        assert folders.owner(None) is None


class TestDiscoverLeavesOrcaSessions:
    def test_sessions_in_orca_folders_are_skipped_with_a_reason(self, tmp_path: Path) -> None:
        home = tmp_path / "claude"
        orca_repo = tmp_path / "orca-repo"
        other = tmp_path / "other"
        for folder in (orca_repo, other):
            folder.mkdir()
        _transcript(home, "orca", orca_repo)
        _transcript(home, "mine", other)

        found = discover(
            since=NOW - timedelta(hours=1),
            is_alive=lambda *_: False,
            home=home,
            orca=OrcaFolders(roots=(orca_repo,)),
        )

        assert [r.session_id for r in found.candidates] == ["mine"]
        assert [(s.record.session_id, s.reason) for s in found.skipped] == [("orca", f"left to Orca ({orca_repo})")]
