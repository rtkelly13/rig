"""Tests for rig's per-worktree state folder, `temp/.rig/`. Its gitignore audit is tested in C# (GitignoresTests)."""

from __future__ import annotations

from pathlib import Path

import pytest

from rig import proc, worktree_state
from rig.worktree_state import Ignored

pytestmark = pytest.mark.ci


def _repo(path: Path, gitignore: str | None) -> Path:
    path.mkdir(parents=True, exist_ok=True)
    assert proc.git("init", "-q", cwd=path).ok
    if gitignore is not None:
        _ = (path / ".gitignore").write_text(gitignore, encoding="utf-8")
    return path


def test_the_location_is_temp_dot_rig(tmp_path: Path) -> None:
    assert worktree_state.state_file(tmp_path, "wrapup") == tmp_path / "temp" / ".rig" / "wrapup.json"


@pytest.mark.parametrize(
    ("gitignore", "expected"),
    [
        ("temp/\n", Ignored.YES),
        ("/temp\n", Ignored.YES),
        (None, Ignored.NO),
        ("temp/*\n!temp/.rig/\n", Ignored.NO),
        ("temp/\n!temp/\n", Ignored.NO),
    ],
)
def test_ignored_asks_git(tmp_path: Path, gitignore: str | None, expected: Ignored) -> None:
    assert worktree_state.ignored(_repo(tmp_path / "r", gitignore)) is expected


def test_outside_a_repo_is_not_a_repo(tmp_path: Path) -> None:
    assert worktree_state.ignored(tmp_path) is Ignored.NOT_A_REPO


def test_a_re_included_state_file_is_not_ignored(tmp_path: Path) -> None:
    repo = _repo(tmp_path / "r", "temp/.rig/*\n!temp/.rig/wrapup.json\n")
    assert worktree_state.ignored(repo) is Ignored.YES
    assert worktree_state.ignored(repo, "wrapup.json") is Ignored.NO


def test_state_reads_back_and_a_missing_file_reads_as_none(tmp_path: Path) -> None:
    assert worktree_state.read_text(tmp_path, "wrapup") is None
    target = worktree_state.state_file(tmp_path, "wrapup")
    target.parent.mkdir(parents=True)
    _ = target.write_text('{"schema": "x"}', encoding="utf-8")
    assert worktree_state.read_text(tmp_path, "wrapup") == '{"schema": "x"}'
