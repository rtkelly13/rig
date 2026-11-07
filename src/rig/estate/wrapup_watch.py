"""`estate wrapup --apply`: ask a few sessions at a time, and ask the next as each one finishes.

At most `max_in_flight` asks are outstanding. Between sends, each outstanding worktree's state file
and `orca worktree ps` are polled. An ask stops being outstanding when its session:

- **reports**: it ran `estate wrapup report`, so the state file holds an answer to this ask's id;
- **answers**: the wrap-up marker is its last prompt, and it went idle *after* the send. The time
  check matters: just after a send, Orca can show the new prompt beside the previous reply;
- **starts waiting** on its owner (a question or a permission prompt), which the owner must clear; or
- **runs out of time** (`wait`). It stays `sent`; a later run reads the answer.

Subtasks are asked first, and a parent is held while any of its subtasks is queued or outstanding, so
its prompt reports their fresh verdicts. Each send and answer is written to the worktree's
`temp/.rig/wrapup.json` when it happens, so an interrupted run loses nothing.
"""

from __future__ import annotations

import datetime as dt
import time
from collections.abc import Callable, Mapping
from dataclasses import dataclass, replace
from pathlib import Path

from rig.estate import wrapup_record
from rig.estate.wrapup import (
    MARKER,
    OrcaAgent,
    OrcaRunner,
    OrcaWorktree,
    Row,
    Status,
    Verdict,
    WrapupError,
    parse_verdict,
    read_worktrees,
    render_prompt,
    send_order,
    send_prompt,
)
from rig.estate.wrapup_state import StateStore, default_store, new_ask_id, timestamp

#: Seconds between polls of `orca worktree ps`.
POLL_SECONDS = 5.0

#: Told about each send, answer and release, for progress lines: the row and what happened.
type Progress = Callable[[Row, str], None]


def _utc_now() -> dt.datetime:
    return dt.datetime.now(dt.UTC)


@dataclass(frozen=True, slots=True)
class Clock:
    """Time, injectable so tests never sleep. `monotonic` is for deadlines, `now` for comparing with Orca."""

    monotonic: Callable[[], float] = time.monotonic
    sleep: Callable[[float], None] = time.sleep
    now: Callable[[], dt.datetime] = _utc_now


def default_clock() -> Clock:
    return Clock()


@dataclass(frozen=True, slots=True)
class _Flight:
    index: int
    sent_at: dt.datetime
    deadline: float


def _quiet(_row: Row, _message: str) -> None:
    return None


def _descendants(row: Row, rows: list[Row]) -> set[str]:
    children: dict[str, tuple[str, ...]] = {}
    for r in rows:
        children.setdefault(r.worktree_id, r.children)
    found: set[str] = set()
    stack = list(row.children)
    while stack:
        wid = stack.pop()
        if wid not in found:
            found.add(wid)
            stack.extend(children.get(wid, ()))
    return found


def _next(queue: list[int], flights: list[_Flight], rows: list[Row]) -> int | None:
    """The first queued ask none of whose subtasks is still queued or outstanding."""
    pending = {rows[i].worktree_id for i in queue} | {rows[f.index].worktree_id for f in flights}
    for i in queue:
        if not (_descendants(rows[i], rows) & pending):
            return i
    # Unreachable while subtask links form a tree; never stall on a cycle.
    return queue[0] if queue and not flights else None


def _reported(row: Row, store: StateStore) -> Row | None:
    """*row* answered, when the state file holds an answer to the ask this run sent it."""
    state = store.read(Path(row.path))
    ask = state.ask_by_id(row.ask_id) if state is not None else None
    answer = state.answer_to(ask) if state is not None and ask is not None else None
    if answer is None:
        return None
    verdict = Verdict(done=answer.verdict == "done", detail=answer.detail)
    return replace(row, status=Status.ANSWERED, verdict=verdict, recorded=answer, note="")


def _outcome(row: Row, agent: OrcaAgent | None, sent_at: dt.datetime) -> Row | None:
    """*row* updated when its session has finished with the ask, else None."""
    if agent is None or agent.state_started_at is None or agent.state_started_at / 1000 <= sent_at.timestamp():
        return None
    if agent.state == "waiting":
        ps = agent.program_status
        message = (ps.message if ps else None) or (f"waiting on {agent.tool_name}" if agent.tool_name else "")
        return replace(row, status=Status.WAITING, state=agent.state, note=message)
    if agent.state != "done" or not (agent.prompt or "").lstrip().startswith(MARKER):
        return None
    verdict = parse_verdict(agent.last_message)
    first = next((ln.strip() for ln in (agent.last_message or "").splitlines() if ln.strip()), "")
    note = "" if verdict is not None else f"replied without a WRAPUP line: {first[:160]}"
    return replace(row, status=Status.ANSWERED, state=agent.state, verdict=verdict, note=note)


