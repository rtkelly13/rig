"""The `estate` commands end to end, against a small estate of real clones under `tmp_path`.

Discovery is stubbed (it caches under the home directory); everything else runs: git,
the risk and reclaim scans, and the tables and `--json` payloads the commands print.
"""

from __future__ import annotations

import datetime as dt
import json
import pathlib

import pytest
from rich.console import Console
from typer.testing import CliRunner

from rig.estate import apps as apps_mod
from rig.estate import busy, cli, discovery, prs
from rig.estate import downloads as downloads_mod
from rig.estate.reclaim import Reclaimable
from rig.typed_json import JsonValue
from tests.test_estate_shelve import _git


@pytest.fixture(autouse=True)
def nothing_is_busy(monkeypatch: pytest.MonkeyPatch) -> None:
    """`tidy` snapshots process working directories with `lsof`; pin that to "none" so runs are deterministic."""
    monkeypatch.setattr(busy, "working_dirs", frozenset[pathlib.Path])


pytestmark = pytest.mark.ci

runner = CliRunner()


def _clone(code: pathlib.Path, name: str) -> pathlib.Path:
    """A clone of a fresh bare origin, with main pushed and `node_modules/` ignored."""
    origin = code.parent / "origins" / f"{name}.git"
    origin.parent.mkdir(exist_ok=True)
    _git(["init", "-q", "--bare", "-b", "main", str(origin)], code.parent)
    root = code / name
    root.mkdir(parents=True)
    _git(["init", "-q", "-b", "main"], root)
    for key, value in [("user.email", "t.user@example.com"), ("user.name", "T"), ("core.autocrlf", "false")]:
        _git(["config", key, value], root)
    _git(["remote", "add", "origin", str(origin)], root)
    (root / ".gitignore").write_text("node_modules/\n", encoding="utf-8")
    (root / "README.md").write_text("hi\n", encoding="utf-8")
    _git(["add", "."], root)
    _git(["commit", "-qm", "first"], root)
    _git(["push", "-q", "-u", "origin", "main"], root)
    return root


def _node_modules(clone: pathlib.Path) -> pathlib.Path:
    modules = clone / "node_modules" / "left-pad"
    modules.mkdir(parents=True)
    (modules / "index.js").write_text("x" * 4096, encoding="utf-8")
    return modules.parent


class Estate:
    """`safe` has everything on origin; `risky` holds an unpushed branch and an uncommitted file."""

    def __init__(self, code: pathlib.Path) -> None:
        self.code = code
        self.safe = _clone(code, "safe")
        self.risky = _clone(code, "risky")
        _git(["switch", "-q", "-c", "wip"], self.risky)
        (self.risky / "wip.txt").write_text("only here\n", encoding="utf-8")
        _git(["add", "wip.txt"], self.risky)
        _git(["commit", "-qm", "wip"], self.risky)
        (self.risky / "README.md").write_text("edited\n", encoding="utf-8")
        self.clones = [self.safe, self.risky]


@pytest.fixture
def estate(tmp_path: pathlib.Path, monkeypatch: pytest.MonkeyPatch) -> Estate:
    built = Estate(tmp_path / "code")
    monkeypatch.setattr(
        discovery, "repos", lambda _root, **_kwargs: discovery.Discovered(paths=built.clones, from_cache=True)
    )
    monkeypatch.setattr(discovery, "cache_age", lambda _root, **_kwargs: dt.timedelta(hours=3))
    # Wide enough that no table cell wraps or truncates.
    monkeypatch.setattr(cli, "console", Console(width=240))
    return built


def _run(*args: str) -> tuple[int, str]:
    done = runner.invoke(cli.app, list(args))
    if done.exception and not isinstance(done.exception, SystemExit):
        raise done.exception
    return done.exit_code, done.stdout


def _json(text: str) -> JsonValue:
    loaded: JsonValue = json.loads(text)
    return loaded


# ---------------------------------------------------------------------------
# --min
# ---------------------------------------------------------------------------


@pytest.mark.parametrize(
    ("text", "expected"),
    [
        ("100MB", 100 * 1024**2),
        (" 2gb ", 2 * 1024**3),
        ("1.5KB", 1536),
        ("1TB", 1024**4),
        ("512B", 512),
        ("1024", 1024),
        ("0", 0),
        ("lots", 0),
        ("xMB", 0),
    ],
)
def test_sizes_parse_with_binary_units_and_fall_back_to_zero(text: str, expected: int) -> None:
    assert cli._parse_size(text) == expected


