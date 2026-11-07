"""Session discovery, tab planning and keystroke encoding for `claude-sessions recover`.

Everything here is pure: fake transcripts and registry files in a temp Claude home,
fake terminal windows. The live UIA path is covered by test_claude_sessions_live.py.
"""

from __future__ import annotations

import json
import subprocess
from dataclasses import asdict
from datetime import UTC, datetime, timedelta
from pathlib import Path

import pytest
import typer

from desktop_manager.claude_sessions.cli import parse_since
from desktop_manager.claude_sessions.discovery import SessionRecord, discover, read_live_sessions, read_transcript
from desktop_manager.claude_sessions.folders import folder_facts
from desktop_manager.claude_sessions.plan import (
    assign_slots,
    is_idle_shell_title,
    resume_command,
    shell_kind_for_title,
)
from desktop_manager.claude_sessions.terminal import TerminalTab, TerminalWindow
from desktop_manager.claude_sessions.wt_uia import KeyEvent, echoed, keystrokes_for

pytestmark = pytest.mark.ci

NOW = datetime(2026, 9, 25, 10, 0, tzinfo=UTC)


def _user(text: str, cwd: str, at: datetime) -> dict[str, object]:
    return {
        "type": "user",
        "entrypoint": "cli",
        "cwd": cwd,
        "timestamp": at.isoformat().replace("+00:00", "Z"),
        "message": {"role": "user", "content": text},
    }


def _write_transcript(home: Path, session_id: str, entries: list[dict[str, object]], project: str = "C--code") -> Path:
    path = home / "projects" / project / f"{session_id}.jsonl"
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text("\n".join(json.dumps(entry) for entry in entries) + "\n", encoding="utf-8")
    return path


def _register(home: Path, pid: int, session_id: str, proc_start: str = "1") -> None:
    sessions = home / "sessions"
    sessions.mkdir(parents=True, exist_ok=True)
    (sessions / f"{pid}.json").write_text(
        json.dumps({"pid": pid, "sessionId": session_id, "procStart": proc_start, "cwd": "x"}), encoding="utf-8"
    )


