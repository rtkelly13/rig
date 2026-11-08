"""Tests for the governance pre-push hook: main-push block, restart history guard and git-lfs handoff."""

from __future__ import annotations

import os
import pathlib
import shutil
import subprocess
import sys

import pytest

from rig.repo_governance.repo_hygiene import (
    PRE_PUSH_HOOK_CONTENT,
    align_git_hooks,
    inspect_git_hooks,
)

# The hook as align-repo-conventions wrote it before it handed pushes to git-lfs.
LEGACY_HOOK = """#!/usr/bin/env bash
# Enforced by align-repo-conventions
while read local_ref local_oid remote_ref remote_oid
do
	if [ "$remote_ref" = "refs/heads/main" ]; then
		echo "🛑 DIRECT PUSH TO MAIN BLOCKED by repository governance policy!"
		exit 1
	fi
done
exit 0
"""

FEATURE_PUSH = "refs/heads/feat abc123 refs/heads/feat 000000\n"
MAIN_PUSH = "refs/heads/main abc123 refs/heads/main 000000\n"
TWO_REF_PUSH = FEATURE_PUSH + "refs/heads/fix def456 refs/heads/fix 000000\n"

needs_bash = pytest.mark.skipif(
    sys.platform == "win32" or shutil.which("bash") is None,
    reason="runs the hook under bash with POSIX stub executables",
)


def _repo(tmp_path: pathlib.Path) -> pathlib.Path:
    (tmp_path / ".git" / "hooks").mkdir(parents=True)
    return tmp_path


def _hook(repo: pathlib.Path) -> pathlib.Path:
    return repo / ".git" / "hooks" / "pre-push"


@pytest.mark.ci
def test_align_writes_hook_that_hands_off_to_git_lfs(tmp_path: pathlib.Path):
    repo = _repo(tmp_path)

    result = align_git_hooks(repo)

    assert result.status == "aligned"
    content = _hook(repo).read_text(encoding="utf-8")
    assert "DIRECT PUSH TO MAIN BLOCKED" in content
    assert 'git lfs pre-push "$@"' in content
    assert inspect_git_hooks(repo).is_aligned


@pytest.mark.ci
def test_legacy_hook_is_outdated_and_align_upgrades_it(tmp_path: pathlib.Path):
    repo = _repo(tmp_path)
    _ = _hook(repo).write_text(LEGACY_HOOK, encoding="utf-8")

    inspected = inspect_git_hooks(repo)
    assert inspected.status == "OUTDATED"
    assert not inspected.is_aligned

    assert align_git_hooks(repo).status == "aligned"
    assert _hook(repo).read_text(encoding="utf-8") == PRE_PUSH_HOOK_CONTENT


@pytest.mark.ci
def test_current_hook_is_left_alone(tmp_path: pathlib.Path):
    repo = _repo(tmp_path)
    _ = align_git_hooks(repo)

    assert align_git_hooks(repo).status == "already_configured"
    assert inspect_git_hooks(repo).status == "ENFORCED"


def _stub_bin(tmp_path: pathlib.Path, bash: str, *, with_lfs: bool) -> pathlib.Path:
    """The whole PATH for the hook: a recording `git`, `cat`, and optionally a `git-lfs`.

    Nothing else is on PATH, so a real git-lfs on the machine (CI runners have one)
    cannot stand in for the stub.
    """
    bin_dir = tmp_path / "bin"
    bin_dir.mkdir()
    record = tmp_path / "git-call.txt"
    cat = shutil.which("cat") or "/bin/cat"
    stubs = {
        # `git config` is how the hook asks for a restart guard; this repo has none.
        "git": f'#!{bash}\n[ "$1" = config ] && exit 1\n{{ echo "args: $*"; "{cat}"; }} > "{record.as_posix()}"\n',
        "cat": f'#!{bash}\nexec "{cat}" "$@"\n',
    }
    if with_lfs:
        stubs["git-lfs"] = f"#!{bash}\nexit 0\n"
    for name, body in stubs.items():
        stub = bin_dir / name
        _ = stub.write_text(body, encoding="utf-8")
        stub.chmod(0o755)
    return bin_dir


def _run_hook(tmp_path: pathlib.Path, stdin: str, *, with_lfs: bool) -> subprocess.CompletedProcess[str]:
    repo = _repo(tmp_path / "repo")
    _ = align_git_hooks(repo)
    bash = shutil.which("bash") or "/bin/bash"
    bin_dir = _stub_bin(tmp_path, bash, with_lfs=with_lfs)
    env = {**os.environ, "PATH": str(bin_dir)}
    env.pop("ALLOW_MAIN_PUSH", None)
    return subprocess.run(  # noqa: S603 - fixed argv against a temp hook
        [bash, str(_hook(repo)), "origin", "git@example.com:o/r.git"],
        input=stdin,
        capture_output=True,
        text=True,
        env=env,
        check=False,
    )


@pytest.mark.ci
@needs_bash
def test_hook_forwards_refs_and_args_to_git_lfs(tmp_path: pathlib.Path):
    proc = _run_hook(tmp_path, FEATURE_PUSH, with_lfs=True)

    assert proc.returncode == 0, proc.stderr
    call = (tmp_path / "git-call.txt").read_text(encoding="utf-8")
    assert call == f"args: lfs pre-push origin git@example.com:o/r.git\n{FEATURE_PUSH}"


@pytest.mark.ci
@needs_bash
def test_hook_forwards_every_ref_of_a_multi_ref_push(tmp_path: pathlib.Path):
    proc = _run_hook(tmp_path, TWO_REF_PUSH, with_lfs=True)

    assert proc.returncode == 0, proc.stderr
    call = (tmp_path / "git-call.txt").read_text(encoding="utf-8")
    assert call == f"args: lfs pre-push origin git@example.com:o/r.git\n{TWO_REF_PUSH}"


