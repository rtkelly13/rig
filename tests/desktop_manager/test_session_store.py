import json
import os
from datetime import UTC, datetime
from pathlib import Path

from desktop_manager.work.session_store import (
    SessionRecord,
    claude_home,
    folder_facts,
    iter_transcripts,
    ledger_dir,
    process_is_alive,
    read_live_sessions,
    read_session_ends,
    read_transcript,
)


def test_claude_home(monkeypatch, tmp_path: Path):
    monkeypatch.delenv("CLAUDE_CONFIG_DIR", raising=False)
    assert claude_home() == Path.home() / ".claude"

    custom = tmp_path / "custom"
    monkeypatch.setenv("CLAUDE_CONFIG_DIR", str(custom))
    assert claude_home() == custom


def test_session_record_label():
    rec = SessionRecord(
        session_id="abcdef123456",
        transcript=Path("test.jsonl"),
        start_cwd="/repo",
        last_cwd="/repo/sub",
        title="My Cool Title",
        first_prompt="first prompt here",
        prompt_count=2,
        last_activity=None,
        size_bytes=100,
        exit_kind="exited",
        interactive=True,
    )
    assert rec.label == "My Cool Title"

    rec_no_title = SessionRecord(
        session_id="abcdef123456",
        transcript=Path("test.jsonl"),
        start_cwd=None,
        last_cwd=None,
        title=None,
        first_prompt="hello world prompt",
        prompt_count=1,
        last_activity=None,
        size_bytes=100,
        exit_kind="crashed",
        interactive=True,
    )
    assert rec_no_title.label == "hello world prompt"

    rec_fallback = SessionRecord(
        session_id="abcdef123456",
        transcript=Path("test.jsonl"),
        start_cwd=None,
        last_cwd=None,
        title=None,
        first_prompt=None,
        prompt_count=0,
        last_activity=None,
        size_bytes=100,
        exit_kind="crashed",
        interactive=False,
    )
    assert rec_fallback.label == "abcdef12"


def test_read_transcript_exited_session(tmp_path: Path):
    t_file = tmp_path / "session1.jsonl"
    lines = [
        {
            "type": "user",
            "cwd": "/repo",
            "entrypoint": "cli",
            "timestamp": "2026-11-15T10:00:00Z",
            "message": {"content": "fix bug"},
        },
        {"type": "ai-title", "aiTitle": "Bugfix session"},
        {
            "type": "assistant",
            "timestamp": "2026-11-15T10:01:00Z",
            "message": {"content": [{"type": "text", "text": "I fixed it."}]},
        },
        {"type": "cost-state"},
    ]
    t_file.write_text("\n".join(json.dumps(line) for line in lines) + "\nnot a json line\n", encoding="utf-8")

    rec = read_transcript(t_file)
    assert rec.session_id == "session1"
    assert rec.start_cwd == "/repo"
    assert rec.last_cwd == "/repo"
    assert rec.title == "Bugfix session"
    assert rec.prompt_count == 1
    assert rec.first_prompt == "fix bug"
    assert rec.last_prompt == "fix bug"
    assert rec.last_reply == "I fixed it."
    assert rec.exit_kind == "exited"
    assert rec.interactive is True
    assert rec.turn_state == "answered"
    assert rec.last_activity == datetime(2026, 11, 15, 10, 1, 0, tzinfo=UTC)


def test_read_transcript_crashed_mid_tool(tmp_path: Path):
    t_file = tmp_path / "crashed_session.jsonl"
    lines = [
        {"type": "user", "cwd": "/start", "timestamp": "2026-11-15T10:00:00Z", "message": {"content": "do tool"}},
        {
            "type": "assistant",
            "cwd": "/work",
            "timestamp": "2026-11-15T10:01:00Z",
            "message": {"content": [{"type": "tool_use", "name": "run"}]},
        },
    ]
    t_file.write_text("\n".join(json.dumps(line) for line in lines) + "\n", encoding="utf-8")

    rec = read_transcript(t_file)
    assert rec.start_cwd == "/start"
    assert rec.last_cwd == "/work"
    assert rec.exit_kind == "crashed"
    assert rec.turn_state == "mid-tool"


def test_read_live_sessions(tmp_path: Path):
    sessions_dir = tmp_path / "sessions"
    sessions_dir.mkdir()

    s1 = {"pid": 1234, "sessionId": "sess-1", "procStart": "100", "cwd": "/code", "status": "active"}
    s2 = {"pid": 5678, "sessionId": "sess-2", "procStart": "200"}
    (sessions_dir / "1234.json").write_text(json.dumps(s1), encoding="utf-8")
    (sessions_dir / "5678.json").write_text(json.dumps(s2), encoding="utf-8")
    (sessions_dir / "bad.json").write_text("invalid json", encoding="utf-8")

    def is_alive(pid: int, proc_start: str | None) -> bool:
        return pid == 1234

    live = read_live_sessions(sessions_dir, is_alive)
    assert len(live) == 1
    assert "sess-1" in live
    assert live["sess-1"].pid == 1234
    assert live["sess-1"].cwd == "/code"

    # Non-existent dir returns empty
    assert read_live_sessions(tmp_path / "missing", is_alive) == {}


def test_read_session_ends_and_ledger_dir(monkeypatch, tmp_path: Path):
    ledger = tmp_path / "session-ends"
    ledger.mkdir()
    monkeypatch.setenv("CLAUDE_SESSION_LEDGER_DIR", str(ledger))
    assert ledger_dir() == ledger

    rec1 = {"session_id": "sess-1", "reason": "clear", "ended_at": "2026-11-15T12:00:00Z"}
    (ledger / "sess-1.json").write_text(json.dumps(rec1), encoding="utf-8")
    (ledger / "corrupt.json").write_text("bad json", encoding="utf-8")

    ends = read_session_ends(ledger)
    assert len(ends) == 1
    assert ends["sess-1"].reason == "clear"
    assert ends["sess-1"].ended_at == datetime(2026, 11, 15, 12, 0, 0, tzinfo=UTC)
    assert read_session_ends(tmp_path / "missing") == {}


def test_iter_transcripts(tmp_path: Path):
    projects = tmp_path / "projects"
    p1 = projects / "proj1"
    p1.mkdir(parents=True)
    t1 = p1 / "sessionA.jsonl"
    t1.write_text("{}", encoding="utf-8")

    sub = p1 / "subagents"
    sub.mkdir()
    (sub / "sub.jsonl").write_text("{}", encoding="utf-8")

    cutoff = datetime(2020, 1, 1, tzinfo=UTC)
    found = list(iter_transcripts(projects, modified_since=cutoff))
    assert found == [t1]

    future = datetime(2099, 1, 1, tzinfo=UTC)
    assert list(iter_transcripts(projects, modified_since=future)) == []


def test_process_is_alive():
    assert process_is_alive(os.getpid(), None) is True
    # PID 99999999 is extraordinarily unlikely to exist
    assert process_is_alive(99999999, None) is False


def test_folder_facts(tmp_path: Path):
    missing = tmp_path / "missing"
    facts = folder_facts(str(missing))
    assert facts.exists is False
    assert facts.git_root is None

    plain_dir = tmp_path / "plain"
    plain_dir.mkdir()
    facts_plain = folder_facts(str(plain_dir))
    assert facts_plain.exists is True
    assert facts_plain.git_root is None
