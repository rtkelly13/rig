"""Tests for the session-ledger SessionEnd hook install, merge, and doctor check."""

import json
from pathlib import Path

import pytest
from typer.testing import CliRunner

from rig.devenv.cli import app
from rig.devenv.doctor.core import run_doctor
from rig.devenv.steps.ai_tools.claude_code import (
    get_claude_settings_path,
    load_settings,
    merge_claude_settings,
    setup_claude_code,
)
from rig.devenv.steps.ai_tools.session_ledger import (
    SESSION_LEDGER_SOURCE,
    SESSION_LEDGER_TIMEOUT_SECONDS,
    LedgerHookState,
    merge_session_ledger_hook,
    runs_session_ledger,
    session_ledger_command,
    session_ledger_hook_path,
    session_ledger_state,
)

WORKSPACE_STYLE = 'node "C:/Users/someone/.claude/hooks/session-ledger.mjs"'


@pytest.fixture
def home(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> Path:
    """A throwaway home directory, so nothing touches the real ~/.claude."""
    monkeypatch.setenv("HOME", str(tmp_path))
    monkeypatch.setenv("USERPROFILE", str(tmp_path))
    monkeypatch.delenv("CLAUDE_SESSION_LEDGER_DIR", raising=False)
    assert Path.home() == tmp_path
    return tmp_path


def _settings(home: Path) -> dict[str, object]:
    return json.loads(get_claude_settings_path(home).read_text(encoding="utf-8"))


def _ledger_entries(settings: dict[str, object]) -> list[str]:
    groups = settings["hooks"]["SessionEnd"]
    return [h["command"] for g in groups for h in g["hooks"] if runs_session_ledger(h["command"])]


@pytest.mark.ci
@pytest.mark.parametrize(
    ("command", "expected"),
    [
        ('node "C:/Users/me/.claude/hooks/session-ledger.mjs"', True),
        ("node C:\\Users\\me\\.claude\\hooks\\session-ledger.mjs", True),
        ("node --no-warnings /home/me/hooks/session-ledger.mjs", True),
        ('"/usr/bin/node.exe" ~/session-ledger.mjs', True),
        ("/home/me/.claude/hooks/session-ledger.mjs", True),
        ("echo session-ledger.mjs", False),
        ('node "/home/me/other.mjs" session-ledger.mjs', False),
        ('node "unterminated', False),
        ("", False),
    ],
)
def test_detection_matches_the_script_basename(command: str, expected: bool):
    assert runs_session_ledger(command) is expected


@pytest.mark.ci
def test_merge_adds_the_hook_once():
    settings: dict[str, object] = {}
    command = session_ledger_command(Path("/home/me/.claude/hooks/session-ledger.mjs"))

    assert merge_session_ledger_hook(settings, command) == [f"+ hooks.SessionEnd: {command}"]
    assert settings == {
        "hooks": {
            "SessionEnd": [
                {"hooks": [{"type": "command", "command": command, "timeout": SESSION_LEDGER_TIMEOUT_SECONDS}]}
            ]
        }
    }
    assert merge_session_ledger_hook(settings, command) == []
    assert len(_ledger_entries(settings)) == 1


@pytest.mark.ci
def test_an_install_from_another_path_is_recognised():
    """The workspace installer's entry counts, wherever its script lives."""
    existing = {"hooks": {"SessionEnd": [{"hooks": [{"type": "command", "command": WORKSPACE_STYLE}]}]}}

    merge = merge_claude_settings(existing, session_end_command='node "/elsewhere/session-ledger.mjs"')

    assert not any("hooks" in c for c in merge.changes)
    assert _ledger_entries(merge.settings) == [WORKSPACE_STYLE]


@pytest.mark.ci
def test_merge_keeps_the_users_other_hooks():
    mine = {"matcher": "", "hooks": [{"type": "command", "command": "notify-send bye"}]}
    pre = [{"matcher": "Bash", "hooks": [{"type": "command", "command": "audit"}]}]
    existing = {"hooks": {"SessionEnd": [mine], "PreToolUse": pre}, "statusLine": {"type": "command"}}

    settings = merge_claude_settings(existing, session_end_command='node "/h/session-ledger.mjs"').settings

    assert settings["hooks"]["PreToolUse"] == pre
    assert settings["hooks"]["SessionEnd"][0] == mine
    assert _ledger_entries(settings) == ['node "/h/session-ledger.mjs"']
    assert settings["statusLine"] == {"type": "command"}


@pytest.mark.ci
@pytest.mark.parametrize("hooks", ["not an object", {"SessionEnd": "not a list"}])
def test_misshapen_hooks_are_left_alone(hooks: object):
    settings: dict[str, object] = {"hooks": hooks}

    assert merge_session_ledger_hook(settings, 'node "/h/session-ledger.mjs"') == []
    assert settings == {"hooks": hooks}


@pytest.mark.ci
def test_setup_installs_script_and_wiring_idempotently(home: Path):
    settings_path = get_claude_settings_path(home)
    settings_path.parent.mkdir(parents=True)
    settings_path.write_text(json.dumps({"model": "sonnet", "theme": "dark"}), encoding="utf-8")
    hook_path = session_ledger_hook_path(home)

    setup_claude_code()

    settings = _settings(home)
    assert settings["model"] == "sonnet"
    assert settings["theme"] == "dark"
    assert _ledger_entries(settings) == [session_ledger_command(hook_path)]
    assert hook_path.read_bytes() == SESSION_LEDGER_SOURCE.read_bytes().replace(b"\r\n", b"\n")
    assert session_ledger_state(load_settings(settings_path), hook_path) is LedgerHookState.CURRENT

    before = settings_path.read_bytes()
    setup_claude_code()
    assert settings_path.read_bytes() == before
    assert len(_ledger_entries(_settings(home))) == 1
    assert not list(settings_path.parent.glob("*.tmp"))


@pytest.mark.ci
def test_setup_updates_a_stale_script_and_accepts_crlf(home: Path):
    setup_claude_code()
    hook_path = session_ledger_hook_path(home)
    settings = load_settings(get_claude_settings_path(home))

    hook_path.write_text("// an older ledger\n", encoding="utf-8")
    assert session_ledger_state(settings, hook_path) is LedgerHookState.SCRIPT_OUTDATED
    setup_claude_code()
    assert session_ledger_state(settings, hook_path) is LedgerHookState.CURRENT

    hook_path.write_bytes(hook_path.read_bytes().replace(b"\n", b"\r\n"))
    assert session_ledger_state(settings, hook_path) is LedgerHookState.CURRENT


@pytest.mark.ci
def test_dry_run_writes_nothing(home: Path):
    setup_claude_code(dry_run=True)

    assert not (home / ".claude").exists()


@pytest.mark.ci
def test_unreadable_settings_are_never_overwritten(home: Path):
    settings_path = get_claude_settings_path(home)
    settings_path.parent.mkdir(parents=True)
    settings_path.write_text('{"model": "sonnet",', encoding="utf-8")

    setup_claude_code()

    assert settings_path.read_text(encoding="utf-8") == '{"model": "sonnet",'
    assert not session_ledger_hook_path(home).exists()


@pytest.mark.ci
def test_state_reports_each_gap(home: Path):
    hook_path = session_ledger_hook_path(home)
    wired: dict[str, object] = {}
    merge_session_ledger_hook(wired, session_ledger_command(hook_path))

    assert session_ledger_state({}, hook_path) is LedgerHookState.NOT_WIRED
    assert session_ledger_state(wired, hook_path) is LedgerHookState.SCRIPT_MISSING


@pytest.mark.ci
def test_cli_claude_command_installs_the_hook(home: Path):
    runner = CliRunner()

    dry = runner.invoke(app, ["claude", "--dry-run"])
    assert dry.exit_code == 0
    assert not (home / ".claude").exists()

    result = runner.invoke(app, ["claude"])
    assert result.exit_code == 0
    state = session_ledger_state(load_settings(get_claude_settings_path(home)), session_ledger_hook_path(home))
    assert state is LedgerHookState.CURRENT


@pytest.mark.ci
def test_doctor_flags_a_missing_hook(home: Path, monkeypatch: pytest.MonkeyPatch, capsys: pytest.CaptureFixture[str]):
    monkeypatch.setattr("rig.devenv.steps.git.get_git_config_val", lambda _key: "x")
    monkeypatch.setenv("COLUMNS", "200")

    run_doctor()
    assert "Not wired in settings.json" in capsys.readouterr().out

    setup_claude_code()
    capsys.readouterr()
    run_doctor()
    out = capsys.readouterr().out
    assert "Session-Ledger Hook" in out
    assert "Not wired" not in out