@pytest.mark.ci
@needs_bash
def test_hook_blocks_main_before_git_lfs_runs(tmp_path: pathlib.Path):
    proc = _run_hook(tmp_path, MAIN_PUSH, with_lfs=True)

    assert proc.returncode == 1
    assert "DIRECT PUSH TO MAIN BLOCKED" in proc.stdout
    assert not (tmp_path / "git-call.txt").exists()


@pytest.mark.ci
@needs_bash
def test_hook_without_git_lfs_installed_still_allows_push(tmp_path: pathlib.Path):
    proc = _run_hook(tmp_path, FEATURE_PUSH, with_lfs=False)

    assert proc.returncode == 0, proc.stderr
    assert not (tmp_path / "git-call.txt").exists()


# -- the restart history guard, against a real repo ---------------------------------


def _real_git(repo: pathlib.Path, *args: str) -> str:
    done = subprocess.run(  # noqa: S603 - fixed argv in a temp repo
        ["git", "-c", "user.name=t", "-c", "user.email=t@example.com", "-c", "commit.gpgsign=false", *args],
        cwd=repo,
        capture_output=True,
        text=True,
        check=True,
    )
    return done.stdout.strip()


def _restarted_repo(tmp_path: pathlib.Path) -> tuple[pathlib.Path, str, str]:
    """A repo with an old history and an unrelated new root; returns (repo, old tip, new tip)."""
    repo = tmp_path / "repo"
    repo.mkdir()
    _ = _real_git(repo, "init", "-q", "-b", "old")
    _ = _real_git(repo, "commit", "-q", "--allow-empty", "-m", "old history")
    old = _real_git(repo, "rev-parse", "HEAD")
    _ = _real_git(repo, "checkout", "-q", "--orphan", "new")
    _ = _real_git(repo, "commit", "-q", "--allow-empty", "-m", "fresh start")
    root = _real_git(repo, "rev-parse", "HEAD")
    _ = _real_git(repo, "commit", "-q", "--allow-empty", "-m", "new work")
    new = _real_git(repo, "rev-parse", "HEAD")
    _ = _real_git(repo, "config", "rig.history.root", root)
    _ = _real_git(repo, "config", "rig.history.slug", "o/r")
    _ = align_git_hooks(repo)
    return repo, old, new


def _push_through_hook(
    tmp_path: pathlib.Path, repo: pathlib.Path, oid: str, url: str
) -> subprocess.CompletedProcess[str]:
    """Run the hook with the real git, except that `git lfs` is a recording stub."""
    bash = shutil.which("bash") or "/bin/bash"
    real_git = shutil.which("git") or "git"
    bin_dir = tmp_path / "bin"
    bin_dir.mkdir()
    stub = bin_dir / "git"
    _ = stub.write_text(
        f'#!{bash}\n[ "$1" = lfs ] && {{ echo lfs > "{(tmp_path / "lfs.txt").as_posix()}"; exit 0; }}\n'
        f'exec "{real_git}" "$@"\n',
        encoding="utf-8",
    )
    stub.chmod(0o755)
    env = {**os.environ, "PATH": f"{bin_dir}{os.pathsep}{os.environ.get('PATH', '')}"}
    env.pop("ALLOW_MAIN_PUSH", None)
    return subprocess.run(  # noqa: S603 - fixed argv against a temp hook
        [bash, str(_hook(repo)), "origin", url],
        input=f"refs/heads/feat {oid} refs/heads/feat {'0' * 40}\n",
        capture_output=True,
        text=True,
        cwd=repo,
        env=env,
        check=False,
    )


@pytest.mark.ci
@needs_bash
def test_hook_refuses_old_history_pushed_to_the_restarted_repo(tmp_path: pathlib.Path):
    repo, old, _ = _restarted_repo(tmp_path)

    proc = _push_through_hook(tmp_path, repo, old, "git@github.com:o/r.git")

    assert proc.returncode == 1
    assert "OLD HISTORY PUSH BLOCKED" in proc.stdout
    assert not (tmp_path / "lfs.txt").exists()


@pytest.mark.ci
@needs_bash
@pytest.mark.parametrize("url", ["git@github.com:o/r.git", "https://github.com/o/r"])
def test_hook_lets_new_history_through_to_the_restarted_repo(tmp_path: pathlib.Path, url: str):
    repo, _, new = _restarted_repo(tmp_path)

    proc = _push_through_hook(tmp_path, repo, new, url)

    assert proc.returncode == 0, proc.stdout + proc.stderr
    assert (tmp_path / "lfs.txt").exists()


@pytest.mark.ci
@needs_bash
def test_hook_lets_old_history_through_to_the_archive(tmp_path: pathlib.Path):
    repo, old, _ = _restarted_repo(tmp_path)

    proc = _push_through_hook(tmp_path, repo, old, "git@github.com:o/r-archive.git")

    assert proc.returncode == 0, proc.stdout + proc.stderr


@pytest.mark.ci
def test_hook_without_the_history_guard_is_outdated(tmp_path: pathlib.Path):
    repo = _repo(tmp_path)
    previous = PRE_PUSH_HOOK_CONTENT.replace("OLD HISTORY PUSH BLOCKED", "removed")
    _ = _hook(repo).write_text(previous, encoding="utf-8")

    inspected = inspect_git_hooks(repo)

    assert inspected.status == "OUTDATED"
    assert inspected.reason == "pre-push hook lacks the restart history guard"