# ---------------------------------------------------------------------------
# audit and risk
# ---------------------------------------------------------------------------


def test_audit_json_inventories_every_clone(estate: Estate) -> None:
    code, out = _run("audit", "--json", "--branches", "--root", str(estate.code))

    assert code == 0
    payload = _json(out)
    assert isinstance(payload, dict)
    assert payload["root"] == str(estate.code.resolve())
    assert payload["duplicate_clones"] == []
    clones = payload["clones"]
    assert isinstance(clones, list)
    rows = {pathlib.Path(str(c["repo"])).name: c for c in clones if isinstance(c, dict)}
    assert {k: v for k, v in rows["risky"].items() if not k.startswith("reclaimable")} == {
        "repo": str(estate.risky),
        "local_only_commits": 1,
        "unpushed_branches": ["wip"],
        "dirty_files": 1,
        "stashes": 0,
        "has_remote": True,
    }
    assert (rows["risky"]["reclaimable_bytes"], rows["risky"]["reclaimable_by_category"]) == (0, {})
    assert (rows["safe"]["local_only_commits"], rows["safe"]["dirty_files"]) == (0, 0)


def test_audit_table_puts_the_riskiest_clone_first(estate: Estate) -> None:
    code, out = _run("audit", "--root", str(estate.code))

    assert code == 0
    assert out.index("risky") < out.index("safe")
    assert "1 commits exist on no remote" in out


def test_the_same_origin_cloned_twice_is_reported_as_a_duplicate(estate: Estate, tmp_path: pathlib.Path) -> None:
    twin = estate.code / "twin"
    _git(["clone", "-q", _git(["remote", "get-url", "origin"], estate.safe), str(twin)], tmp_path)
    estate.clones.append(twin)

    _, out = _run("audit", "--root", str(estate.code))

    assert "Duplicate clones" in out
    assert "safe (0 local-only), twin (0 local-only)" in out


def test_risk_json_lists_only_clones_holding_work_found_nowhere_else(estate: Estate) -> None:
    code, out = _run("risk", "--json", "--root", str(estate.code))

    assert code == 0
    assert _json(out) == [
        {
            "repo": str(estate.risky),
            "local_only_commits": 1,
            "unpushed_branches": ["wip"],
            "dirty_files": 1,
            "stashes": 0,
            "has_remote": True,
        }
    ]


def test_risk_names_the_push_that_saves_each_branch(estate: Estate) -> None:
    code, out = _run("risk", "--root", str(estate.code))

    assert code == 0
    assert f"cd {estate.risky} && git push -u origin wip" in out
    assert "1 uncommitted file(s)" in out
    assert "1 commits across 1 clones exist only here." in out


def test_risk_reports_a_clone_with_no_remote_and_its_stash(estate: Estate) -> None:
    _git(["remote", "remove", "origin"], estate.safe)
    (estate.safe / "README.md").write_text("stashed\n", encoding="utf-8")
    _git(["stash", "push", "-q", "-m", "test-stash"], estate.safe)
    estate.clones.remove(estate.risky)

    _, out = _run("risk", "--root", str(estate.code))

    assert "no remote at all" in out
    assert "1 stash entry" in out


def test_risk_with_everything_pushed_says_so(estate: Estate) -> None:
    estate.clones.remove(estate.risky)

    assert _run("risk", "--root", str(estate.code)) == (
        0,
        "✓ Nothing at risk — every commit is on a remote, every tree clean.\n",
    )


# ---------------------------------------------------------------------------
# reclaim and trim
# ---------------------------------------------------------------------------


def test_reclaim_dry_run_keeps_everything_and_notes_clones_at_risk(estate: Estate) -> None:
    modules = _node_modules(estate.risky)

    code, out = _run("reclaim", "--min", "0", "--root", str(estate.code))

    assert code == 0
    assert "would remove" in out
    assert "Would free" in out
    assert "risky — 1 local-only commit(s), 1 uncommitted file(s)" in out
    assert modules.exists()


def test_reclaim_apply_removes_regenerable_output(estate: Estate) -> None:
    modules = _node_modules(estate.safe)

    code, out = _run("reclaim", "--min", "0", "--apply", "--root", str(estate.code))

    assert code == 0
    assert "removed" in out
    assert "Freed" in out
    assert not modules.exists()


def _worktree_entry(clone: pathlib.Path, path: pathlib.Path) -> Reclaimable:
    return Reclaimable(path=path, category="temp", bytes_used=1, regenerate="", is_worktree=True)


