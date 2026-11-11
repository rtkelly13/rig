"""The decisions feed, read back for the web console and the jobs that point at it.

`rig estate decisions` (C#, `src/Rig.Core/Estate/Decisions.cs`) finds the worktree
questions only a human should answer. The daily `estate-decisions` job writes them to
`default_feed_path()` as a JSON array of `Decision` objects, for the web console and the
boot-time Claude session to read without scanning anything.
"""

from __future__ import annotations

import os
import pathlib
from dataclasses import dataclass

from rig.typed_json import try_decode_json

FEED_NAME = "decisions.json"


@dataclass(frozen=True, slots=True)
class Decision:
    """One thing on this machine a human has to decide about."""

    kind: str
    #: The clone's directory name.
    repo: str
    path: str
    reason: str
    size_bytes: int
    suggested_action: str
    #: One command that carries out the suggested action.
    command: str


def default_feed_path() -> pathlib.Path:
    """`%LOCALAPPDATA%\\shared-utilities\\jobs\\decisions.json`, beside the jobs coordinator's state."""
    base = os.environ.get("LOCALAPPDATA") or str(pathlib.Path.home() / "AppData" / "Local")
    return pathlib.Path(base) / "shared-utilities" / "jobs" / FEED_NAME


def read_feed(path: pathlib.Path) -> list[Decision] | None:
    """The feed at *path*; None when it is missing or unreadable."""
    try:
        text = path.read_text(encoding="utf-8")
    except OSError:
        return None
    return try_decode_json(list[Decision], text)