class TestReadTranscript:
    def test_trailing_cost_state_means_a_clean_exit(self, tmp_path: Path):
        path = _write_transcript(
            tmp_path, "a", [_user("hi", str(tmp_path), NOW), {"type": "last-prompt"}, {"type": "cost-state"}]
        )
        assert read_transcript(path).exit_kind == "exited"

    def test_activity_after_the_last_cost_state_means_a_crash(self, tmp_path: Path):
        # Resumed after a clean exit, then killed: the old cost-state no longer covers it.
        path = _write_transcript(
            tmp_path, "a", [_user("hi", str(tmp_path), NOW), {"type": "cost-state"}, _user("more", str(tmp_path), NOW)]
        )
        assert read_transcript(path).exit_kind == "crashed"

    def test_titles_prefer_custom_then_ai_and_count_only_typed_prompts(self, tmp_path: Path):
        path = _write_transcript(
            tmp_path,
            "a",
            [
                _user("first real prompt", str(tmp_path), NOW),
                _user("<system-reminder>injected</system-reminder>", str(tmp_path), NOW),
                {"type": "user", "message": {"content": [{"type": "tool_result"}]}},
                {"type": "ai-title", "aiTitle": "AI title"},
                {"type": "custom-title", "customTitle": "Mine"},
                {"type": "garbage-line-follows"},
            ],
        )
        path.write_text(path.read_text(encoding="utf-8") + "{not json\n", encoding="utf-8")
        record = read_transcript(path)
        assert record.title == "Mine"
        assert record.prompt_count == 1
        assert record.first_prompt == "first real prompt"

    @pytest.mark.parametrize(
        ("last", "state"),
        [
            ({"type": "assistant", "message": {"content": [{"type": "text", "text": "Done."}]}}, "answered"),
            ({"type": "assistant", "message": {"content": [{"type": "tool_use", "id": "t"}]}}, "mid-tool"),
            ({"type": "user", "message": {"content": [{"type": "tool_result", "tool_use_id": "t"}]}}, "mid-tool"),
            ({"type": "user", "message": {"content": "and then?"}}, "unanswered"),
            ({"type": "user", "isCompactSummary": True, "message": {"content": "This session is..."}}, "unanswered"),
        ],
    )
    def test_turn_state_comes_from_the_last_turn(self, tmp_path: Path, last: dict[str, object], state: str):
        path = _write_transcript(tmp_path, "a", [_user("hi", str(tmp_path), NOW), last, {"type": "last-prompt"}])
        assert read_transcript(path).turn_state == state

    def test_last_prompt_and_reply_skip_summaries_and_tool_only_turns(self, tmp_path: Path):
        path = _write_transcript(
            tmp_path,
            "a",
            [
                _user("do the thing", str(tmp_path), NOW),
                {"type": "assistant", "message": {"content": [{"type": "text", "text": "Starting."}]}},
                {"type": "user", "isCompactSummary": True, "message": {"content": "This session is being continued"}},
                {"type": "assistant", "message": {"content": [{"type": "tool_use", "id": "t"}]}},
            ],
        )
        record = read_transcript(path)
        assert (record.last_prompt, record.last_reply, record.prompt_count) == ("do the thing", "Starting.", 1)

    def test_start_dir_is_the_first_cwd_and_last_dir_the_latest(self, tmp_path: Path):
        start, worktree = str(tmp_path), str(tmp_path / ".worktrees" / "fix")
        path = _write_transcript(tmp_path, "a", [_user("hi", start, NOW), _user("go", worktree, NOW)])
        record = read_transcript(path)
        assert (record.start_cwd, record.last_cwd) == (start, worktree)

    def test_misshapen_lines_and_fields_are_ignored_not_fatal(self, tmp_path: Path):
        path = _write_transcript(
            tmp_path,
            "a",
            [
                {"type": ["not", "a", "string"], "cwd": 7},
                _user("hi", str(tmp_path), NOW),
                {"type": "assistant", "message": {"content": ["text", {"type": "text", "text": "Done."}]}},
                {"type": "custom-title", "customTitle": 42},
                {"type": "ai-title", "aiTitle": "AI title"},
                {"type": "assistant", "message": "not an object"},
            ],
        )
        path.write_text(path.read_text(encoding="utf-8") + '[1, 2]\n"text"\n', encoding="utf-8")
        record = read_transcript(path)
        assert (record.start_cwd, record.title, record.last_reply) == (str(tmp_path), "AI title", "Done.")
        assert (record.prompt_count, record.turn_state) == (1, "answered")

    def test_print_mode_sessions_are_not_interactive(self, tmp_path: Path):
        entry = _user("hi", str(tmp_path), NOW) | {"entrypoint": "sdk-cli"}
        assert not read_transcript(_write_transcript(tmp_path, "a", [entry])).interactive


class TestDiscover:
    def test_live_sessions_are_never_candidates_and_dead_registry_entries_do_not_count(self, tmp_path: Path):
        cwd = str(tmp_path)
        for session_id in ("live", "dead", "gone"):
            _write_transcript(tmp_path, session_id, [_user("hi", cwd, NOW)])
        _register(tmp_path, 100, "live")
        _register(tmp_path, 200, "dead")

        found = discover(
            since=datetime.now(UTC) - timedelta(hours=1), is_alive=lambda pid, _: pid == 100, home=tmp_path
        )

        assert set(found.live) == {"live"}
        assert {r.session_id for r in found.candidates} == {"dead", "gone"}
        assert {s.record.session_id: s.reason for s in found.skipped}["live"] == "running (pid 100)"

    def test_misshapen_registry_files_are_not_live(self, tmp_path: Path):
        sessions = tmp_path / "sessions"
        sessions.mkdir()
        (sessions / "1.json").write_text("[1]", encoding="utf-8")
        (sessions / "2.json").write_text(json.dumps({"pid": "2", "sessionId": "x"}), encoding="utf-8")
        (sessions / "3.json").write_text(json.dumps({"pid": 3, "sessionId": "ok", "procStart": 5}), encoding="utf-8")
        starts: list[str | None] = []

        def alive(pid: int, proc_start: str | None) -> bool:
            starts.append(proc_start)
            return True

        assert set(read_live_sessions(sessions, alive)) == {"ok"}
        assert starts == [None]

    def test_skips_clean_exits_only_when_asked_and_always_skips_missing_dirs(self, tmp_path: Path):
        cwd = str(tmp_path)
        _write_transcript(tmp_path, "clean", [_user("hi", cwd, NOW), {"type": "cost-state"}])
        _write_transcript(tmp_path, "crash", [_user("hi", cwd, NOW)])
        _write_transcript(tmp_path, "moved", [_user("hi", str(tmp_path / "deleted-worktree"), NOW)])
        _write_transcript(tmp_path, "empty", [{"type": "mode", "mode": "normal"}])
        since = datetime.now(UTC) - timedelta(hours=1)

        everything = discover(since=since, is_alive=lambda *_: False, home=tmp_path)
        crashes = discover(since=since, is_alive=lambda *_: False, home=tmp_path, include_exited=False)

        assert {r.session_id for r in everything.candidates} == {"clean", "crash"}
        assert {r.session_id for r in crashes.candidates} == {"crash"}
        reasons = {s.record.session_id: s.reason for s in everything.skipped}
        assert reasons["moved"].startswith("start directory missing")
        assert reasons["empty"] == "no typed prompts"

    def test_subagent_transcripts_are_ignored_and_newest_comes_first(self, tmp_path: Path):
        cwd = str(tmp_path)
        _write_transcript(tmp_path, "old", [_user("hi", cwd, NOW - timedelta(hours=2))])
        _write_transcript(tmp_path, "new", [_user("hi", cwd, NOW)])
        _write_transcript(tmp_path, "sub", [_user("hi", cwd, NOW)], project="C--code/old/subagents")

        found = discover(since=datetime.now(UTC) - timedelta(hours=1), is_alive=lambda *_: False, home=tmp_path)

        assert [r.session_id for r in found.candidates] == ["new", "old"]