@pytest.fixture
def temp_worktree(estate: Estate) -> pathlib.Path:
    path = estate.safe / "temp" / "wt"
    _git(["worktree", "add", "-q", "-b", "wt", str(path)], estate.safe)
    _git(["push", "-q", "-u", "origin", "wt"], path)
    return path


def test_a_dirty_worktree_is_refused_unless_forced(estate: Estate, temp_worktree: pathlib.Path) -> None:
    (temp_worktree / "README.md").write_text("edited\n", encoding="utf-8")
    entry = _worktree_entry(estate.safe, temp_worktree)

    assert cli._reclaim_one(estate.safe, entry, apply=True) == cli._ReclaimOutcome("dirty worktree", "yellow")
    assert cli._reclaim_one(estate.safe, entry, apply=False, force=True).status == "would remove"


def test_a_worktree_holding_the_only_copy_of_a_commit_is_refused(estate: Estate, temp_worktree: pathlib.Path) -> None:
    (temp_worktree / "new.txt").write_text("new\n", encoding="utf-8")
    _git(["add", "new.txt"], temp_worktree)
    _git(["commit", "-qm", "local"], temp_worktree)

    outcome = cli._reclaim_one(estate.safe, _worktree_entry(estate.safe, temp_worktree), apply=True)

    assert outcome == cli._ReclaimOutcome("only copy", "yellow")
    assert temp_worktree.exists()


def test_a_clean_pushed_worktree_is_removed_through_git(estate: Estate, temp_worktree: pathlib.Path) -> None:
    outcome = cli._reclaim_one(estate.safe, _worktree_entry(estate.safe, temp_worktree), apply=True)

    assert outcome == cli._ReclaimOutcome("removed", "bold green")
    assert not temp_worktree.exists()
    assert "temp/wt" not in _git(["worktree", "list"], estate.safe)


def test_an_entry_resolving_outside_the_clone_is_never_removed(estate: Estate, tmp_path: pathlib.Path) -> None:
    outside = tmp_path / "elsewhere"
    outside.mkdir()
    entry = Reclaimable(path=outside, category="node_modules", bytes_used=1, regenerate="")

    assert cli._reclaim_one(estate.safe, entry, apply=True) == cli._ReclaimOutcome("outside clone", "bold red")
    assert outside.exists()


def test_trim_reports_idle_worktree_output(estate: Estate, temp_worktree: pathlib.Path) -> None:
    _node_modules(temp_worktree)

    code, out = _run("trim", "--idle-days", "0", "--min", "0", "--root", str(estate.code))

    assert code == 0
    assert "Idle worktrees (dry run" in out
    assert "Would free" in out


# ---------------------------------------------------------------------------
# tidy, doctor, sync and clones
# ---------------------------------------------------------------------------


@pytest.mark.parametrize("args", [[], ["--all", "--repo", "safe"]])
def test_tidy_demands_exactly_one_scope(estate: Estate, args: list[str]) -> None:
    code, out = _run("tidy", "--root", str(estate.code), *args)

    assert code == 2
    assert out == ""


def test_tidy_on_a_tidy_clone_has_nothing_to_do(estate: Estate, monkeypatch: pytest.MonkeyPatch) -> None:
    asked: list[pathlib.Path] = []
    monkeypatch.setattr(prs, "merged_heads", lambda clone: asked.append(clone) or {})

    code, out = _run("tidy", "--root", str(estate.code), "--repo", str(estate.safe), "--merged")

    assert code == 0
    assert "Tidy (dry run" in out
    assert "✓ Nothing to tidy." in out
    assert asked == [estate.safe]


def test_tidy_all_refuses_to_touch_work_found_nowhere_else(estate: Estate) -> None:
    _git(["switch", "-q", "main"], estate.risky)
    _git(["stash", "push", "-q", "-m", "keep"], estate.risky)

    code, out = _run("tidy", "--root", str(estate.code), "--all", "--apply")

    assert code == 0
    assert "Tidy" in out
    assert "dry run" not in out
    assert _git(["branch", "--list", "wip"], estate.risky) != ""


def test_doctor_refuses_a_folder_that_is_not_a_checkout(tmp_path: pathlib.Path) -> None:
    assert _run("doctor", str(tmp_path)) == (2, "")


