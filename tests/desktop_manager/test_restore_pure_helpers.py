from __future__ import annotations

import re
from pathlib import Path

import pytest

from desktop_manager.models import LaunchEntry, RestoreRunLogger
from desktop_manager.restore_fallbacks import (
    _build_missing_path_note,
    _sanitize_notepad_stem,
    is_missing_path_error,
    normalize_missing_path_error_message,
)
from desktop_manager.restore_timing import StepTimer, new_terminal_restore_token, should_force_terminal_relaunch
from desktop_manager.window_reuse import describe_launch

# ---------------------------------------------------------------------------
# is_missing_path_error
# ---------------------------------------------------------------------------


def test_is_missing_path_error_returns_true_for_matching_message() -> None:
    err = ValueError("Launch entry `MyApp` resolved path does not exist: C:\\missing\\app.exe")
    assert is_missing_path_error(err) is True


def test_is_missing_path_error_returns_false_for_generic_error() -> None:
    err = RuntimeError("Something went wrong")
    assert is_missing_path_error(err) is False


def test_is_missing_path_error_requires_both_phrases() -> None:
    err_only_path = ValueError("resolved path does not exist: C:\\foo.exe")
    err_only_entry = ValueError("Launch entry `MyApp` failed to start")
    assert is_missing_path_error(err_only_path) is False
    assert is_missing_path_error(err_only_entry) is False


def test_is_missing_path_error_accepts_cwd_variant() -> None:
    err = ValueError("Launch entry `Term` resolved path does not exist: C:\\missing\\cwd")
    assert is_missing_path_error(err) is True


# ---------------------------------------------------------------------------
# normalize_missing_path_error_message
# ---------------------------------------------------------------------------


def test_normalize_missing_path_error_message_normalizes_path_field() -> None:
    msg = "Launch entry `MyApp` resolved path does not exist: C:\\missing\\app.exe"
    result = normalize_missing_path_error_message(msg)
    assert result == "Launch target resolved path does not exist: C:\\missing\\app.exe"


def test_normalize_missing_path_error_message_normalizes_cwd_field() -> None:
    msg = "Launch entry `Terminal` resolved cwd does not exist: C:\\missing\\dir"
    result = normalize_missing_path_error_message(msg)
    assert result == "Launch target resolved cwd does not exist: C:\\missing\\dir"


def test_normalize_missing_path_error_message_normalizes_arg_field() -> None:
    msg = "Launch entry `Editor` resolved arg does not exist: C:\\missing\\file.txt"
    result = normalize_missing_path_error_message(msg)
    assert result == "Launch target resolved arg does not exist: C:\\missing\\file.txt"


def test_normalize_missing_path_error_message_passthrough_unrecognized() -> None:
    msg = "Some other error message that does not match the pattern"
    result = normalize_missing_path_error_message(msg)
    assert result == msg


def test_normalize_missing_path_error_message_passthrough_empty() -> None:
    assert normalize_missing_path_error_message("") == ""


# ---------------------------------------------------------------------------
# _sanitize_notepad_stem
# ---------------------------------------------------------------------------


def test_sanitize_notepad_stem_keeps_alphanumeric_and_separators() -> None:
    assert _sanitize_notepad_stem("my-app_1") == "my-app_1"


def test_sanitize_notepad_stem_replaces_spaces_with_dashes() -> None:
    assert _sanitize_notepad_stem("My App Name") == "My-App-Name"


def test_sanitize_notepad_stem_strips_leading_and_trailing_dashes() -> None:
    assert _sanitize_notepad_stem("  Hello World  ") == "Hello-World"


def test_sanitize_notepad_stem_replaces_special_chars() -> None:
    result = _sanitize_notepad_stem("app: (v2.0)")
    assert result == "app---v2-0"


def test_sanitize_notepad_stem_returns_fallback_for_all_special_chars() -> None:
    assert _sanitize_notepad_stem("!!!") == "workspace-error"


def test_sanitize_notepad_stem_empty_string_returns_fallback() -> None:
    assert _sanitize_notepad_stem("") == "workspace-error"


# ---------------------------------------------------------------------------
# _build_missing_path_note
# ---------------------------------------------------------------------------


