"""`estate shelve`, `estate unshelve` and `estate shelved` through the CLI, against a real clone.

The planning and acting rules are modelled in `features/estate_shelve.feature`; these tests
pin the command layer around them: option parsing, scope resolution, exit codes and the
`--json` payloads a script reads.
"""

from __future__ import annotations

import datetime as dt
import json
import pathlib

import pytest
import typer
from hypothesis import given
from hypothesis import strategies as st
from rich.console import Console
from typer.testing import CliRunner

from rig.estate import shelf_store, shelve, shelve_cli
from rig.estate.prs import PullRequest
from rig.typed_json import JsonValue
from tests import test_estate_shelve as shelve_tests
from tests.test_estate_shelve import _commit, _dirty, _git, _worktree

pytestmark = pytest.mark.ci

#: The real clone and bare origin the shelve tests build.
clone = shelve_tests.clone

runner = CliRunner()


@pytest.fixture(autouse=True)
def no_github(monkeypatch: pytest.MonkeyPatch) -> dict[str, PullRequest]:
    """No test reaches GitHub; a test that wants PRs fills the returned map."""
    heads: dict[str, PullRequest] = {}
    monkeypatch.setattr(shelve_cli, "pr_heads", lambda *_args, **_kwargs: heads)
    # Wide enough that no table cell wraps or truncates.
    monkeypatch.setattr(shelve_cli, "console", Console(width=240))
    return heads


def _run(*args: str) -> tuple[int, str]:
    done = runner.invoke(shelve_cli.app, list(args))
    if done.exception and not isinstance(done.exception, SystemExit):
        raise done.exception
    return done.exit_code, done.stdout


def _json_list(text: str) -> list[dict[str, JsonValue]]:
    loaded: list[dict[str, JsonValue]] = json.loads(text)
    return loaded


def _shelve(*args: str) -> tuple[int, list[dict[str, JsonValue]]]:
    code, out = _run("shelve", "--json", "--idle", "0", *args)
    return code, _json_list(out)


# ---------------------------------------------------------------------------
# --idle
# ---------------------------------------------------------------------------


@pytest.mark.parametrize(
    ("text", "expected"),
    [
        ("0", dt.timedelta(0)),
        ("", dt.timedelta(0)),
        ("30m", dt.timedelta(minutes=30)),
        ("2h", dt.timedelta(hours=2)),
        (" 2H ", dt.timedelta(hours=2)),
        ("1.5d", dt.timedelta(days=1.5)),
    ],
)
def test_idle_accepts_minutes_hours_and_days(text: str, expected: dt.timedelta) -> None:
    assert shelve_cli._parse_duration(text) == expected


@pytest.mark.parametrize("text", ["2", "h", "2w", "-1h", "1.2.3h", "two hours"])
def test_idle_rejects_anything_else(text: str) -> None:
    with pytest.raises(typer.BadParameter, match="expected e.g. 30m, 2h, 1d"):
        shelve_cli._parse_duration(text)


@given(amount=st.integers(0, 10_000), unit=st.sampled_from(["m", "h", "d"]))
def test_idle_round_trips_a_whole_amount_of_any_unit(amount: int, unit: str) -> None:
    seconds = {"m": 60, "h": 3600, "d": 86400}[unit]

    assert shelve_cli._parse_duration(f"{amount}{unit}").total_seconds() == amount * seconds


# ---------------------------------------------------------------------------
# estate shelve
# ---------------------------------------------------------------------------


def test_opposite_ignored_flags_are_refused(clone: pathlib.Path) -> None:
    code, out = _run("shelve", "--repo", str(clone), "--include-ignored", "--discard-ignored")

    assert code == 2
    assert out == ""


def test_a_clone_with_no_linked_worktrees_plans_nothing(clone: pathlib.Path) -> None:
    assert _shelve("--repo", str(clone)) == (0, [])


def test_dry_run_plans_every_linked_worktree_and_touches_nothing(clone: pathlib.Path) -> None:
    clean = _worktree(clone, "clean")
    ahead = _worktree(clone, "ahead")
    _commit(ahead, "local.txt", "only here\n")
    dirty = _worktree(clone, "dirty")
    _dirty(dirty)

    code, plans = _shelve("--repo", str(clone))

    assert code == 0
    assert [(p["name"], p["branch"], p["action"]) for p in plans] == [
        ("ahead", "ahead", shelve.PUSH),
        ("clean", "clean", shelve.REMOVE),
        ("dirty", "dirty", shelve.SNAPSHOT),
    ]
    assert [p["path"] for p in plans] == [str(ahead.resolve()), str(clean.resolve()), str(dirty.resolve())]
    assert all(p["clone"] == str(clone.resolve()) for p in plans)
    assert all(path.exists() for path in (clean, ahead, dirty))
    assert shelf_store.remote_shelves(clone) == {}