def _session(session_id: str, cwd: str = r"C:\code", last_cwd: str | None = None) -> SessionRecord:
    return SessionRecord(
        session_id=session_id,
        transcript=Path(f"{session_id}.jsonl"),
        start_cwd=cwd,
        last_cwd=last_cwd or cwd,
        title=session_id,
        first_prompt=None,
        prompt_count=1,
        last_activity=NOW,
        size_bytes=0,
        exit_kind="crashed",
        interactive=True,
    )


def _window(hwnd: int, *titles: str) -> TerminalWindow:
    return TerminalWindow(
        hwnd=hwnd,
        title=titles[0],
        tabs=tuple(TerminalTab(i, title, i == 0, (hwnd, i)) for i, title in enumerate(titles)),
    )


class TestFolderFacts:
    def test_missing_dir(self, tmp_path: Path):
        gone = str(tmp_path / "gone")
        assert asdict(folder_facts(gone)) == {
            "path": gone,
            "exists": False,
            "git_root": None,
            "branch": None,
            "is_worktree": False,
        }

    def test_repo_and_worktree(self, tmp_path: Path):
        repo, tree = tmp_path / "repo", tmp_path / "tree"
        repo.mkdir()
        git = ["git", "-c", "user.name=t", "-c", "user.email=t@t", "-C", str(repo)]
        subprocess.run([*git, "init", "-q", "-b", "main"], check=True)
        subprocess.run([*git, "commit", "-q", "--allow-empty", "-m", "x"], check=True)
        subprocess.run([*git, "worktree", "add", "-q", "-b", "fix", str(tree)], check=True)

        main, worktree = folder_facts(str(repo)), folder_facts(str(tree))

        assert (main.exists, main.branch, main.is_worktree) == (True, "main", False)
        assert (worktree.branch, worktree.is_worktree) == ("fix", True)
        assert worktree.git_root is not None
        assert Path(worktree.git_root).resolve() == tree.resolve()


class TestTabTitles:
    @pytest.mark.parametrize(
        "title",
        [
            "Windows PowerShell",
            "PowerShell",
            "PowerShell 7.5.2",
            "pwsh in code",
            "Command Prompt",
            "Administrator: Windows PowerShell",
            r"C:\WINDOWS\System32\WindowsPowerShell\v1.0\powershell.exe",
        ],
    )
    def test_bare_shell_titles_are_idle(self, title: str):
        assert is_idle_shell_title(title)

    @pytest.mark.parametrize(
        "title", ["✳ Recover crashed sessions", "◐ Working on it", "vim notes.md", "ssh prod-box", "npm run dev"]
    )
    def test_claude_and_other_program_titles_are_busy(self, title: str):
        assert not is_idle_shell_title(title)

    def test_shell_kind(self):
        assert shell_kind_for_title("Command Prompt") == "cmd"
        assert shell_kind_for_title(r"C:\Windows\system32\cmd.exe") == "cmd"
        assert shell_kind_for_title("pwsh in code") == "powershell"