@dataclass(frozen=True, slots=True)
class _Pane:
    worktree_id: str
    pane: str


def _agents(worktrees: tuple[OrcaWorktree, ...]) -> dict[_Pane, OrcaAgent]:
    return {_Pane(w.worktree_id, a.pane_key): a for w in worktrees for a in (w.agents or ()) if a is not None}


def ask_rolling(
    rows: list[Row],
    template: str,
    worktrees: Mapping[str, OrcaWorktree],
    *,
    max_in_flight: int,
    wait: dt.timedelta,
    progress: Progress = _quiet,
    run: OrcaRunner | None = None,
    clock: Clock | None = None,
    store: StateStore | None = None,
) -> list[Row]:
    """Ask every ASK row, `max_in_flight` at a time, recording each send and answer as it happens."""
    tick = clock or default_clock()
    files = store or default_store()
    out = list(rows)
    queue = send_order(out)
    flights: list[_Flight] = []
    width = max(max_in_flight, 1)

    def save(i: int, template_text: str | None) -> None:
        out[i] = wrapup_record.record([out[i]], now=tick.now(), template=template_text, store=files)[0]

    try:
        while queue or flights:
            while len(flights) < width and (i := _next(queue, flights, out)) is not None:
                queue.remove(i)
                row = replace(out[i], ask_id=new_ask_id())
                if row.terminal is None:
                    out[i] = replace(row, status=Status.SEND_FAILED, note="no terminal")
                    progress(out[i], "send failed: no terminal")
                    continue
                result = send_prompt(row.terminal, render_prompt(template, row, out, worktrees), run)
                if not result.ok:
                    out[i] = replace(row, status=Status.SEND_FAILED, note=result.detail)
                    progress(out[i], f"send failed: {result.detail}")
                    continue
                sent_at = tick.now()
                asked = wrapup_record.ask_for(row, at=timestamp(sent_at), template=template)
                out[i] = replace(row, status=Status.SENT, asked=asked)
                save(i, template)
                flights.append(_Flight(i, sent_at, tick.monotonic() + wait.total_seconds()))
                progress(out[i], "asked")
            if not flights:
                continue
            earliest = min(f.deadline for f in flights)
            tick.sleep(max(0.0, min(POLL_SECONDS, earliest - tick.monotonic())))
            try:
                agents = _agents(read_worktrees(run))
            except WrapupError:
                agents = {}
            for flight in list(flights):
                row = out[flight.index]
                # The agent's own report wins; it is already in the file, so nothing is saved for it.
                finished = _reported(row, files)
                if finished is None:
                    finished = _outcome(row, agents.get(_Pane(row.worktree_id, row.pane)), flight.sent_at)
                    if finished is not None and finished.status is Status.ANSWERED:
                        out[flight.index] = finished
                        save(flight.index, None)
                        finished = out[flight.index]
                if finished is not None:
                    out[flight.index] = finished
                    flights.remove(flight)
                    progress(out[flight.index], _said(out[flight.index]))
                elif tick.monotonic() >= flight.deadline:
                    out[flight.index] = replace(row, note=f"no answer within {_minutes(wait)}; the next run reads it")
                    flights.remove(flight)
                    progress(out[flight.index], f"no reply within {_minutes(wait)}, moving on")
    except KeyboardInterrupt:
        for i in queue:
            out[i] = replace(out[i], status=Status.DEFERRED, note="interrupted before it was asked")
    return out


def _minutes(wait: dt.timedelta) -> str:
    seconds = int(wait.total_seconds())
    return f"{seconds // 60}m" if seconds % 60 == 0 else f"{seconds}s"


def _said(row: Row) -> str:
    if row.status is Status.WAITING:
        return "waiting on you" + (f": {row.note}" if row.note else "")
    if row.verdict is None:
        return "answered, without a WRAPUP line"
    return "answered: done" if row.verdict.done else f"answered: pending: {row.verdict.detail}"
