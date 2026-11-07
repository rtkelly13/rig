"""Read Claude Code's on-disk state to find sessions worth resuming.

Two sources, both under the Claude config directory:

- **Transcripts** (`projects/<project>/<session-id>.jsonl`). Subagent transcripts live one
  level deeper and are never candidates. A clean exit appends a `cost-state` entry after
  the last message; a crash, an OOM kill or a killed terminal leaves none. That trailing
  entry is the crash signal. How the last turn ended says whether the work finished:
  a reply in plain text means the session was waiting on the user; a tool call or a
  tool result with nothing after it means it died mid-task.

  Every entry records the working directory. The first is where the session started,
  which is where Claude files its transcript and where it must be resumed. Later
  entries can differ: a session often moves into a repo or a worktree to work. The
  last one says where the work was when it stopped.
- **The live-session registry** (`sessions/<pid>.json`). Each running interactive session
  writes one, with its pid and the process start time. A session is live only while that
  pid still exists *with the same start time*, so a recycled pid does not keep a dead
  session looking alive. Resuming a live session would put two writers on one transcript,
  so live sessions are never candidates.

A third source is the user's own, not Claude's:

- **The session-end ledger** (`session-ends/<session-id>.json`, or `$CLAUDE_SESSION_LEDGER_DIR`).
  A `SessionEnd` hook installed by `devenv claude` (or the workspace repo's devenv) records
  why each session ended. `reason: "clear"` means the user ran `/clear`: they were done with it,
  so it is not offered again. A crash fires no hook, so a missing record means nothing.
  A record older than the transcript's last activity is stale (the session was resumed
  and used after it ended) and is ignored.
"""

from __future__ import annotations

import os
import sys
from collections.abc import Callable, Iterable, Mapping
from dataclasses import dataclass, replace
from datetime import UTC, datetime
from pathlib import Path
from typing import Annotated, Literal

from pydantic import Field, StrictInt, StrictStr

from desktop_manager.claude_sessions.orca import OrcaFolders
from rig.typed_json import Lenient, try_decode_json

ExitKind = Literal["crashed", "exited"]
#: How the conversation's last turn ended.
#: - answered: the last entry is assistant text; the session was waiting for the user.
#: - mid-tool: a tool call or its result is last; the session died in the middle of work.
#: - unanswered: a typed prompt is last; the session died before replying.
TurnState = Literal["answered", "mid-tool", "unanswered"]

#: Entries that mean the conversation moved on. Bookkeeping entries (titles, modes,
#: `last-prompt`) are written around a clean exit too, so they do not count.
_ACTIVITY_TYPES = frozenset({"user", "assistant", "system", "attachment"})
_INTERACTIVE_ENTRYPOINT = "cli"

#: (pid, registry procStart) -> alive. procStart may be None for old registry files.
LivenessCheck = Callable[[int, str | None], bool]


def claude_home() -> Path:
    configured = os.environ.get("CLAUDE_CONFIG_DIR")
    return Path(configured) if configured else Path.home() / ".claude"


@dataclass(frozen=True)
class SessionRecord:
    session_id: str
    transcript: Path
    start_cwd: str | None
    last_cwd: str | None
    title: str | None
    first_prompt: str | None
    prompt_count: int
    last_activity: datetime | None
    size_bytes: int
    exit_kind: ExitKind
    interactive: bool
    turn_state: TurnState = "answered"
    last_prompt: str | None = None
    last_reply: str | None = None
    #: Why the session ended, from the session-end ledger; None when nothing was recorded.
    end_reason: str | None = None

    @property
    def cleared(self) -> bool:
        return self.end_reason == "clear"

    @property
    def label(self) -> str:
        return self.title or (self.first_prompt or "").strip()[:60] or self.session_id[:8]


@dataclass(frozen=True)
class LiveSession:
    pid: int
    session_id: str
    cwd: str | None
    status: str | None


def _parse_timestamp(value: object) -> datetime | None:
    if not isinstance(value, str):
        return None
    try:
        return datetime.fromisoformat(value.replace("Z", "+00:00"))
    except ValueError:
        return None


