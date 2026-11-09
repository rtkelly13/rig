"""`estate orca-worktrees`: which Orca worktrees are finished, and removing only those."""

from __future__ import annotations

import pathlib
from collections.abc import Mapping

import pytest
import typer
from typer.testing import CliRunner

from rig import proc
from rig.estate import orca_worktrees, orca_worktrees_cli
from rig.estate.cli import app as estate_app
from rig.estate.orca_worktrees import Facts, PrAnswer, Row, Verdict, decide, leftover_matches
from rig.estate.prs import PullRequest
from tests.fakes.orca import FakeOrca

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
        (Facts(exists=True, is_worktree=True, foreign=True, pr=_pr("MERGED")), Verdict.FOREIGN),
        (Facts(exists=True, is_worktree=True, dirty=True, pr=_pr("MERGED")), Verdict.DIRTY),
        (Facts(exists=True, is_worktree=True, pr_known=False, in_default=True, idle_days=30), Verdict.PR_UNKNOWN),
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
    assert decide(facts, idle_days=2).verdict is verdict


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

    assert leftover_matches(path, repo, "feature").matches


def test_a_leftover_folder_with_an_edit_or_a_new_file_is_kept(repo: pathlib.Path) -> None:
    path = _worktree(repo, "feature")
    _break(path)
    (path / "src" / "app.py").write_text("print(2)\n", encoding="utf-8")
    (path / "notes.md").write_text("mine\n", encoding="utf-8")

    compared = leftover_matches(path, repo, "feature")

    assert not compared.matches
    assert "2 file(s) differ" in compared.detail


def test_a_leftover_folder_whose_branch_is_gone_is_kept(repo: pathlib.Path) -> None:
    path = _worktree(repo, "feature")
    _break(path)
    _ = _git(repo, "worktree", "prune")
    _ = _git(repo, "branch", "-D", "feature")

    compared = leftover_matches(path, repo, "feature")

    assert not compared.matches
    assert "only copy" in compared.detail


# ---------------------------------------------------------------- through a fake Orca


def _prs(heads: Mapping[str, PullRequest]) -> orca_worktrees.PrLookup:
    return lambda _repo, branch: PrAnswer(known=True, pr=heads.get(branch))


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


def _review(fake: FakeOrca, repo: pathlib.Path, heads: Mapping[str, PullRequest] | None = None) -> list[Row]:
    return orca_worktrees.review(fake.runtime(), "personal", cwd=repo, now_ms=NOW_MS, prs=_prs(heads or {}))


def test_a_leftover_edited_after_review_is_not_deleted(repo: pathlib.Path) -> None:
    fake = FakeOrca(repo)
    path = _worktree(repo, "feature")
    _break(path)
    fake.add(path, "feature")
    rows = _review(fake, repo)
    assert [r.verdict for r in rows] == [Verdict.LEFTOVER]

    (path / "notes.md").write_text("written after review\n", encoding="utf-8")
    done = orca_worktrees.apply(fake.runtime(), rows)

    assert done[0].outcome.startswith("skipped: changed since review")
    assert (path / "notes.md").exists()
    assert fake.removed == []


def test_a_worktree_dirtied_after_review_is_kept(repo: pathlib.Path) -> None:
    fake = FakeOrca(repo)
    path = _worktree(repo, "landed", commit=False)
    fake.add(path, "landed")
    rows = _review(fake, repo)
    assert [r.verdict for r in rows] == [Verdict.LANDED]

    (path / "scratch.txt").write_text("unsaved\n", encoding="utf-8")
    done = orca_worktrees.apply(fake.runtime(), rows)

    assert done[0].outcome == "skipped: changed since review: uncommitted or untracked files"
    assert fake.removed == []


def test_a_removal_orca_does_not_confirm_is_a_failure(repo: pathlib.Path) -> None:
    fake = FakeOrca(repo)
    stuck = fake.add(_worktree(repo, "landed", commit=False), "landed")
    fake.stuck.add(stuck)

    done = orca_worktrees.apply(fake.runtime(), _review(fake, repo))

    assert done[0].failed
    assert "still lists" in done[0].outcome


def test_apply_removes_only_the_worktree_it_reviewed(repo: pathlib.Path) -> None:
    fake = FakeOrca(repo)
    fake.add(_worktree(repo, "landed", commit=False), "landed")
    rows = _review(fake, repo)
    # Another entry takes the same path before --apply runs.
    fake.worktrees[0]["id"] = "r1::replacement"

    done = orca_worktrees.apply(fake.runtime(), rows)

    assert done[0].outcome == "skipped: Orca no longer lists the worktree reviewed"
    assert fake.removed == []


def test_a_worktree_of_another_repository_is_foreign(repo: pathlib.Path, tmp_path: pathlib.Path) -> None:
    other = tmp_path / "other"
    other.mkdir()
    _ = _git(other, "init", "-q", "-b", "main")
    _ = _git(other, "config", "user.email", "t@example.com")
    _ = _git(other, "config", "user.name", "T")
    (other / "a.txt").write_text("a\n", encoding="utf-8")
    _ = _git(other, "add", ".")
    _ = _git(other, "commit", "-qm", "a")
    path = tmp_path / "orca" / "stranger"
    _ = _git(other, "worktree", "add", "-q", "-b", "stranger", str(path))
    fake = FakeOrca(repo)
    fake.add(path, "stranger")

    rows = _review(fake, repo, {"stranger": PullRequest(1, "MERGED", "stranger")})

    assert [r.verdict for r in rows] == [Verdict.FOREIGN]


