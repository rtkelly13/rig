"""Tests for rig's per-worktree state folder, `temp/.rig/`, and the gitignore audit that enforces it."""

from __future__ import annotations

from pathlib import Path

import pytest

from rig import proc, worktree_state
from rig.repo_governance.gitignore_manager import WORKTREE_STATE_ENTRY, inspect_gitignore, required_entries
from rig.repo_governance.policy_config import load_policy_config
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


def test_outside_a_repo_nothing_is_written(tmp_path: Path) -> None:
    assert worktree_state.ignored(tmp_path) is Ignored.NOT_A_REPO
    assert worktree_state.write_text(tmp_path, "wrapup", "{}") == "not a git worktree"
    assert not (tmp_path / "temp").exists()
    gone = tmp_path / "removed-worktree"
    assert worktree_state.write_text(gone, "wrapup", "{}") == "not a git worktree"
    assert not gone.exists()


def test_an_unignored_folder_is_never_written(tmp_path: Path) -> None:
    repo = _repo(tmp_path / "r", None)
    assert worktree_state.write_text(repo, "wrapup", "{}") == "temp/.rig/wrapup.json is not gitignored here"
    assert not (repo / "temp").exists()


def test_a_re_included_state_file_is_never_written(tmp_path: Path) -> None:
    repo = _repo(tmp_path / "r", "temp/.rig/*\n!temp/.rig/wrapup.json\n")
    assert worktree_state.ignored(repo) is Ignored.YES
    assert worktree_state.ignored(repo, "wrapup.json") is Ignored.NO
    assert worktree_state.write_text(repo, "wrapup", "{}") == "temp/.rig/wrapup.json is not gitignored here"
    assert not (repo / "temp" / ".rig" / "wrapup.json").exists()


def test_written_state_reads_back_and_leaves_the_worktree_clean(tmp_path: Path) -> None:
    repo = _repo(tmp_path / "r", "temp/\n")
    _ = proc.git("add", ".gitignore", cwd=repo)
    _ = proc.git("-c", "user.name=t", "-c", "user.email=t@example.invalid", "commit", "-qm", "init", cwd=repo)
    assert worktree_state.read_text(repo, "wrapup") is None
    assert worktree_state.write_text(repo, "wrapup", '{"schema": "x"}') is None
    assert worktree_state.read_text(repo, "wrapup") == '{"schema": "x"}'
    assert [p.name for p in worktree_state.state_dir(repo).iterdir()] == ["wrapup.json"]
    assert proc.git_out("status", "--porcelain", cwd=repo) == ""


def test_audit_flags_a_rule_that_re_includes_the_state_folder(tmp_path: Path) -> None:
    config = load_policy_config()
    repo = _repo(tmp_path / "r", None)
    entries = required_entries(repo, config=config, languages=[])
    lines = "\n".join(line for e in entries for line in e.write)
    _ = (repo / ".gitignore").write_text(lines + "\n", encoding="utf-8")
    assert inspect_gitignore(repo, config=config, languages=[]).status == "CONFIGURED"

    _ = (repo / ".gitignore").write_text(lines + "\n!temp/\n", encoding="utf-8")
    flagged = inspect_gitignore(repo, config=config, languages=[])
    assert not flagged.is_aligned
    assert flagged.missing == (WORKTREE_STATE_ENTRY,)
    assert flagged.status.startswith("NOT IGNORED temp/.rig/")