@dataclass(frozen=True, slots=True)
class _Block:
    type: Lenient[StrictStr] = None
    text: Lenient[StrictStr] = None


@dataclass(frozen=True, slots=True)
class _Message:
    content: Lenient[StrictStr | list[Lenient[_Block]]] = None


@dataclass(frozen=True, slots=True)
class _Entry:
    """One transcript line. The format is Claude Code's, so every field is optional and read leniently."""

    type: Lenient[StrictStr] = None
    cwd: Lenient[StrictStr] = None
    entrypoint: Lenient[StrictStr] = None
    timestamp: object = None
    message: Lenient[_Message] = None
    is_compact_summary: Annotated[object, Field(alias="isCompactSummary")] = None
    is_meta: Annotated[object, Field(alias="isMeta")] = None
    custom_title: Annotated[Lenient[StrictStr], Field(alias="customTitle")] = None
    ai_title: Annotated[Lenient[StrictStr], Field(alias="aiTitle")] = None
    summary: Lenient[StrictStr] = None


@dataclass(frozen=True, slots=True)
class _Registration:
    """One `sessions/<pid>.json` registry file; pid and session id are required."""

    pid: StrictInt
    session_id: Annotated[StrictStr, Field(alias="sessionId")]
    proc_start: Annotated[Lenient[StrictStr], Field(alias="procStart")] = None
    cwd: Lenient[StrictStr] = None
    status: Lenient[StrictStr] = None


def _content(entry: _Entry) -> str | list[_Block | None] | None:
    return entry.message.content if entry.message is not None else None


def _typed_prompt(entry: _Entry) -> str | None:
    """The text of a prompt the user typed; None for tool results, reminders and compaction summaries."""
    if entry.is_compact_summary or entry.is_meta:
        return None
    content = _content(entry)
    if not isinstance(content, str) or content.startswith("<"):
        return None
    return content


def _entry(line: str) -> _Entry | None:
    return try_decode_json(_Entry, line)


def _content_blocks(entry: _Entry) -> list[_Block]:
    content = _content(entry)
    if not isinstance(content, list):
        return []
    return [block for block in content if block is not None]


def _reply_text(entry: _Entry) -> str:
    return "".join(b.text for b in _content_blocks(entry) if b.type == "text" and b.text).strip()


def _turn_state(entry: _Entry) -> TurnState:
    """How the conversation ended, judged from its last user or assistant entry."""
    if entry.is_compact_summary:
        return "unanswered"
    blocks = _content_blocks(entry)
    if entry.type == "assistant":
        return "mid-tool" if any(b.type == "tool_use" for b in blocks) else "answered"
    if any(b.type == "tool_result" for b in blocks):
        return "mid-tool"
    return "unanswered" if _typed_prompt(entry) is not None else "answered"


def read_transcript(path: Path) -> SessionRecord:
    """Summarise one transcript. Unparseable lines are skipped, never fatal."""
    start_cwd: str | None = None
    last_cwd: str | None = None
    custom_title: str | None = None
    ai_title: str | None = None
    summary: str | None = None
    first_prompt: str | None = None
    prompt_count = 0
    last_activity: datetime | None = None
    last_activity_line = -1
    last_cost_state_line = -1
    entrypoint: str | None = None
    last_turn: _Entry | None = None
    last_prompt: str | None = None
    last_reply: str | None = None

    with path.open(encoding="utf-8", errors="replace") as handle:
        for line_number, line in enumerate(handle):
            entry = _entry(line)
            if entry is None:
                continue
            entry_type = entry.type
            if entry.cwd is not None:
                last_cwd = entry.cwd
                start_cwd = start_cwd or last_cwd
            if entrypoint is None:
                entrypoint = entry.entrypoint
            if entry_type == "cost-state":
                last_cost_state_line = line_number
            elif entry_type == "custom-title":
                custom_title = entry.custom_title or custom_title
            elif entry_type == "ai-title":
                ai_title = entry.ai_title or ai_title
            elif entry_type == "summary":
                summary = entry.summary or summary
            elif entry_type in _ACTIVITY_TYPES:
                last_activity_line = line_number
                last_activity = _parse_timestamp(entry.timestamp) or last_activity
                if entry_type == "user" and (prompt := _typed_prompt(entry)) is not None:
                    prompt_count += 1
                    first_prompt = first_prompt or prompt
                    last_prompt = prompt
                if entry_type == "assistant" and (reply := _reply_text(entry)):
                    last_reply = reply
                if entry_type in ("user", "assistant") and not entry.is_meta:
                    last_turn = entry

    title = custom_title or ai_title or summary
    return SessionRecord(
        session_id=path.stem,
        transcript=path,
        start_cwd=start_cwd,
        last_cwd=last_cwd,
        title=title,
        first_prompt=first_prompt,
        prompt_count=prompt_count,
        last_activity=last_activity,
        size_bytes=path.stat().st_size,
        exit_kind="exited" if last_cost_state_line > last_activity_line else "crashed",
        interactive=entrypoint in (None, _INTERACTIVE_ENTRYPOINT),
        turn_state=_turn_state(last_turn) if last_turn is not None else "answered",
        last_prompt=last_prompt,
        last_reply=last_reply,
    )


