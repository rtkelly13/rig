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
from rig.typed_json import JsonValue
from tests.fakes.git import git


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
    git(["init", "-q", "--bare", "-b", "main", str(origin)], code.parent)
    root = code / name
    root.mkdir(parents=True)
    git(["init", "-q", "-b", "main"], root)
    for key, value in [("user.email", "t.user@example.com"), ("user.name", "T"), ("core.autocrlf", "false")]:
        git(["config", key, value], root)
    git(["remote", "add", "origin", str(origin)], root)
    (root / ".gitignore").write_text("node_modules/\n", encoding="utf-8")
    (root / "README.md").write_text("hi\n", encoding="utf-8")
    git(["add", "."], root)
    git(["commit", "-qm", "first"], root)
    git(["push", "-q", "-u", "origin", "main"], root)
    return root


class Estate:
    """`safe` has everything on origin; `risky` holds an unpushed branch and an uncommitted file."""

    def __init__(self, code: pathlib.Path) -> None:
        self.code = code
        self.safe = _clone(code, "safe")
        self.risky = _clone(code, "risky")
        git(["switch", "-q", "-c", "wip"], self.risky)
        (self.risky / "wip.txt").write_text("only here\n", encoding="utf-8")
        git(["add", "wip.txt"], self.risky)
        git(["commit", "-qm", "wip"], self.risky)
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
# tidy
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
    git(["switch", "-q", "main"], estate.risky)
    git(["stash", "push", "-q", "-m", "keep"], estate.risky)

    code, out = _run("tidy", "--root", str(estate.code), "--all", "--apply")

    assert code == 0
    assert "Tidy" in out
    assert "dry run" not in out
    assert git(["branch", "--list", "wip"], estate.risky) != ""


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
