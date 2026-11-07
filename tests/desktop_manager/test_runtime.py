"""`desktop-manager runtime`: copies of one commit, switched only once they pass their checks."""

from __future__ import annotations

import json
import os
import subprocess
from collections.abc import Sequence
from pathlib import Path

import pytest

from desktop_manager import runtime

pytestmark = pytest.mark.ci

_GIT = ["git", "-c", "user.name=t", "-c", "user.email=t@example.com", "-c", "core.autocrlf=false"]


def _commit(repo: Path, files: dict[str, str]) -> str:
    for name, text in files.items():
        path = repo / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding="utf-8")
    subprocess.run([*_GIT, "-C", str(repo), "add", "-A"], check=True)
    subprocess.run([*_GIT, "-C", str(repo), "commit", "-q", "-m", "c"], check=True)
    return subprocess.run(
        ["git", "-C", str(repo), "rev-parse", "HEAD"], capture_output=True, text=True, check=True
    ).stdout.strip()


@pytest.fixture
def repo(tmp_path: Path) -> Path:
    repo = tmp_path / "repo"
    repo.mkdir()
    subprocess.run([*_GIT, "-C", str(repo), "init", "-q"], check=True)
    return repo


class FakeTools:
    """Real git; `uv sync` and the copy's own commands are recorded and answered here."""

    def __init__(self, failing: str | None = None) -> None:
        self.calls: list[list[str]] = []
        self.failing = failing

    def __call__(self, command: Sequence[str], cwd: Path | None) -> runtime.Ran:
        command = list(command)
        if command[0] == "git":
            return runtime.run(command, cwd)
        self.calls.append(command)
        if self.failing and self.failing in " ".join(command):
            return runtime.Ran(1, "Traceback: boom")
        return runtime.Ran(0, "")


def test_install_copies_the_commit_not_the_working_tree(repo: Path, tmp_path: Path):
    sha = _commit(repo, {"app.txt": "committed\n"})
    (repo / "app.txt").write_text("uncommitted edit\n", encoding="utf-8")
    root = tmp_path / "runtime"
    tools = FakeTools()

    result = runtime.install(repo, "HEAD", root=root, fetch=False, runner=tools)

    copy = root / sha[: runtime.SHORT_SHA]
    assert result.changed and result.copy == copy
    assert (runtime.current_link(root) / "app.txt").read_text(encoding="utf-8") == "committed\n"
    assert json.loads((copy / runtime.INSTALLED_NAME).read_text(encoding="utf-8"))["sha"] == sha
    assert runtime.read_state(root) == runtime.State(current=copy.name, previous=None, repo=os.path.realpath(repo))
    assert ["uv", "sync", "--project", str(copy), "--frozen", "--no-dev", "--link-mode", "copy"] in tools.calls


def test_a_new_install_keeps_one_previous_copy_and_removes_older_ones(repo: Path, tmp_path: Path):
    root = tmp_path / "runtime"
    first = _commit(repo, {"app.txt": "1\n"})[: runtime.SHORT_SHA]
    runtime.install(repo, "HEAD", root=root, fetch=False, runner=FakeTools())
    second = _commit(repo, {"app.txt": "2\n"})[: runtime.SHORT_SHA]
    runtime.install(repo, "HEAD", root=root, fetch=False, runner=FakeTools())
    third = _commit(repo, {"app.txt": "3\n"})[: runtime.SHORT_SHA]

    result = runtime.install(repo, "HEAD", root=root, fetch=False, runner=FakeTools())

    assert result.previous == second
    assert result.removed == [first]
    assert sorted(p.name for p in root.iterdir() if p.name != runtime.LINK_NAME and p.is_dir()) == sorted(
        [second, third]
    )
    assert (runtime.current_link(root) / "app.txt").read_text(encoding="utf-8") == "3\n"


def test_a_copy_that_fails_its_checks_never_becomes_current(repo: Path, tmp_path: Path):
    root = tmp_path / "runtime"
    good = _commit(repo, {"app.txt": "good\n", "desktop-manager/workspace-plan.bundle.json": "{}"})
    runtime.install(repo, "HEAD", root=root, fetch=False, runner=FakeTools())
    bad = _commit(repo, {"app.txt": "bad\n"})

    with pytest.raises(runtime.InstallError, match="current is unchanged"):
        runtime.install(repo, "HEAD", root=root, fetch=False, runner=FakeTools(failing="chrome-tabs --help"))

    assert runtime.read_state(root).current == good[: runtime.SHORT_SHA]
    assert (runtime.current_link(root) / "app.txt").read_text(encoding="utf-8") == "good\n"
    assert not (root / bad[: runtime.SHORT_SHA]).exists()


