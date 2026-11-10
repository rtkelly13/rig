"""Tests for the low-priority SessionStart hook, the env baseline, and the hook script's process walk."""

import json
import os
import shutil
import subprocess
from pathlib import Path

import pytest

from rig import tracing
from rig.devenv.steps.ai_tools.bundled_hooks import LOW_PRIORITY, SESSION_LEDGER, HookState
from rig.devenv.steps.ai_tools.claude_code import (
    get_claude_settings_path,
    load_settings,
    merge_claude_settings,
    setup_claude_code,
)

ORCA_HOOK = {"hooks": [{"type": "command", "command": "/Applications/Orca.app/hook session-start"}]}
NODE = shutil.which("node")
needs_node = pytest.mark.skipif(NODE is None, reason="node is not installed")


@pytest.fixture
def home(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> Path:
    """A throwaway home directory, so nothing touches the real ~/.claude."""
    monkeypatch.setenv("HOME", str(tmp_path))
    monkeypatch.setenv("USERPROFILE", str(tmp_path))
    assert Path.home() == tmp_path
    return tmp_path


def _start_commands(settings: dict[str, object]) -> list[str]:
    groups = settings["hooks"]["SessionStart"]
    return [h["command"] for g in groups for h in g["hooks"] if LOW_PRIORITY.runs(h["command"])]


@pytest.mark.ci
def test_merge_adds_the_hook_beside_other_session_start_hooks():
    existing = {"hooks": {"SessionStart": [ORCA_HOOK]}}

    merge = merge_claude_settings(existing, session_start_command='node "/h/low-priority.mjs"')

    assert merge.settings["hooks"]["SessionStart"][0] == ORCA_HOOK
    assert _start_commands(merge.settings) == ['node "/h/low-priority.mjs"']
    assert merge.settings["hooks"]["SessionStart"][1]["hooks"][0]["timeout"] == LOW_PRIORITY.timeout_seconds
    again = merge_claude_settings(merge.settings, session_start_command='node "/elsewhere/low-priority.mjs"')
    assert not any("SessionStart" in c for c in again.changes)


@pytest.mark.ci
def test_env_baseline_adds_missing_keys_and_keeps_the_users():
    merge = merge_claude_settings({"env": {"MSBUILDDISABLENODEREUSE": "0", "OTHER": "x"}})
    assert merge.settings["env"] == {"MSBUILDDISABLENODEREUSE": "0", "OTHER": "x"}

    fresh = merge_claude_settings({})
    assert fresh.settings["env"] == {"MSBUILDDISABLENODEREUSE": "1"}
    assert "+ env.MSBUILDDISABLENODEREUSE: '1'" in fresh.changes


@pytest.mark.ci
def test_setup_installs_both_hooks_idempotently(home: Path):
    setup_claude_code()
    settings_path = get_claude_settings_path(home)
    settings = load_settings(settings_path)

    for hook in (SESSION_LEDGER, LOW_PRIORITY):
        assert hook.state(settings, hook.hook_path(home)) is HookState.CURRENT
    before = settings_path.read_bytes()

    LOW_PRIORITY.hook_path(home).write_text("// an older script\n", encoding="utf-8")
    assert LOW_PRIORITY.state(settings, LOW_PRIORITY.hook_path(home)) is HookState.SCRIPT_OUTDATED
    setup_claude_code()

    assert LOW_PRIORITY.state(settings, LOW_PRIORITY.hook_path(home)) is HookState.CURRENT
    assert settings_path.read_bytes() == before


def _run_node(script: str) -> str:
    assert NODE is not None
    result = tracing.run(
        [NODE, "--input-type=module", "-e", script],
        capture_output=True,
        check=True,
        timeout=30,
    )
    return result.stdout


@pytest.mark.ci
@needs_node
def test_script_walks_up_to_the_claude_process():
    module = LOW_PRIORITY.source.as_uri()
    out = _run_node(f"""
import {{ parseProcessTable, findClaude, childrenOf }} from {json.dumps(module)};
const mac = parseProcessTable(`
  100     1 /Applications/Claude.app/Contents/MacOS/Claude
  200     1 /bin/zsh
  300   200 claude
  310   300 node
  320   300 caffeinate
  400   300 /bin/sh
  500   400 node
`);
const win = parseProcessTable("10 1 explorer.exe\\r\\n20 10 Claude.EXE\\r\\n30 20 bash.exe\\r\\n40 30 node.exe\\r\\n");
console.log(JSON.stringify({{
  mac: findClaude(mac, 400, "darwin"),
  children: childrenOf(mac, 300),
  desktopApp: findClaude(mac, 100, "darwin"),
  missing: findClaude(mac, 999, "darwin"),
  win: findClaude(win, 30, "win32"),
  winOnPosixRules: findClaude(win, 30, "darwin"),
}}));
""")
    assert json.loads(out) == {
        "mac": 300,
        "children": [310, 320, 400],
        "desktopApp": None,
        "missing": None,
        "win": 20,
        "winOnPosixRules": None,
    }


@pytest.mark.ci
@needs_node
def test_foreground_opt_out_changes_nothing_and_prints_nothing():
    assert NODE is not None
    env = {**os.environ, "CLAUDE_FOREGROUND": "1"}
    result = tracing.run(
        [NODE, str(LOW_PRIORITY.source)],
        capture_output=True,
        env=env,
        stdin=subprocess.DEVNULL,
        timeout=30,
    )
    assert result.returncode == 0
    assert result.stdout == ""
