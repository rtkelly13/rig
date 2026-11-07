"""Where a tab's title came from, looked up in the profile's Chrome history.

UI Automation shows a tab's title but never its URL, and some sites (claude.ai
artifacts, for one) title their pages with nothing but the page's own name. The
profile's `History` database records the URL of every title it has seen, so a
title that history maps to exactly one host names that host.

Chrome keeps the database open; it is read in place, read-only and without
locking, so a lookup can miss the newest visits but never disturbs Chrome.
"""

from __future__ import annotations

import os
import sqlite3
from collections import defaultdict
from collections.abc import Iterable, Iterator
from contextlib import closing
from dataclasses import dataclass
from pathlib import Path
from urllib.parse import urlsplit

from pydantic import StrictStr

from rig.typed_json import decode, try_decode

_MAX_TITLES_PER_QUERY = 500


def history_path(profile_dir: str) -> Path | None:
    local = os.environ.get("LOCALAPPDATA")
    if not local:
        return None
    return Path(local) / "Google" / "Chrome" / "User Data" / profile_dir / "History"


@dataclass(frozen=True, slots=True)
class _HistoryRow:
    title: str
    url: str


def _history_rows(raw_rows: list[object]) -> Iterator[_HistoryRow]:
    """The rows with a string title and URL; a malformed row is skipped, not fatal."""
    for raw in raw_rows:
        match try_decode(list[StrictStr], raw):
            case [title, url]:
                yield _HistoryRow(title=title, url=url)
            case _:
                continue


def hosts_by_title(profile_dir: str, titles: Iterable[str]) -> dict[str, str]:
    """The host each title was visited on, for titles history ties to a single host."""
    path = history_path(profile_dir)
    wanted = sorted({title for title in titles if title})
    if path is None or not path.exists() or not wanted:
        return {}
    seen: dict[str, set[str]] = defaultdict(set)
    try:
        with closing(sqlite3.connect(f"file:{path.as_posix()}?mode=ro&immutable=1", uri=True)) as db:
            for start in range(0, len(wanted), _MAX_TITLES_PER_QUERY):
                chunk = wanted[start : start + _MAX_TITLES_PER_QUERY]
                marks = ", ".join("?" * len(chunk))
                rows = db.execute(
                    f"select title, url from urls where title in ({marks})",  # nosec B608 - only ? placeholders are interpolated
                    chunk,
                )
                for row in _history_rows(decode(list[object], rows.fetchall())):
                    host = urlsplit(row.url).hostname
                    if host:
                        seen[row.title].add(host)
    except sqlite3.Error:
        return {}
    return {title: next(iter(hosts)) for title, hosts in seen.items() if len(hosts) == 1}
