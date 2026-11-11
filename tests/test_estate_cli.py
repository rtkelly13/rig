"""The `estate` commands still on Python, end to end: `--min` parsing, `apps` and `downloads`.

The clone commands (audit, risk, reclaim, trim, tidy, ...) run on the C# CLI and are tested in
tests/Rig.Cli.Tests/Estate/EstateCommandTests.cs.
"""

from __future__ import annotations

import datetime as dt
import json
import pathlib

import pytest
from rich.console import Console
from typer.testing import CliRunner

from rig.estate import apps as apps_mod
from rig.estate import cli
from rig.estate import downloads as downloads_mod
from rig.typed_json import JsonValue

pytestmark = pytest.mark.ci

runner = CliRunner()


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
