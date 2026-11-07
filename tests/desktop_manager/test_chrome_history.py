"""History lookups against a throwaway copy of Chrome's `urls` table."""

from __future__ import annotations

import sqlite3
from contextlib import closing
from pathlib import Path

import pytest

from desktop_manager.chrome.history import hosts_by_title

pytestmark = pytest.mark.ci


def _history(root: Path, rows: list[tuple[str, str]]) -> None:
    path = root / "Google" / "Chrome" / "User Data" / "Profile 4" / "History"
    path.parent.mkdir(parents=True)
    with closing(sqlite3.connect(path)) as db:
        db.execute("create table urls (id integer primary key, url text, title text)")
        db.executemany("insert into urls (url, title) values (?, ?)", rows)
        db.commit()


def test_a_title_seen_on_one_host_maps_to_it(tmp_path: Path, monkeypatch: pytest.MonkeyPatch):
    _history(
        tmp_path,
        [
            ("https://claude.ai/artifact/1", "Linking Triage"),
            ("https://claude.ai/artifact/1?v=2", "Linking Triage"),
            ("https://meet.google.com/abc", "Standup"),
            ("https://calendar.google.com/x", "Standup"),
        ],
    )
    monkeypatch.setenv("LOCALAPPDATA", str(tmp_path))
    assert hosts_by_title("Profile 4", ["Linking Triage", "Standup", "Never seen"]) == {"Linking Triage": "claude.ai"}


def test_a_missing_history_finds_nothing(tmp_path: Path, monkeypatch: pytest.MonkeyPatch):
    monkeypatch.setenv("LOCALAPPDATA", str(tmp_path))
    assert hosts_by_title("Profile 4", ["Linking Triage"]) == {}
