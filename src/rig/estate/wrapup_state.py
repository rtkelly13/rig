"""What `estate wrapup` remembers per worktree: `temp/.rig/wrapup.json`, schema `rig.wrapup/1`.

The location and its rules are rig's per-worktree state standard (`rig.worktree_state`,
docs/repo-governance/worktree-state.md). `estate wrapup` runs from C# (`src/Rig.Core/Estate/WrapupState.cs`) and
writes the file; this module is its shape as the triage inbox reads it. Spec: docs/estate/wrapup.md#the-state-file.
"""

from __future__ import annotations

import datetime as dt
from collections.abc import Callable
from dataclasses import dataclass
from pathlib import Path

from rig import worktree_state
from rig.typed_json import try_decode_json

FEATURE = "wrapup"
SCHEMA = "rig.wrapup/1"


@dataclass(frozen=True, slots=True)
class AskRecord:
    """One prompt rig delivered to one agent pane."""

    #: When the send was accepted, UTC ISO 8601.
    at: str
    #: Orca's `tabId:leafId` for the pane.
    pane: str
    session: str
    #: HEAD when asked, so a reader can tell whether the worktree moved on since.
    head: str
    #: The facts line rig sent with the prompt.
    facts: str
    #: First 12 hex digits of the template's SHA-256, so a wording change is visible.
    template_sha: str
    #: Short token sent in the prompt; `estate wrapup report --ask ID` names the ask it answers. Empty before ids.
    id: str = ""


@dataclass(frozen=True, slots=True)
class AnswerRecord:
    """One verdict: reported by the agent with `estate wrapup report`, or read back from its `WRAPUP:` line."""

    at: str
    pane: str
    #: "done" or "pending".
    verdict: str
    detail: str
    head: str
    #: The `at` of the ask it answers; None when rig has no record of asking (asked before this file existed).
    asked_at: str | None = None
    #: What the agent says is left for its owner, one item each; only `report` fills it.
    todo: tuple[str, ...] = ()
    #: "report" when the agent ran `estate wrapup report`, "reply" when rig parsed its last message.
    via: str = "reply"


@dataclass(frozen=True, slots=True)
class WrapupState:
    schema: str = SCHEMA
    worktree_id: str = ""
    branch: str = ""
    asks: tuple[AskRecord, ...] = ()
    answers: tuple[AnswerRecord, ...] = ()


type StateReader = Callable[[Path], WrapupState | None]


def read(worktree: Path) -> WrapupState | None:
    """`<worktree>/temp/.rig/wrapup.json`. Missing or unreadable files and unknown schemas read as absent."""
    text = worktree_state.read_text(worktree, FEATURE)
    state = try_decode_json(WrapupState, text) if text else None
    return state if state is not None and state.schema == SCHEMA else None


def parse_timestamp(text: str) -> dt.datetime | None:
    try:
        return dt.datetime.fromisoformat(text)
    except ValueError:
        return None