def test_apply_shelves_and_removes_each_worktree(clone: pathlib.Path) -> None:
    dirty = _worktree(clone, "dirty")
    _dirty(dirty)

    code, results = _shelve("--repo", str(clone), "--apply")

    assert code == 0
    assert [(r["name"], r["outcome"]) for r in results] == [("dirty", "shelved")]
    assert not dirty.exists()
    assert list(shelf_store.remote_shelves(clone)) == ["refs/heads/shelved/t.user/dirty"]
    assert shelf_store.remote_shelves(clone)["refs/heads/shelved/t.user/dirty"] == results[0]["sha"]


def test_a_worktree_needing_a_decision_exits_2_and_says_why(clone: pathlib.Path) -> None:
    secret = _worktree(clone, "secret")
    (secret / ".env").write_text("TOKEN=x\n", encoding="utf-8")

    code, plans = _shelve("--repo", str(clone))

    assert code == 2
    assert [p["name"] for p in plans] == ["secret"]
    assert plans[0]["keep_ignored"] == [".env"]
    assert plans[0]["decision_needed"] != []


def test_discard_ignored_resolves_that_decision(clone: pathlib.Path) -> None:
    secret = _worktree(clone, "secret")
    (secret / ".env").write_text("TOKEN=x\n", encoding="utf-8")

    code, plans = _shelve("--repo", str(clone), "--discard-ignored")

    assert code == 0
    assert plans[0]["decision_needed"] == []


def test_a_recently_touched_worktree_is_held_by_the_default_idle_guard(clone: pathlib.Path) -> None:
    _worktree(clone, "live")

    code, out = _run("shelve", "--json", "--repo", str(clone))

    assert code == 2
    assert _json_list(out)[0]["decision_needed"] != []


def test_a_known_pr_is_named_in_the_plan(clone: pathlib.Path, no_github: dict[str, PullRequest]) -> None:
    _worktree(clone, "feat")
    no_github["feat"] = PullRequest(number=42, state="OPEN", head_ref="feat")

    _, plans = _shelve("--repo", str(clone))

    assert (plans[0]["pr"], plans[0]["pr_state"]) == (42, "OPEN")


def test_paths_scope_to_the_worktree_holding_each_path(clone: pathlib.Path) -> None:
    one = _worktree(clone, "one")
    _worktree(clone, "two")
    (one / "sub").mkdir()

    code, plans = _shelve(str(one / "sub"))

    assert code == 0
    assert [p["name"] for p in plans] == ["one"]


def test_the_main_checkout_is_never_a_target(clone: pathlib.Path) -> None:
    code, out = _run("shelve", "--idle", "0", str(clone))

    assert code == 2
    assert out == ""


def test_a_path_outside_any_repository_is_refused(tmp_path: pathlib.Path) -> None:
    outside = tmp_path / "plain"
    outside.mkdir()

    code, _ = _run("shelve", str(outside))

    assert code == 2


def test_the_table_reports_each_worktree_and_tallies_the_plans(clone: pathlib.Path) -> None:
    _worktree(clone, "clean")
    _dirty(_worktree(clone, "dirty"))

    code, out = _run("shelve", "--idle", "0", "--repo", str(clone))

    assert code == 0
    assert "dry run" in out
    assert "1 remove" in out
    assert "1 snapshot+remove" in out


def test_the_applied_table_reports_outcomes(clone: pathlib.Path) -> None:
    _worktree(clone, "clean")

    code, out = _run("shelve", "--idle", "0", "--repo", str(clone), "--apply")

    assert code == 0
    assert "dry run" not in out
    assert "1 removed" in out


# ---------------------------------------------------------------------------
# estate shelved and estate unshelve
# ---------------------------------------------------------------------------


@pytest.fixture
def shelved_dirty(clone: pathlib.Path) -> pathlib.Path:
    """A dirty worktree, shelved and removed. Returns where it was."""
    dirty = _worktree(clone, "dirty")
    _dirty(dirty)
    code, _ = _shelve("--repo", str(clone), "--apply")
    assert code == 0
    return dirty


def test_nothing_shelved_says_so(clone: pathlib.Path) -> None:
    assert _run("shelved", "--repo", str(clone)) == (0, "✓ Nothing shelved.\n")
    assert _run("shelved", "--repo", str(clone), "--json") == (0, "[]\n")