def test_build_missing_path_note_includes_error_and_entry_names() -> None:
    entries = [
        LaunchEntry(name="Chrome", type="app", path=r"C:\chrome.exe"),
        LaunchEntry(name="VS Code", type="app", path=r"C:\code.exe"),
    ]
    note = _build_missing_path_note("Some error occurred", entries)
    assert "Workspace restore fallback" in note
    assert "Some error occurred" in note
    assert "Affected windows:" in note
    assert "- Chrome" in note
    assert "- VS Code" in note


def test_build_missing_path_note_ends_with_newline() -> None:
    entries = [LaunchEntry(name="App", type="app", path=r"C:\app.exe")]
    note = _build_missing_path_note("error", entries)
    assert note.endswith("\n")


def test_build_missing_path_note_single_entry() -> None:
    entries = [LaunchEntry(name="Solo", type="app", path=r"C:\solo.exe")]
    note = _build_missing_path_note("path missing", entries)
    lines = note.splitlines()
    affected_idx = lines.index("Affected windows:")
    assert lines[affected_idx + 1] == "- Solo"


def test_build_missing_path_note_preserves_error_message_verbatim() -> None:
    error_msg = "Launch entry `My App` resolved cwd does not exist: C:\\missing"
    entries = [LaunchEntry(name="My App", type="app", path=r"C:\app.exe")]
    note = _build_missing_path_note(error_msg, entries)
    assert error_msg in note


# ---------------------------------------------------------------------------
# new_terminal_restore_token
# ---------------------------------------------------------------------------


def test_new_terminal_restore_token_contains_normalized_entry_name() -> None:
    entry = LaunchEntry(name="Workspace Terminal 1", type="terminal", shell="wt-pwsh")
    token = new_terminal_restore_token(entry)
    assert token.startswith("workspace-terminal-1-")


def test_new_terminal_restore_token_ends_with_hex_suffix() -> None:
    entry = LaunchEntry(name="My Terminal", type="terminal", shell="wt-pwsh")
    token = new_terminal_restore_token(entry)
    parts = token.rsplit("-", 1)
    assert len(parts) == 2
    assert len(parts[1]) == 8
    assert re.fullmatch(r"[0-9a-f]{8}", parts[1])


def test_new_terminal_restore_token_unique_per_call() -> None:
    entry = LaunchEntry(name="Terminal", type="terminal", shell="wt-pwsh")
    tokens = {new_terminal_restore_token(entry) for _ in range(10)}
    assert len(tokens) == 10


def test_new_terminal_restore_token_strips_special_chars_from_name() -> None:
    entry = LaunchEntry(name="My App: (v2)", type="terminal", shell="wt-pwsh")
    token = new_terminal_restore_token(entry)
    # name should be normalized: lowercase, special chars replaced by dashes
    prefix = token.rsplit("-", 1)[0]
    assert re.fullmatch(r"[a-z0-9\-]+", prefix)


def test_new_terminal_restore_token_fallback_for_empty_name() -> None:
    entry = LaunchEntry(name="!!!", type="terminal", shell="wt-pwsh")
    token = new_terminal_restore_token(entry)
    assert token.startswith("terminal-")


# ---------------------------------------------------------------------------
# should_force_terminal_relaunch
# ---------------------------------------------------------------------------


def test_should_force_terminal_relaunch_true_for_wt_pwsh() -> None:
    entry = LaunchEntry(name="Term", type="terminal", shell="wt-pwsh")
    assert should_force_terminal_relaunch(entry) is True


def test_should_force_terminal_relaunch_false_for_app() -> None:
    entry = LaunchEntry(name="Chrome", type="app", path=r"C:\chrome.exe")
    assert should_force_terminal_relaunch(entry) is False


def test_should_force_terminal_relaunch_false_for_terminal_without_wt_pwsh() -> None:
    entry = LaunchEntry(name="CMD", type="terminal", shell="cmd")
    assert should_force_terminal_relaunch(entry) is False


def test_should_force_terminal_relaunch_false_for_command_type() -> None:
    entry = LaunchEntry(name="Script", type="command", command="run.sh")
    assert should_force_terminal_relaunch(entry) is False


# ---------------------------------------------------------------------------
# describe_launch
# ---------------------------------------------------------------------------


def test_describe_launch_app_with_no_args() -> None:
    entry = LaunchEntry(name="Chrome", type="app", path=r"C:\chrome.exe")
    result = describe_launch(entry, {})
    assert result == r"app `Chrome` -> C:\chrome.exe"


