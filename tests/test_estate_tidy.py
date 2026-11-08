"""`estate tidy --repo`: narrowing a run to named clones, and the spans a run leaves behind."""

from __future__ import annotations

import json
import pathlib

import pytest
import typer
from rich.console import Console
from typer.testing import CliRunner

from rig import proc, tracing
from rig.estate import busy, locate
from rig.estate.cli import app

_QUIET = Console(stderr=True, quiet=True)


@pytest.fixture(autouse=True)
def nothing_is_busy(monkeypatch: pytest.MonkeyPatch) -> None:
    """`tidy` snapshots process working directories with `lsof`; pin that to "none" so runs are deterministic."""
    monkeypatch.setattr(busy, "working_dirs", frozenset[pathlib.Path])


def _git(args: list[str], cwd: pathlib.Path) -> None:
    proc.git(*args, cwd=cwd, check=True)


def _clone(parent: pathlib.Path, name: str) -> pathlib.Path:
    """A repo with one commit, pushed to a bare origin beside it."""
    parent.mkdir(parents=True, exist_ok=True)
    origin = parent / f"{name}.git"
    origin.parent.mkdir(parents=True, exist_ok=True)
    _git(["init", "-q", "--bare", "-b", "main", str(origin)], origin.parent)
    root = parent / name
    root.mkdir(parents=True)
    _git(["init", "-q", "-b", "main"], root)
    _git(["config", "user.email", "t@example.com"], root)
    _git(["config", "user.name", "T"], root)
    _git(["remote", "add", "origin", str(origin)], root)
    (root / "README.md").write_text("hi\n", encoding="utf-8")
    _git(["add", "."], root)
    _git(["commit", "-qm", "first"], root)
    _git(["push", "-q", "-u", "origin", "main"], root)
    return root


@pytest.fixture
def estate(tmp_path: pathlib.Path) -> pathlib.Path:
    """A code root holding `alpha`, `beta` and `team/beta`, the last two sharing a folder name."""
    root = tmp_path / "code"
    for name in ("alpha", "beta", "team/beta"):
        _clone(root, name)
    return root


def _no_discovery(*_args: object, **_kwargs: object) -> list[pathlib.Path]:
    raise AssertionError("a path must not trigger an estate scan")


