"""`estate wrapup report`: the asked agent records its own verdict, whenever it is ready.

The prompt names a command (`$report` in config/orca-wrapup.md) instead of asking for a reply in a set
format. The agent can check whatever it needs first (git status, the PR, CI), then run it from its
worktree. The answer goes straight into that worktree's `temp/.rig/wrapup.json`, which is what
`estate wrapup` and the triage inbox read. The `WRAPUP:` line in a reply stays as the fallback.

This module must not import `rig.estate.wrapup`: that module renders `$report` from here.
"""

from __future__ import annotations

import datetime as dt
import shlex
import sys
from dataclasses import dataclass
from pathlib import Path

from rig import proc
from rig.estate.wrapup_state import AnswerRecord, AskRecord, StateStore, WrapupState, default_store, timestamp


def rig_executable() -> str:
    """The `rig` beside the running interpreter, so the agent runs a rig that has `report`; else `rig` on PATH.

    The agent's shell is another repo's worktree, with no rig in its environment and no interactive
    shell functions, so a bare `rig` may not resolve there.
    """
    scripts = Path(sys.executable).parent
    found = scripts / ("rig.exe" if sys.platform == "win32" else "rig")
    return str(found) if found.is_file() else "rig"


def report_command(ask_id: str, executable: str | None = None) -> str:
    """The command line the prompt hands the agent, shell-quoted (the runtime copy's path has a space)."""
    parts = [executable or rig_executable(), "estate", "wrapup", "report"]
    if ask_id:
        parts += ["--ask", ask_id]
    return shlex.join(parts)


@dataclass(frozen=True, slots=True)
class Reported:
    """What `report` did: the answer it wrote, or why it wrote nothing."""

    answer: AnswerRecord | None
    error: str = ""


def worktree_root(cwd: Path) -> Path | None:
    top = proc.git_out("rev-parse", "--show-toplevel", cwd=cwd)
    return Path(top) if top else None


def _target_ask(state: WrapupState, ask_id: str) -> AskRecord | None:
    """The ask named by *ask_id*; without one, the newest ask not yet answered, else the newest ask."""
    if ask_id:
        return state.ask_by_id(ask_id)
    unanswered = next((a for a in reversed(state.asks) if state.answer_to(a) is None), None)
    return unanswered or (state.asks[-1] if state.asks else None)


def report(
    worktree: Path,
    *,
    ask_id: str,
    done: bool,
    pending: str,
    todo: tuple[str, ...],
    now: dt.datetime,
    head: str | None = None,
    store: StateStore | None = None,
) -> Reported:
    """Append the agent's verdict to *worktree*'s state file, linked to the ask it answers."""
    target = store or default_store()
    state = target.read(worktree)
    if state is None or not state.asks:
        return Reported(None, "no wrap-up ask is recorded in this worktree")
    ask = _target_ask(state, ask_id)
    if ask is None:
        return Reported(None, f"no ask {ask_id} is recorded in this worktree")
    detail = "" if done else (pending.strip() or "; ".join(todo))
    answer = AnswerRecord(
        at=timestamp(now),
        pane=ask.pane,
        verdict="done" if done else "pending",
        detail=detail,
        head=head if head is not None else proc.git_out("rev-parse", "HEAD", cwd=worktree),
        asked_at=ask.at,
        todo=todo,
        via="report",
    )
    failure = target.write(worktree, state.with_answer(answer))
    return Reported(None, failure) if failure is not None else Reported(answer)