class TestResumeCommand:
    def test_powershell_quotes_the_directory_literally(self):
        command = resume_command(_session("abc", r"C:\it's\[odd]"), shell="powershell", claude_args=["--model", "opus"])
        assert command == r"Set-Location -LiteralPath 'C:\it''s\[odd]'; claude --resume abc --model opus"

    def test_resumes_in_the_start_dir_not_the_last(self):
        session = _session("abc", r"C:\code", last_cwd=r"C:\code\repo\.worktrees\x")
        assert resume_command(session, shell="cmd") == r'cd /d "C:\code" && claude --resume abc'

    def test_cmd_changes_drive_too(self):
        assert resume_command(_session("abc", r"D:\x y"), shell="cmd") == r'cd /d "D:\x y" && claude --resume abc'

    def test_posix_quotes_the_directory_and_arguments(self):
        command = resume_command(_session("abc", "/home/me/it's here"), shell="posix", claude_args=["--model=o p"])
        assert command == """cd '/home/me/it'"'"'s here' && claude --resume abc '--model=o p'"""


class TestAssignSlots:
    def test_idle_tabs_fill_first_then_new_tabs_spread_to_the_emptiest_window(self):
        windows = [_window(1, "✳ busy", "pwsh in code"), _window(2, "✳ busy", "✳ busy", "✳ busy")]
        slots = assign_slots([_session(s) for s in "abcd"], windows)
        assert [(s.session.session_id, s.window, s.tab) for s in slots] == [
            ("a", 1, (1, 1)),  # the idle tab
            ("b", 1, None),  # 2 tabs vs 3
            ("c", 1, None),  # 3 vs 3: a tie goes to the first window
            ("d", 2, None),
        ]

    def test_overflow_goes_to_new_windows_once_every_window_is_full(self):
        slots = assign_slots([_session(s) for s in "abc"], [_window(1, "✳ busy")], max_tabs_per_window=2)
        assert [(s.window, s.tab) for s in slots] == [(1, None), (None, None), (None, None)]

    def test_no_windows_means_new_windows(self):
        assert [s.describe for s in assign_slots([_session("a")], [])] == ["new window 1"]

    def test_reuse_idle_off_and_window_filter(self):
        windows = [_window(1, "pwsh in code"), _window(2, "pwsh in code")]
        slots = assign_slots([_session("a")], windows, reuse_idle=False, only_window=2)
        assert [(s.window, s.tab) for s in slots] == [(2, None)]


class TestKeystrokes:
    def test_escape_clears_the_line_then_unicode_chars_then_enter(self):
        events = keystrokes_for("a✳")
        assert events[:2] == [KeyEvent(0x1B, 0, 0), KeyEvent(0x1B, 0, 2)]
        assert events[2:6] == [
            KeyEvent(0, ord("a"), 4),
            KeyEvent(0, ord("a"), 6),
            KeyEvent(0, 0x2733, 4),
            KeyEvent(0, 0x2733, 6),
        ]
        assert events[-2:] == [KeyEvent(0x0D, 0, 0), KeyEvent(0x0D, 0, 2)]

    def test_astral_characters_become_surrogate_pairs(self):
        events = keystrokes_for("😀", clear_line=False, submit=False)
        assert [event.scan for event in events if event.flags == 4] == [0xD83D, 0xDE00]


class TestParseSince:
    def test_units(self):
        assert parse_since("90m") == timedelta(minutes=90)
        assert parse_since("12h") == timedelta(hours=12)
        assert parse_since("1.5d") == timedelta(days=1.5)

    def test_rejects_bare_numbers(self):
        with pytest.raises(typer.BadParameter):
            parse_since("12")


class TestEchoed:
    COMMAND = r"Set-Location -LiteralPath 'C:\code\workspace'; claude"
    PROMPT = "PS C:\\code\\workspace> "

    def test_typed_text_counts_once_it_newly_appears_even_wrapped(self):
        assert not echoed(self.PROMPT, self.PROMPT, self.COMMAND)
        assert echoed(self.PROMPT, self.PROMPT + self.COMMAND, self.COMMAND)
        wrapped = self.COMMAND.replace("workspace", "work\n  space")
        assert echoed(self.PROMPT, self.PROMPT + wrapped, self.COMMAND)

    def test_the_same_command_already_in_scrollback_does_not_count(self):
        screen = f"{self.PROMPT}{self.COMMAND}\n{self.PROMPT}"
        assert not echoed(screen, screen, self.COMMAND)
        assert echoed(screen, screen + self.COMMAND, self.COMMAND)

    def test_nothing_to_type_never_counts(self):
        assert not echoed(self.PROMPT, self.PROMPT, "")
