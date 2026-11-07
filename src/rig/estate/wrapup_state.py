"""What `estate wrapup` remembers per worktree: `temp/.rig/wrapup.json`, schema `rig.wrapup/1`.

The location and its rules are rig's per-worktree state standard (`rig.worktree_state`,
docs/repo-governance/worktree-state.md). This module is the file's shape and how it is read and written;
`wrapup.py` decides what goes in it. Spec: docs/estate/wrapup.md#the-state-file.
"""

from __future__ import annotations

import datetime as dt
import secrets
from collections.abc import Callable
from dataclasses import dataclass, replace
from pathlib import Path
from typing import Protocol

from rig import worktree_state
from rig.typed_json import encode_json, try_decode_json

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

    def last_ask(self, pane: str) -> AskRecord | None:
        return next((a for a in reversed(self.asks) if a.pane == pane), None)

    def last_answer(self, pane: str) -> AnswerRecord | None:
        return next((a for a in reversed(self.answers) if a.pane == pane), None)

    def ask_by_id(self, ask_id: str) -> AskRecord | None:
        return next((a for a in reversed(self.asks) if ask_id and a.id == ask_id), None)

    def answer_to(self, ask: AskRecord) -> AnswerRecord | None:
        """The newest answer recorded for *ask*, from either source."""
        return next((a for a in reversed(self.answers) if a.asked_at == ask.at and a.pane == ask.pane), None)

    def with_ask(self, ask: AskRecord) -> WrapupState:
        return replace(self, asks=(*self.asks, ask))

    def with_answer(self, answer: AnswerRecord) -> WrapupState:
        return replace(self, answers=(*self.answers, answer))


class StateStore(Protocol):
    def read(self, worktree: Path) -> WrapupState | None: ...

    #: None when written, else why not.
    def write(self, worktree: Path, state: WrapupState) -> str | None: ...


type StateReader = Callable[[Path], WrapupState | None]


class FileStateStore:
    """`<worktree>/temp/.rig/wrapup.json`. Unreadable files and unknown schemas read as absent."""

    def read(self, worktree: Path) -> WrapupState | None:
        text = worktree_state.read_text(worktree, FEATURE)
        state = try_decode_json(WrapupState, text) if text else None
        return state if state is not None and state.schema == SCHEMA else None

    def write(self, worktree: Path, state: WrapupState) -> str | None:
        return worktree_state.write_text(worktree, FEATURE, encode_json(WrapupState, state) + "\n")


def default_store() -> StateStore:
    return FileStateStore()


def new_ask_id() -> str:
    """Eight hex digits: enough to tell one worktree's asks apart, short enough to type."""
    return secrets.token_hex(4)


def timestamp(when: dt.datetime) -> str:
    return when.astimezone(dt.UTC).isoformat(timespec="milliseconds")


def parse_timestamp(text: str) -> dt.datetime | None:
    try:
        return dt.datetime.fromisoformat(text)
    except ValueError:
        return None