def test_shelved_lists_each_shelf_with_its_trailers(clone: pathlib.Path, shelved_dirty: pathlib.Path) -> None:
    code, out = _run("shelved", "--repo", str(clone), "--json")

    assert code == 0
    [row] = _json_list(out)
    assert row["ref"] == "refs/heads/shelved/t.user/dirty"
    assert sorted(row) == ["at", "base", "branch", "by", "clone", "name", "path", "ref", "sha", "snapshot", "subject"]
    assert (row["name"], row["snapshot"]) == ("dirty", True)
    assert (row["branch"], row["path"], row["by"]) == ("dirty", ".worktrees/dirty", "estate shelve")
    assert row["sha"] == shelf_store.remote_shelves(clone)["refs/heads/shelved/t.user/dirty"]


def test_a_snapshot_is_never_pruned(clone: pathlib.Path, shelved_dirty: pathlib.Path) -> None:
    code, out = _run("shelved", "--repo", str(clone), "--prune", "--apply", "--json")

    assert code == 0
    [row] = _json_list(out)
    assert row["prune"] == ""
    assert "pruned" not in row
    assert list(shelf_store.remote_shelves(clone)) == ["refs/heads/shelved/t.user/dirty"]


def test_prune_drops_a_shelf_whose_base_landed_only_with_apply(clone: pathlib.Path) -> None:
    ahead = _worktree(clone, "ahead")
    _commit(ahead, "local.txt", "only here\n")
    assert _shelve("--repo", str(clone), "--apply")[0] == 0
    [shelf] = shelf_store.list_shelves(clone)
    _git(["push", "-q", "origin", f"{shelf.trailers.base}:main"], clone)
    _git(["fetch", "-q", "origin"], clone)

    _, dry = _run("shelved", "--repo", str(clone), "--prune", "--json")
    _, applied = _run("shelved", "--repo", str(clone), "--prune", "--apply", "--json")

    assert [(r["prune"], "pruned" in r) for r in _json_list(dry)] == [("base is in main", False)]
    assert [(r["prune"], r["pruned"]) for r in _json_list(applied)] == [("base is in main", True)]
    assert shelf_store.remote_shelves(clone) == {}


def test_the_shelved_table_marks_snapshots_and_prune_verdicts(clone: pathlib.Path, shelved_dirty: pathlib.Path) -> None:
    code, out = _run("shelved", "--repo", str(clone), "--prune")

    assert code == 0
    assert "prune dry run" in out
    assert "shelved/t.user/dirty" in out
    assert "✓" in out


def test_unshelve_restores_the_worktree_and_its_uncommitted_work(
    clone: pathlib.Path, shelved_dirty: pathlib.Path
) -> None:
    code, out = _run("unshelve", "dirty", "--repo", str(clone))

    assert code == 0
    assert out.startswith("✓ shelved/t.user/dirty → ")
    assert (shelved_dirty / "new.txt").read_text(encoding="utf-8") == "untracked\n"
    assert not (shelved_dirty / "gone.txt").exists()
    assert list(shelf_store.remote_shelves(clone)) == ["refs/heads/shelved/t.user/dirty"]


def test_unshelve_drop_deletes_the_shelf_once_restored(clone: pathlib.Path, shelved_dirty: pathlib.Path) -> None:
    code, out = _run("unshelve", "shelved/t.user/dirty", "--repo", str(clone), "--drop")

    assert code == 0
    assert "shelf dropped from origin" in out
    assert shelf_store.remote_shelves(clone) == {}


def test_unshelve_to_an_occupied_path_fails_and_keeps_the_shelf(
    clone: pathlib.Path, shelved_dirty: pathlib.Path
) -> None:
    shelved_dirty.mkdir(parents=True)

    code, out = _run("unshelve", "dirty", "--repo", str(clone), "--drop")

    assert code == 1
    assert out.startswith("✗ ")
    assert list(shelf_store.remote_shelves(clone)) == ["refs/heads/shelved/t.user/dirty"]


def test_unshelve_of_an_unknown_name_is_refused(clone: pathlib.Path, shelved_dirty: pathlib.Path) -> None:
    with pytest.raises(typer.BadParameter, match=r"'nope' matches 0 shelves in clone \(none\)"):
        shelve_cli._find_shelf(clone, "nope", None)


def test_a_bare_name_shared_by_two_users_is_ambiguous(clone: pathlib.Path, shelved_dirty: pathlib.Path) -> None:
    sha = shelf_store.remote_shelves(clone)["refs/heads/shelved/t.user/dirty"]
    _git(["push", "-q", "origin", f"{sha}:refs/heads/shelved/someone/dirty"], clone)

    with pytest.raises(typer.BadParameter, match="matches 2 shelves"):
        shelve_cli._find_shelf(clone, "dirty", "nobody")
    assert shelve_cli._find_shelf(clone, "dirty", "someone").ref == "refs/heads/shelved/someone/dirty"