def test_describe_launch_app_with_args() -> None:
    entry = LaunchEntry(name="Chrome", type="app", path=r"C:\chrome.exe", args=["--incognito", "--new-window"])
    result = describe_launch(entry, {})
    assert result == r"app `Chrome` -> C:\chrome.exe --incognito --new-window"


def test_describe_launch_app_substitutes_placeholders() -> None:
    entry = LaunchEntry(name="Editor", type="app", path=r"{workspace}\editor.exe")
    result = describe_launch(entry, {"workspace": r"C:\code\myproject"})
    assert result == r"app `Editor` -> C:\code\myproject\editor.exe"


def test_describe_launch_app_missing_path_shows_placeholder() -> None:
    entry = LaunchEntry(name="Broken", type="app", path=None)
    result = describe_launch(entry, {})
    assert "<missing path>" in result


def test_describe_launch_terminal_with_command_and_cwd() -> None:
    entry = LaunchEntry(
        name="Dev Terminal",
        type="terminal",
        command="opencode",
        cwd=r"C:\code\project",
        shell="wt-pwsh",
    )
    result = describe_launch(entry, {})
    assert result == r"terminal `Dev Terminal` -> opencode (cwd: C:\code\project)"


def test_describe_launch_terminal_substitutes_workspace_placeholder() -> None:
    entry = LaunchEntry(
        name="Workspace Terminal",
        type="terminal",
        command="opencode",
        cwd="{workspace}",
        shell="wt-pwsh",
    )
    result = describe_launch(entry, {"workspace": r"C:\code\shared-utilities"})
    assert result == r"terminal `Workspace Terminal` -> opencode (cwd: C:\code\shared-utilities)"


def test_describe_launch_command_type_with_cwd() -> None:
    entry = LaunchEntry(name="Build", type="command", command="make all", cwd=r"C:\project")
    result = describe_launch(entry, {})
    assert result == r"command `Build` -> make all (cwd: C:\project)"


def test_describe_launch_command_type_without_cwd() -> None:
    entry = LaunchEntry(name="Script", type="command", command="run.sh")
    result = describe_launch(entry, {})
    assert result == "command `Script` -> run.sh"


def test_describe_launch_command_type_missing_command() -> None:
    entry = LaunchEntry(name="Empty", type="command")
    result = describe_launch(entry, {})
    assert "<missing command>" in result


def test_describe_launch_app_with_substituted_arg() -> None:
    entry = LaunchEntry(
        name="VSCode",
        type="app",
        path=r"C:\code.exe",
        args=["{workspace}\\project.code-workspace"],
    )
    result = describe_launch(entry, {"workspace": r"C:\code\myproject"})
    assert r"C:\code\myproject\project.code-workspace" in result


@pytest.mark.parametrize(
    "entry_type,expected_prefix",
    [
        ("app", "app `"),
        ("terminal", "terminal `"),
        ("command", "command `"),
    ],
)
def test_describe_launch_prefix_matches_entry_type(entry_type: str, expected_prefix: str) -> None:
    if entry_type == "app":
        entry = LaunchEntry(name="Test", type=entry_type, path=r"C:\test.exe")
    else:
        entry = LaunchEntry(name="Test", type=entry_type, command="run")
    result = describe_launch(entry, {})
    assert result.startswith(expected_prefix)


def test_step_timer_flags_slow_steps_and_lists_every_step(tmp_path: Path) -> None:
    logger = RestoreRunLogger(tmp_path / "run.log", tmp_path / "latest.log")
    ticks = iter([0.0, 47.2, 50.0, 50.3, 60.0, 130.0])
    steps = StepTimer(logger, "test", clock=lambda: next(ticks))

    with steps.step("restore.settle_app_updates"):
        pass
    with steps.step("restore.plan"):
        pass
    with steps.step("restore.desktop", label="desktop 1 (Work)", slow_after=90):
        pass
    steps.log_summary()

    assert [line.split("] ", 1)[1] for line in logger.lines] == [
        "Slow step: settle_app_updates took 47.2s (over 15s)",
        "Step timings: settle_app_updates 47.2s (slow), plan 0.3s, desktop 1 (Work) 70.0s",
    ]
