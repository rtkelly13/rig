"""Write what `estate wrapup` saw to each worktree's `temp/.rig/wrapup.json`: asks sent, verdicts read."""

from __future__ import annotations

import datetime as dt
import hashlib
from dataclasses import replace
from pathlib import Path

from rig.estate.wrapup import Row, Status, describe_facts
from rig.estate.wrapup_state import AnswerRecord, AskRecord, StateStore, WrapupState, default_store, timestamp


def _template_sha(template: str) -> str:
    return hashlib.sha256(template.encode("utf-8")).hexdigest()[:12]


def ask_for(row: Row, *, at: str, template: str | None) -> AskRecord:
    """The record of sending the prompt to *row* at *at*, under the id the prompt carried."""
    head = row.facts.head if row.facts is not None else ""
    sha = _template_sha(template) if template is not None else ""
    return AskRecord(at, row.pane, row.session, head, describe_facts(row.facts, row.pr), sha, id=row.ask_id)


def _new_answer(row: Row) -> AnswerRecord | None:
    """The verdict on *row* when the state file does not have it yet."""
    if row.status is not Status.ANSWERED or row.verdict is None:
        return None
    verdict = "done" if row.verdict.done else "pending"
    asked_at = row.asked.at if row.asked is not None else None
    last = row.recorded
    if last is not None and asked_at is not None and last.asked_at == asked_at:
        return None  # already recorded for this ask: by `report`, or by an earlier run
    if last is not None and (last.asked_at, last.verdict, last.detail) == (asked_at, verdict, row.verdict.detail):
        return None
    # A reply read later says nothing of the commit it was written at. The ask's commit is the
    # conservative stand-in: if HEAD moved since, the inbox shows the verdict as older than the PR.
    if row.asked is not None and row.asked.head:
        head = row.asked.head
    else:
        head = row.facts.head if row.facts is not None else ""
    return AnswerRecord(at="", pane=row.pane, verdict=verdict, detail=row.verdict.detail, head=head, asked_at=asked_at)


def _answered_since(state: WrapupState, answer: AnswerRecord) -> bool:
    """Whether the agent reported on the same ask after this run read the file: its own word wins."""
    return answer.asked_at is not None and any(
        a.asked_at == answer.asked_at and a.pane == answer.pane for a in state.answers
    )


def record(
    rows: list[Row], *, now: dt.datetime, template: str | None = None, store: StateStore | None = None
) -> list[Row]:
    """Write new verdicts, and asks sent this run, to each worktree's state file.

    Answers are recorded on every run, so they outlive the owner's next prompt. A worktree whose
    state cannot be written gets a note saying why; nothing else changes.
    """
    target = store or default_store()
    at = timestamp(now)
    out = list(rows)
    by_path: dict[str, list[int]] = {}
    for i, row in enumerate(out):
        by_path.setdefault(row.path, []).append(i)
    for path, indexes in by_path.items():
        additions: list[AskRecord | AnswerRecord] = []
        for i in indexes:
            row = out[i]
            if row.status is Status.SENT:
                additions.append(ask_for(row, at=at, template=template))
            elif (answer := _new_answer(row)) is not None:
                additions.append(replace(answer, at=at))
        if not additions:
            continue
        first = out[indexes[0]]
        state = target.read(Path(path)) or WrapupState(worktree_id=first.worktree_id, branch=first.branch)
        for item in additions:
            if isinstance(item, AskRecord):
                state = state.with_ask(item)
            elif not _answered_since(state, item):
                state = state.with_answer(item)
        failure = target.write(Path(path), state)
        if failure is not None:
            for i in indexes:
                note = "; ".join(x for x in (out[i].note, f"state not recorded: {failure}") if x)
                out[i] = replace(out[i], note=note)
    return out
