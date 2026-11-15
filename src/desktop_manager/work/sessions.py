"""Read Claude Code sessions for the work list, through `work.session_store`.

Unlike a resume scan, which drops sessions that are running now, this keeps every
interactive session touched in the window and classifies it. Print-mode sessions and
sessions nobody typed into are dropped unless they are running now.
"""

from __future__ import annotations

import datetime as dt
from dataclasses import dataclass
from pathlib import Path

from desktop_manager.work import session_store
from desktop_manager.work.join import classify_session
from desktop_manager.work.model import Attribution, SessionFacts

DEFAULT_WINDOW = dt.timedelta(days=30)


@dataclass(frozen=True, slots=True)
class SessionScan:
    sessions: tuple[SessionFacts, ...]
    transcripts_read: int


def read_sessions(
    *,
    since: dt.datetime,
    home: Path | None = None,
    is_alive: session_store.LivenessCheck = session_store.process_is_alive,
) -> SessionScan:
    root = home or session_store.claude_home()
    live = session_store.read_live_sessions(root / "sessions", is_alive)
    ends = session_store.read_session_ends(session_store.ledger_dir(root))
    out: list[SessionFacts] = []
    read = 0
    for path in session_store.iter_transcripts(root / "projects", modified_since=since):
        try:
            record = session_store.read_transcript(path)
        except OSError:
            continue
        read += 1
        running = live.get(record.session_id)
        if running is None and (not record.interactive or record.prompt_count == 0):
            continue
        cleared = _cleared(record, ends.get(record.session_id))
        state = classify_session(
            live_status=running.status if running else None,
            is_live=running is not None,
            cleared=cleared,
            exited_cleanly=record.exit_kind == "exited",
            turn_state=record.turn_state,
        )
        out.append(
            SessionFacts(
                session_id=record.session_id,
                title=record.label,
                state=state,
                last_activity=record.last_activity,
                last_cwd=record.last_cwd or (running.cwd if running else None),
                start_cwd=record.start_cwd,
                prompt_count=record.prompt_count,
                pid=running.pid if running else None,
            )
        )
    return SessionScan(sessions=tuple(out), transcripts_read=read)


def _cleared(record: session_store.SessionRecord, end: session_store.SessionEnd | None) -> bool:
    """The ledger says `/clear`, and the transcript was not used again after that."""
    if end is None or end.reason != "clear":
        return False
    return not (end.ended_at is not None and record.last_activity is not None and record.last_activity > end.ended_at)


def resolve_folder(cwd: str) -> Attribution | None:
    """Fallback attribution for a directory outside the estate, via `session_store.folder_facts`."""
    facts = session_store.folder_facts(cwd)
    if facts.git_root is None:
        return None
    return Attribution(repo_path=Path(facts.git_root), branch=facts.branch or "")