def test_a_path_selects_its_clone_without_scanning(estate: pathlib.Path, monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setattr(locate, "clones", _no_discovery)

    chosen = locate.selected_clones(estate, [str(estate / "alpha")], refresh=False, quiet=True, err=_QUIET)

    assert chosen == [(estate / "alpha").resolve()]


def test_a_worktree_path_selects_the_clone_it_belongs_to(estate: pathlib.Path, monkeypatch: pytest.MonkeyPatch) -> None:
    alpha = estate / "alpha"
    worktree = alpha / ".worktrees" / "feature"
    _git(["worktree", "add", "-q", "-b", "feature", str(worktree)], alpha)
    monkeypatch.setattr(locate, "clones", _no_discovery)

    chosen = locate.selected_clones(estate, [str(worktree)], refresh=False, quiet=True, err=_QUIET)

    assert chosen == [alpha.resolve()]


def test_a_name_matches_a_folder_name_or_a_path_under_the_root(
    estate: pathlib.Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    known = [estate / "alpha", estate / "beta", estate / "team" / "beta"]
    monkeypatch.setattr(locate, "clones", lambda *_a, **_k: known)
    monkeypatch.chdir(estate.parent)

    chosen = locate.selected_clones(estate, ["ALPHA", "team/beta", "alpha"], refresh=False, quiet=True, err=_QUIET)

    assert chosen == [estate / "alpha", estate / "team" / "beta"]


@pytest.mark.parametrize("name", ["beta", "gamma"])
def test_an_ambiguous_or_unknown_name_is_an_error(
    name: str, estate: pathlib.Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    known = [estate / "alpha", estate / "beta", estate / "team" / "beta"]
    monkeypatch.setattr(locate, "clones", lambda *_a, **_k: known)
    monkeypatch.chdir(estate.parent)

    with pytest.raises(typer.Exit):
        locate.selected_clones(estate, [name], refresh=False, quiet=True, err=_QUIET)


def _spans(trace_dir: pathlib.Path) -> list[dict[str, object]]:
    tracing.flush()
    spans: list[dict[str, object]] = []
    for path in trace_dir.rglob("*.ndjson"):
        for line in path.read_text(encoding="utf-8").splitlines():
            for resource in json.loads(line)["resourceSpans"]:
                for scope in resource["scopeSpans"]:
                    spans.extend(scope["spans"])
    return spans


def test_tidy_on_one_repo_touches_only_it_and_traces_the_clone(
    estate: pathlib.Path, monkeypatch: pytest.MonkeyPatch, isolate_trace_output: pathlib.Path
) -> None:
    alpha = estate / "alpha"
    monkeypatch.setattr(locate, "clones", _no_discovery)

    result = CliRunner().invoke(app, ["tidy", "--root", str(estate), "--repo", str(alpha)])

    assert result.exit_code == 0, result.output
    spans = _spans(isolate_trace_output)
    clone_spans = [s for s in spans if s["name"] == "estate.tidy.clone"]
    assert len(clone_spans) == 1
    assert {"key": "estate.clone", "value": {"stringValue": str(alpha.resolve())}} in clone_spans[0]["attributes"]  # type: ignore[operator]
    clone_id = clone_spans[0]["spanId"]
    inspect = next(s for s in spans if s["name"] == "estate.inspect_repo")
    assert inspect["parentSpanId"] == clone_id
    assert any(str(s["name"]).startswith("subprocess: git") for s in spans), "git calls must be traced too"


def test_a_checkout_with_a_separate_git_dir_selects_itself(tmp_path: pathlib.Path) -> None:
    checkout = tmp_path / "code" / "split"
    checkout.mkdir(parents=True)
    proc.git("init", "-q", "--separate-git-dir", str(tmp_path / "elsewhere.git"), cwd=checkout, check=True)

    chosen = locate.selected_clones(tmp_path / "code", [str(checkout)], refresh=False, quiet=True, err=_QUIET)

    assert chosen == [checkout.resolve()]


def test_a_worktree_whose_main_checkout_cannot_be_found_is_refused(tmp_path: pathlib.Path) -> None:
    """`worktree list` names the git dir, not the checkout, when the main has a separate git dir."""
    main = tmp_path / "code" / "split"
    main.mkdir(parents=True)
    proc.git("init", "-q", "-b", "main", "--separate-git-dir", str(tmp_path / "elsewhere.git"), cwd=main, check=True)
    proc.git(
        "-c",
        "user.email=t@example.com",
        "-c",
        "user.name=T",
        "commit",
        "-q",
        "--allow-empty",
        "-m",
        "x",
        cwd=main,
        check=True,
    )
    worktree = tmp_path / "code" / "wt"
    proc.git("worktree", "add", "-q", "-b", "wt", str(worktree), cwd=main, check=True)

    with pytest.raises(typer.Exit):
        locate.selected_clones(tmp_path / "code", [str(worktree)], refresh=False, quiet=True, err=_QUIET)


@pytest.mark.parametrize("scope", [[], ["--all", "--repo", "alpha"]], ids=["neither", "both"])
def test_tidy_needs_exactly_one_explicit_scope(
    scope: list[str], estate: pathlib.Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    monkeypatch.setattr(locate, "clones", _no_discovery)

    result = CliRunner().invoke(app, ["tidy", "--root", str(estate), *scope])

    assert result.exit_code == 2


def test_tidy_all_sweeps_every_clone(estate: pathlib.Path, monkeypatch: pytest.MonkeyPatch) -> None:
    swept: list[pathlib.Path] = []

    def every(root: pathlib.Path, **_kwargs: object) -> list[pathlib.Path]:
        swept.append(root)
        return [estate / "alpha"]

    monkeypatch.setattr(locate, "clones", every)

    result = CliRunner().invoke(app, ["tidy", "--root", str(estate), "--all"])

    assert result.exit_code == 0, result.output
    assert swept == [estate.resolve()]


def test_tidy_rejects_an_unknown_check(estate: pathlib.Path, monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setattr(locate, "clones", _no_discovery)

    result = CliRunner().invoke(app, ["tidy", "--root", str(estate), "--all", "--check", "worktree-dirty"])

    assert result.exit_code == 2, "worktree-dirty is reported, never acted on, so it is not a tidy check"


def test_tidy_check_leaves_other_findings_alone(estate: pathlib.Path) -> None:
    alpha = estate / "alpha"
    _git(["branch", "done"], alpha)  # level with main: a `branch-merged` finding tidy would delete

    skipped = CliRunner().invoke(
        app, ["tidy", "--root", str(estate), "--repo", str(alpha), "--check", "temp-stale", "--apply"]
    )

    assert skipped.exit_code == 0, skipped.output
    assert proc.git("rev-parse", "--verify", "refs/heads/done", cwd=alpha).ok, "only temp-stale was in scope"

    unscoped = CliRunner().invoke(app, ["tidy", "--root", str(estate), "--repo", str(alpha), "--apply"])

    assert unscoped.exit_code == 0, unscoped.output
    assert not proc.git("rev-parse", "--verify", "refs/heads/done", cwd=alpha).ok, "the control: unscoped, it goes"