def test_the_check_validates_the_copys_workspace_plan(repo: Path, tmp_path: Path):
    _commit(repo, {"desktop-manager/workspace-plan.bundle.json": "{}"})
    tools = FakeTools()

    runtime.install(repo, "HEAD", root=tmp_path / "runtime", fetch=False, runner=tools)

    assert any(call[1:3] == ["workspace", "validate-plan"] for call in tools.calls)


def test_installing_the_current_commit_again_changes_nothing(repo: Path, tmp_path: Path):
    root = tmp_path / "runtime"
    _commit(repo, {"app.txt": "1\n"})
    runtime.install(repo, "HEAD", root=root, fetch=False, runner=FakeTools())
    tools = FakeTools()

    result = runtime.install(repo, "HEAD", root=root, fetch=False, runner=tools)

    assert not result.changed
    assert tools.calls == []


def test_rollback_swaps_current_and_previous(repo: Path, tmp_path: Path):
    root = tmp_path / "runtime"
    first = _commit(repo, {"app.txt": "1\n"})[: runtime.SHORT_SHA]
    runtime.install(repo, "HEAD", root=root, fetch=False, runner=FakeTools())
    second = _commit(repo, {"app.txt": "2\n"})[: runtime.SHORT_SHA]
    runtime.install(repo, "HEAD", root=root, fetch=False, runner=FakeTools())

    state = runtime.rollback(root)

    assert (state.current, state.previous) == (first, second)
    assert (runtime.current_link(root) / "app.txt").read_text(encoding="utf-8") == "1\n"


def test_rollback_without_a_previous_copy_fails(tmp_path: Path):
    with pytest.raises(runtime.InstallError, match="no previous copy"):
        runtime.rollback(tmp_path)


def test_a_failed_switch_puts_the_old_link_back(repo: Path, tmp_path: Path, monkeypatch):
    root = tmp_path / "runtime"
    _commit(repo, {"app.txt": "1\n"})
    runtime.install(repo, "HEAD", root=root, fetch=False, runner=FakeTools())
    make_link = runtime._make_link

    def fail_once(link: Path, target: Path) -> None:
        monkeypatch.setattr(runtime, "_make_link", make_link)
        raise runtime.InstallError("mklink failed")

    monkeypatch.setattr(runtime, "_make_link", fail_once)

    with pytest.raises(runtime.InstallError, match="mklink failed"):
        runtime.point_current(root, tmp_path / "elsewhere")

    assert (runtime.current_link(root) / "app.txt").read_text(encoding="utf-8") == "1\n"


def test_prune_never_follows_a_link(repo: Path, tmp_path: Path):
    root = tmp_path / "runtime"
    _commit(repo, {"app.txt": "1\n"})
    result = runtime.install(repo, "HEAD", root=root, fetch=False, runner=FakeTools())
    runtime._make_link(root / "alias", result.copy)

    assert runtime.prune(root, {result.copy.name}) == []
    assert (result.copy / "app.txt").read_text(encoding="utf-8") == "1\n"


def test_a_command_that_hangs_fails_instead_of_blocking(monkeypatch):
    def hang(*_args, **kwargs):
        raise subprocess.TimeoutExpired("git", kwargs["timeout"])

    monkeypatch.setattr(runtime.subprocess, "run", hang)

    ran = runtime.run(["git", "fetch"])

    assert ran.code == runtime.TIMED_OUT
    assert "timed out" in ran.output


def test_running_copy_is_true_only_under_the_runtime_root(tmp_path: Path):
    root = tmp_path / "runtime"
    assert runtime.running_copy(str(root / "abc" / "src" / "desktop_manager" / "cli.py"), root)
    assert not runtime.running_copy(str(tmp_path / "repo" / "src" / "desktop_manager" / "cli.py"), root)


def test_workspace_logs_dir_honours_the_runner_override(monkeypatch, tmp_path: Path):
    from desktop_manager.plan import workspace_logs_dir

    monkeypatch.setenv("DESKTOP_MANAGER_LOG_DIR", str(tmp_path / "elsewhere"))

    assert workspace_logs_dir(tmp_path / "plan.json") == tmp_path / "elsewhere"