def read_live_sessions(sessions_dir: Path, is_alive: LivenessCheck) -> dict[str, LiveSession]:
    """Registry entries whose process is still running, keyed by session id."""
    live: dict[str, LiveSession] = {}
    if not sessions_dir.is_dir():
        return live
    for path in sessions_dir.glob("*.json"):
        try:
            text = path.read_text(encoding="utf-8")
        except OSError:
            continue
        entry = try_decode_json(_Registration, text)
        if entry is not None and is_alive(entry.pid, entry.proc_start):
            live[entry.session_id] = LiveSession(entry.pid, entry.session_id, entry.cwd, entry.status)
    return live


@dataclass(frozen=True)
class SessionEnd:
    reason: str | None
    ended_at: datetime | None


@dataclass(frozen=True, slots=True)
class _LedgerEntry:
    """One session-end record, as the SessionEnd hook writes it."""

    session_id: StrictStr
    reason: Lenient[StrictStr] = None
    ended_at: Lenient[StrictStr] = None


def ledger_dir(home: Path | None = None) -> Path:
    configured = os.environ.get("CLAUDE_SESSION_LEDGER_DIR")
    return Path(configured) if configured else (home or claude_home()) / "session-ends"


def read_session_ends(directory: Path) -> dict[str, SessionEnd]:
    """The session-end ledger, keyed by session id. Unreadable records are skipped."""
    ends: dict[str, SessionEnd] = {}
    if not directory.is_dir():
        return ends
    for path in directory.glob("*.json"):
        try:
            text = path.read_text(encoding="utf-8")
        except OSError:
            continue
        record = try_decode_json(_LedgerEntry, text)
        if record is not None:
            ends[record.session_id] = SessionEnd(record.reason, _parse_timestamp(record.ended_at))
    return ends


def _end_reason(record: SessionRecord, end: SessionEnd | None) -> str | None:
    """The ledger's reason, unless the session was used again after that ending."""
    if end is None:
        return None
    if end.ended_at is not None and record.last_activity is not None and record.last_activity > end.ended_at:
        return None
    return end.reason


def iter_transcripts(projects_dir: Path, *, modified_since: datetime) -> Iterable[Path]:
    """Top-level transcripts touched since the cutoff. Subagent transcripts sit deeper and are skipped."""
    cutoff = modified_since.timestamp()
    for path in projects_dir.glob("*/*.jsonl"):
        try:
            if path.stat().st_mtime >= cutoff:
                yield path
        except OSError:
            continue


@dataclass(frozen=True, slots=True)
class Skipped:
    record: SessionRecord
    reason: str


@dataclass(frozen=True)
class Discovery:
    candidates: list[SessionRecord]
    live: dict[str, LiveSession]
    skipped: list[Skipped]