def test_doctor_json_reports_the_clones_exposure(estate: Estate, monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setattr(prs, "merged_heads", lambda _clone: {})

    code, out = _run("doctor", str(estate.risky), "--json", "--merged")

    assert code == 0
    payload = _json(out)
    assert isinstance(payload, dict)
    assert (payload["repo"], payload["local_only_commits"], payload["dirty_files"]) == (str(estate.risky), 1, 1)
    assert payload["unpushed_branches"] == ["wip"]
    assert isinstance(payload["findings"], list)


def test_doctor_on_a_clean_clone_says_so(estate: Estate) -> None:
    code, out = _run("doctor", str(estate.safe))

    assert code == 0
    assert "0 worktree(s) · 1 local branch(es) · 0 local-only commit(s) · 0B reclaimable" in out
    assert "✓ Clean." in out


def test_doctor_tabulates_findings_with_their_fix(estate: Estate) -> None:
    code, out = _run("doctor", str(estate.risky))

    assert code == 0
    assert "risky" in out
    assert "✓ Clean." not in out


def test_sync_fetches_every_clone_and_names_the_ones_that_fail(estate: Estate, tmp_path: pathlib.Path) -> None:
    _git(["remote", "set-url", "origin", str(tmp_path / "gone.git")], estate.risky)

    code, out = _run("sync", "--root", str(estate.code))

    assert code == 0
    assert "✓ Fetched 1 of 2 clones." in out
    assert "risky:" in out


def test_clones_lists_the_cached_scan_and_its_age(estate: Estate) -> None:
    code, out = _run("clones", "--root", str(estate.code))

    assert code == 0
    assert f"2 clones under {estate.code.resolve()}" in out
    assert "cached, scanned 3h ago" in out
    assert str(estate.safe) in out


# ---------------------------------------------------------------------------
# apps and downloads
# ---------------------------------------------------------------------------

LONG_AGO = dt.datetime(2020, 1, 1, tzinfo=dt.UTC)
RECENT = dt.datetime.now(dt.UTC)


@pytest.fixture
def machine(monkeypatch: pytest.MonkeyPatch, tmp_path: pathlib.Path) -> pathlib.Path:
    monkeypatch.setattr(cli, "console", Console(width=240))
    monkeypatch.setattr(cli.sys, "platform", "darwin")
    apps = [
        apps_mod.AppInfo("Old", tmp_path / "Old.app", 3 * 1024**3, "1.0", LONG_AGO, LONG_AGO),
        apps_mod.AppInfo("New", tmp_path / "New.app", 1024**3, "2.0", RECENT, RECENT),
    ]
    monkeypatch.setattr(apps_mod, "scan_applications", lambda **_kwargs: apps)
    downloads = [
        downloads_mod.DownloadItem(
            "setup.dmg",
            tmp_path / "setup.dmg",
            2 * 1024**3,
            downloads_mod.DownloadCategory.INSTALLER,
            False,
            tmp_path / "Drive" / "setup.dmg",
        ),
        downloads_mod.DownloadItem(
            "data.csv", tmp_path / "data.csv", 1024**3, downloads_mod.DownloadCategory.DATASET, False
        ),
    ]
    monkeypatch.setattr(downloads_mod, "scan_downloads", lambda **_kwargs: downloads)
    return tmp_path


def test_apps_json_marks_the_dormant_ones(machine: pathlib.Path) -> None:
    code, out = _run("apps", "--json")

    assert code == 0
    payload = _json(out)
    assert isinstance(payload, list)
    assert [(a["name"], a["version"], a["is_dormant"]) for a in payload if isinstance(a, dict)] == [
        ("Old", "1.0", True),
        ("New", "2.0", False),
    ]


def test_apps_table_totals_the_dormant_space(machine: pathlib.Path) -> None:
    code, out = _run("apps")

    assert code == 0
    assert "across 2 applications" in out
    assert "dormant" in out and "active" in out


def test_downloads_json_names_the_drive_copy(machine: pathlib.Path) -> None:
    code, out = _run("downloads", "--json")

    assert code == 0
    assert _json(out) == [
        {
            "name": "setup.dmg",
            "path": str(machine / "setup.dmg"),
            "bytes": 2 * 1024**3,
            "category": "installer",
            "is_dir": False,
            "cloud_duplicate": str(machine / "Drive" / "setup.dmg"),
        },
        {
            "name": "data.csv",
            "path": str(machine / "data.csv"),
            "bytes": 1024**3,
            "category": "dataset",
            "is_dir": False,
            "cloud_duplicate": None,
        },
    ]


def test_downloads_table_separates_backed_up_from_unique(machine: pathlib.Path) -> None:
    code, out = _run("downloads")

    assert code == 0
    assert "duplicate in Drive (setup.dmg)" in out
    assert "unique local file" in out
    assert "already backed up to Google Drive" in out
