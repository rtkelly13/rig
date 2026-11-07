"""Read Claude Code sessions for the work list, through `claude_sessions.discovery`.

Unlike `discover()`, which looks for sessions worth resuming and so drops live ones, this
keeps every interactive session touched in the window and classifies it. Print-mode
sessions and sessions nobody typed into are dropped unless they are running now.
"""

from __future__ import annotations

import datetime as dt
from dataclasses import dataclass
from pathlib import Path

from desktop_manager.claude_sessions import discovery, folders
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
    is_alive: discovery.LivenessCheck = discovery.process_is_alive,
) -> SessionScan:
    root = home or discovery.claude_home()
    live = discovery.read_live_sessions(root / "sessions", is_alive)
    ends = discovery.read_session_ends(discovery.ledger_dir(root))
    out: list[SessionFacts] = []
    read = 0
    for path in discovery.iter_transcripts(root / "projects", modified_since=since):
        try:
            record = discovery.read_transcript(path)
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


def _cleared(record: discovery.SessionRecord, end: discovery.SessionEnd | None) -> bool:
    """The ledger says `/clear`, and the transcript was not used again after that."""
    if end is None or end.reason != "clear":
        return False
    return not (end.ended_at is not None and record.last_activity is not None and record.last_activity > end.ended_at)


def resolve_folder(cwd: str) -> Attribution | None:
    """Fallback attribution for a directory outside the estate, via `folders.folder_facts`."""
    facts = folders.folder_facts(cwd)
    if facts.git_root is None:
        return None
    return Attribution(repo_path=Path(facts.git_root), branch=facts.branch or "")