def discover(
    *,
    since: datetime,
    is_alive: LivenessCheck,
    home: Path | None = None,
    include_exited: bool = True,
    include_cleared: bool = False,
    exclude_ids: Iterable[str] = (),
    orca: OrcaFolders = OrcaFolders(),
) -> Discovery:
    """Sessions active since `since` that are not running now, newest first.

    A clean exit is usually a closed tab, not a decision to stop, so exited sessions are
    kept unless `include_exited` is False. Sessions the user ended with `/clear` are
    skipped unless `include_cleared`. Print-mode sessions, sessions nobody typed into,
    and sessions whose directory has gone are always skipped, as are sessions in folders
    Orca owns (`orca`): Orca resumes those itself.
    """
    root = home or claude_home()
    live = read_live_sessions(root / "sessions", is_alive)
    ends = read_session_ends(ledger_dir(root))
    excluded = set(exclude_ids)
    candidates: list[SessionRecord] = []
    skipped: list[Skipped] = []
    for path in iter_transcripts(root / "projects", modified_since=since):
        record = read_transcript(path)
        record = replace(record, end_reason=_end_reason(record, ends.get(record.session_id)))
        reason = _skip_reason(
            record,
            live=live,
            excluded=excluded,
            include_exited=include_exited,
            include_cleared=include_cleared,
            orca=orca,
        )
        if reason is None:
            candidates.append(record)
        else:
            skipped.append(Skipped(record, reason))
    epoch = datetime.min.replace(tzinfo=UTC)
    candidates.sort(key=lambda r: r.last_activity or epoch, reverse=True)
    return Discovery(candidates=candidates, live=live, skipped=skipped)


def _skip_reason(
    record: SessionRecord,
    *,
    live: Mapping[str, LiveSession],
    excluded: set[str],
    include_exited: bool,
    include_cleared: bool,
    orca: OrcaFolders,
) -> str | None:
    if record.session_id in live:
        return f"running (pid {live[record.session_id].pid})"
    if record.session_id in excluded:
        return "excluded"
    owner = orca.owner(record.start_cwd) or orca.owner(record.last_cwd)
    if owner is not None:
        return f"left to Orca ({owner})"
    if not record.interactive:
        return "not an interactive session"
    if record.prompt_count == 0:
        return "no typed prompts"
    if record.cleared and not include_cleared:
        return "cleared with /clear"
    if record.exit_kind == "exited" and not include_exited:
        return "exited cleanly"
    if record.start_cwd is None or not Path(record.start_cwd).is_dir():
        return f"start directory missing: {record.start_cwd}"
    return None


def process_is_alive(pid: int, proc_start: str | None) -> bool:
    """True if `pid` runs and, when the registry recorded one, started at `proc_start`.

    `procStart` is the process creation time as a Windows FILETIME (100ns ticks). Off
    Windows there is no start time to compare, so only existence is checked.
    """
    if sys.platform != "win32":
        try:
            os.kill(pid, 0)
        except ProcessLookupError:
            return False
        except PermissionError:
            return True
        return True
    started = _windows_process_start(pid)
    return started is not None and (proc_start is None or str(started) == proc_start)


def _open_process(open_process: Callable[[int, bool, int], int], access: int, pid: int) -> int:
    return open_process(access, False, pid)


def _windows_process_start(pid: int) -> int | None:
    if sys.platform != "win32":
        return None
    import ctypes
    from ctypes import wintypes

    process_query_limited_information = 0x1000
    still_active = 259
    kernel32 = ctypes.windll.kernel32
    handle = _open_process(kernel32.OpenProcess, process_query_limited_information, pid)
    if not handle:
        return None
    try:
        exit_code = wintypes.DWORD()
        if not kernel32.GetExitCodeProcess(handle, ctypes.byref(exit_code)) or exit_code.value != still_active:
            return None
        creation, exit_time, kernel, user = (wintypes.FILETIME() for _ in range(4))
        if not kernel32.GetProcessTimes(
            handle, ctypes.byref(creation), ctypes.byref(exit_time), ctypes.byref(kernel), ctypes.byref(user)
        ):
            return None
        return (creation.dwHighDateTime << 32) | creation.dwLowDateTime
    finally:
        kernel32.CloseHandle(handle)