def test_a_failed_pr_lookup_keeps_the_worktree(repo: pathlib.Path) -> None:
    fake = FakeOrca(repo)
    fake.add(_worktree(repo, "landed", commit=False), "landed")

    rows = orca_worktrees.review(
        fake.runtime(), "personal", cwd=repo, now_ms=NOW_MS, prs=lambda _repo, _branch: PrAnswer(known=False)
    )

    assert [r.verdict for r in rows] == [Verdict.PR_UNKNOWN]


def _github_clone(tmp_path: pathlib.Path) -> pathlib.Path:
    root = tmp_path / "clone"
    root.mkdir()
    _ = _git(root, "init", "-q", "-b", "main")
    _ = _git(root, "remote", "add", "origin", "https://github.com/owner/name.git")
    return root


def test_branch_pr_asks_for_the_branch_and_ignores_fork_prs(tmp_path: pathlib.Path) -> None:
    asked: list[list[str]] = []
    pulls = (
        '[{"number": 9, "state": "OPEN", "headRefOid": "f", "isCrossRepository": true},'
        ' {"number": 4, "state": "MERGED", "headRefOid": "a", "isCrossRepository": false},'
        ' {"number": 6, "state": "CLOSED", "headRefOid": "b", "isCrossRepository": false}]'
    )

    def gh(args: list[str]) -> proc.Ran:
        asked.append(args)
        return proc.Ran(0, pulls, "")

    answer = orca_worktrees.branch_pr(_github_clone(tmp_path), "feature", run=gh)

    assert answer == PrAnswer(known=True, pr=PullRequest(6, "CLOSED", "feature", "b"))
    assert asked[0][:6] == ["pr", "list", "--repo", "owner/name", "--head", "feature"]


@pytest.mark.parametrize(("code", "out", "known"), [(0, "[]", True), (1, "", False), (0, "not json", False)])
def test_branch_pr_says_when_it_does_not_know(tmp_path: pathlib.Path, code: int, out: str, known: bool) -> None:
    answer = orca_worktrees.branch_pr(_github_clone(tmp_path), "feature", run=lambda _args: proc.Ran(code, out, ""))

    assert answer == PrAnswer(known=known)


def _row(outcome: str, verdict: Verdict = Verdict.LANDED) -> Row:
    return Row("personal", "repo", "/x", "b", verdict, "", outcome=outcome)


@pytest.mark.parametrize("as_json", [False, True])
def test_a_failed_removal_exits_1_in_either_output(as_json: bool) -> None:
    rows = [_row("removed"), _row("failed: nope")]

    with pytest.raises(typer.Exit) as raised:
        orca_worktrees_cli.report(rows, as_json=as_json, show_kept=False, applied=True)

    assert raised.value.exit_code == 1


def test_the_headline_counts_removed_failed_and_skipped_apart() -> None:
    rows = [_row("removed"), _row("failed: x"), _row("skipped: y"), _row("", Verdict.DIRTY)]

    headline = orca_worktrees_cli._headline(rows, applied=True)

    assert headline.startswith("1 of 4 Orca worktree(s) removed, 1 failed, 1 skipped")


# ---------------------------------------------------------------- scope


def test_select_names_a_project_by_folder_display_name_or_path(repo: pathlib.Path) -> None:
    fake = FakeOrca(repo)
    inside = _worktree(repo, "feature")
    fake.add(inside, "feature")
    runtime = fake.runtime()

    assert orca_worktrees.select(runtime, "REPO") == {"r1"}
    assert orca_worktrees.select(runtime, str(repo / "src")) == {"r1"}
    assert orca_worktrees.select(runtime, str(inside)) == {"r1"}
    assert orca_worktrees.select(runtime, "elsewhere") == frozenset()


def test_select_still_finds_a_worktree_whose_folder_is_gone(repo: pathlib.Path) -> None:
    fake = FakeOrca(repo)
    vanished = repo.parent / "orca" / "vanished"
    fake.add(vanished, "vanished")

    assert orca_worktrees.select(fake.runtime(), str(vanished)) == {"r1"}


def test_review_only_judges_the_chosen_projects(repo: pathlib.Path) -> None:
    fake = FakeOrca(repo)
    fake.add(_worktree(repo, "feature"), "feature")

    rows = orca_worktrees.review(
        fake.runtime(), "personal", cwd=repo, now_ms=NOW_MS, prs=_prs({}), only=frozenset({"other"})
    )

    assert rows == []


@pytest.mark.parametrize("args", [[], ["--all", "--repo", "rig"]])
def test_the_command_demands_exactly_one_scope(args: list[str]) -> None:
    result = CliRunner().invoke(estate_app, ["orca-worktrees", *args])

    assert result.exit_code == 2
    assert "Choose a scope" in result.output
