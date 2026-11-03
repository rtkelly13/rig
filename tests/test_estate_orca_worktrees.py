"""`estate orca-worktrees`: which Orca worktrees are finished, and removing only those."""

from __future__ import annotations

import json
import pathlib
from collections.abc import Iterator, Mapping
from dataclasses import dataclass, field

import pytest
from pydantic import TypeAdapter

from rig import proc
from rig.estate import orca_worktrees
from rig.estate.orca_runtime import OrcaRuntime
from rig.estate.orca_worktrees import Facts, Verdict, decide, leftover_matches
from rig.estate.prs import PullRequest

pytestmark = pytest.mark.ci

type Json = dict[str, object]
DAY_MS = 86_400_000.0
NOW_MS = 100 * DAY_MS


def _pr(state: str, number: int = 7) -> PullRequest:
    return PullRequest(number=number, state=state, head_ref="feature")


@pytest.mark.parametrize(
    ("facts", "verdict"),
    [
        (Facts(exists=True, is_worktree=True, is_current=True, pr=_pr("MERGED")), Verdict.CURRENT),
        (Facts(exists=True, is_worktree=True, busy_agents=1, pr=_pr("MERGED")), Verdict.BUSY),
        (Facts(exists=False, is_worktree=False), Verdict.MISSING),
        (Facts(exists=True, is_worktree=False, leftover_matches=True), Verdict.LEFTOVER),
        (Facts(exists=True, is_worktree=False, leftover_matches=False), Verdict.LEFTOVER_DIFFERS),
        (Facts(exists=True, is_worktree=True, dirty=True, pr=_pr("MERGED")), Verdict.DIRTY),
        (Facts(exists=True, is_worktree=True, pr=_pr("MERGED")), Verdict.MERGED),
        (Facts(exists=True, is_worktree=True, pr=_pr("MERGED"), beyond_merge=2), Verdict.AFTER_MERGE),
        (Facts(exists=True, is_worktree=True, pr=_pr("OPEN")), Verdict.OPEN_PR),
        (Facts(exists=True, is_worktree=True, pr=_pr("CLOSED")), Verdict.CLOSED_PR),
        (Facts(exists=True, is_worktree=True, in_default=False, idle_days=30), Verdict.UNMERGED),
        (Facts(exists=True, is_worktree=True, in_default=True, idle_days=0.5), Verdict.RECENT),
        (Facts(exists=True, is_worktree=True, in_default=True, idle_days=3), Verdict.LANDED),
    ],
)
def test_each_rule_gives_its_verdict(facts: Facts, verdict: Verdict) -> None:
    assert decide(facts, idle_days=2)[0] is verdict


def test_only_finished_verdicts_are_removable() -> None:
    removable = {Verdict.MERGED, Verdict.LANDED, Verdict.MISSING, Verdict.LEFTOVER}
    assert removable == orca_worktrees.REMOVABLE


# ---------------------------------------------------------------- real git


def _git(cwd: pathlib.Path, *args: str) -> str:
    return proc.git(*args, cwd=cwd, check=True).out.strip()


@pytest.fixture
def repo(tmp_path: pathlib.Path) -> pathlib.Path:
    """A clone of a bare origin, with `.gitignore` ignoring `.venv/`."""
    origin = tmp_path / "origin.git"
    _ = _git(tmp_path, "init", "-q", "--bare", "-b", "main", str(origin))
    root = tmp_path / "repo"
    root.mkdir()
    _ = _git(root, "init", "-q", "-b", "main")
    _ = _git(root, "config", "user.email", "t@example.com")
    _ = _git(root, "config", "user.name", "T")
    _ = _git(root, "config", "core.autocrlf", "false")
    _ = _git(root, "remote", "add", "origin", str(origin))
    (root / ".gitignore").write_text(".venv/\n", encoding="utf-8")
    (root / "README.md").write_text("hi\n", encoding="utf-8")
    (root / "src").mkdir()
    (root / "src" / "app.py").write_text("print(1)\n", encoding="utf-8")
    _ = _git(root, "add", ".")
    _ = _git(root, "commit", "-qm", "first")
    _ = _git(root, "push", "-q", "-u", "origin", "main")
    _ = _git(root, "remote", "set-head", "origin", "main")
    return root


def _worktree(repo: pathlib.Path, name: str, *, commit: bool = True) -> pathlib.Path:
    path = repo.parent / "orca" / name
    _ = _git(repo, "worktree", "add", "-q", "-b", name, str(path))
    if commit:
        (path / "src" / f"{name}.py").write_text("x = 1\n", encoding="utf-8")
        _ = _git(path, "add", ".")
        _ = _git(path, "commit", "-qm", name)
    return path


def _break(path: pathlib.Path) -> None:
    """What an interrupted delete leaves: no `.git`, no `.gitignore`, a locked `.venv` still there."""
    (path / ".git").unlink()
    (path / ".gitignore").unlink()
    (path / ".venv").mkdir()
    (path / ".venv" / "python.exe").write_text("binary", encoding="utf-8")


def test_a_leftover_folder_matching_its_branch_is_safe(repo: pathlib.Path) -> None:
    path = _worktree(repo, "feature")
    _break(path)
    (path / "README.md").unlink()

    assert leftover_matches(path, repo, "feature") == (True, "")


def test_a_leftover_folder_with_an_edit_or_a_new_file_is_kept(repo: pathlib.Path) -> None:
    path = _worktree(repo, "feature")
    _break(path)
    (path / "src" / "app.py").write_text("print(2)\n", encoding="utf-8")
    (path / "notes.md").write_text("mine\n", encoding="utf-8")

    matches, detail = leftover_matches(path, repo, "feature")

    assert not matches
    assert "2 file(s) differ" in detail


def test_a_leftover_folder_whose_branch_is_gone_is_kept(repo: pathlib.Path) -> None:
    path = _worktree(repo, "feature")
    _break(path)
    _ = _git(repo, "worktree", "prune")
    _ = _git(repo, "branch", "-D", "feature")

    matches, detail = leftover_matches(path, repo, "feature")

    assert not matches
    assert "only copy" in detail


# ---------------------------------------------------------------- through a fake Orca


@dataclass(frozen=True)
class Request:
    id: str
    method: str
    params: dict[str, str | None] | None


REQUEST = TypeAdapter(Request)


@dataclass
class FakeOrca:
    """`repo.list`, `worktree.list`, `worktree.ps` and `worktree.rm`, as Orca answers them."""

    repo: pathlib.Path
    worktrees: list[Json] = field(default_factory=list[Json])
    activity: list[Json] = field(default_factory=list[Json])
    removed: list[str] = field(default_factory=list[str])

    def runtime(self) -> OrcaRuntime:
        return OrcaRuntime("pipe", "token", self.exchange)

    def exchange(self, _endpoint: str, request: bytes) -> Iterator[bytes]:
        body = REQUEST.validate_json(request)
        method, params = body.method, body.params or {}
        if method == "repo.list":
            result: object = {"repos": [{"id": "r1", "path": self.repo.as_posix(), "displayName": "repo"}]}
        elif method == "worktree.list":
            result = {"worktrees": self.worktrees}
        elif method == "worktree.ps":
            result = {"worktrees": self.activity, "truncated": False}
        elif method == "worktree.rm":
            selector = str(params["worktree"]).removeprefix("id:")
            self.removed.append(selector)
            result = {"removed": True}
        else:
            raise AssertionError(method)
        yield (json.dumps({"id": body.id, "ok": True, "result": result}) + "\n").encode()

    def add(self, path: pathlib.Path, branch: str, *, idle_days: float = 10) -> str:
        worktree_id = f"r1::{path.as_posix()}"
        self.worktrees.append(
            {
                "id": worktree_id,
                "repoId": "r1",
                "path": path.as_posix(),
                "branch": f"refs/heads/{branch}" if branch else "",
                "isMainWorktree": False,
                "lastActivityAt": NOW_MS - idle_days * DAY_MS,
            }
        )
        return worktree_id


def _prs(heads: Mapping[str, PullRequest]) -> orca_worktrees.PrLookup:
    return lambda _repo: heads


def test_apply_removes_finished_worktrees_through_orca_and_keeps_the_rest(repo: pathlib.Path) -> None:
    fake = FakeOrca(repo)
    fake.worktrees.append({"id": "r1::main", "repoId": "r1", "path": repo.as_posix(), "isMainWorktree": True})
    merged_path = _worktree(repo, "merged")
    merged = fake.add(merged_path, "merged")
    fake.add(_worktree(repo, "open"), "open")
    dirty_path = _worktree(repo, "dirty")
    (dirty_path / "scratch.txt").write_text("unsaved\n", encoding="utf-8")
    fake.add(dirty_path, "dirty")
    busy = fake.add(_worktree(repo, "busy"), "busy")
    fake.activity.append({"worktreeId": busy, "liveTerminalCount": 1, "agents": [{"state": "working"}]})
    landed = fake.add(_worktree(repo, "landed", commit=False), "landed")
    fake.add(_worktree(repo, "fresh", commit=False), "fresh", idle_days=0.1)
    gone = fake.add(repo.parent / "orca" / "vanished", "vanished")
    heads = {
        "merged": PullRequest(1, "MERGED", "merged", _git(merged_path, "rev-parse", "HEAD")),
        "open": PullRequest(2, "OPEN", "open"),
        "busy": PullRequest(3, "MERGED", "busy"),
    }
    runtime = fake.runtime()

    rows = orca_worktrees.review(runtime, "personal", cwd=repo, now_ms=NOW_MS, prs=_prs(heads))
    verdicts = {pathlib.Path(r.path).name: r.verdict for r in rows}
    done = orca_worktrees.apply(runtime, rows)

    assert verdicts == {
        "merged": Verdict.MERGED,
        "open": Verdict.OPEN_PR,
        "dirty": Verdict.DIRTY,
        "busy": Verdict.BUSY,
        "landed": Verdict.LANDED,
        "fresh": Verdict.RECENT,
        "vanished": Verdict.MISSING,
    }
    assert sorted(fake.removed) == sorted([merged, landed, gone])
    assert {pathlib.Path(r.path).name for r in done if r.outcome == "removed"} == {"merged", "landed", "vanished"}


def test_the_worktree_the_command_runs_in_is_never_judged_finished(repo: pathlib.Path) -> None:
    fake = FakeOrca(repo)
    here = _worktree(repo, "here")
    fake.add(here, "here")

    rows = orca_worktrees.review(
        fake.runtime(),
        "personal",
        cwd=here / "src",
        now_ms=NOW_MS,
        prs=_prs({"here": PullRequest(1, "MERGED", "here")}),
    )

    assert [r.verdict for r in rows] == [Verdict.CURRENT]
