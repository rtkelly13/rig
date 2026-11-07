from types import SimpleNamespace

from desktop_manager.launching import clean_launch_environment, launch_entry
from desktop_manager.models import LaunchEntry


def test_strips_inherited_agent_session_variables():
    environ = {
        "PATH": r"C:\Windows",
        "USERPROFILE": r"C:\Users\ryan",
        "NO_COLOR": "1",
        "CLAUDECODE": "1",
        "CLAUDE_PID": "26432",
        "CLAUDE_CODE_CHILD_SESSION": "1",
        "CLAUDE_CODE_SESSION_ID": "e4e34cc0",
        "CLAUDE_CONFIG_DIR": r"C:\Users\ryan\.claude",
    }

    cleaned = clean_launch_environment(environ)

    # The user's own Claude config survives; only per-session markers go.
    assert cleaned == {
        "PATH": r"C:\Windows",
        "USERPROFILE": r"C:\Users\ryan",
        "CLAUDE_CONFIG_DIR": r"C:\Users\ryan\.claude",
    }


def test_terminal_launch_does_not_inherit_no_color(monkeypatch, tmp_path):
    monkeypatch.setenv("NO_COLOR", "1")
    monkeypatch.setenv("CLAUDE_CODE_CHILD_SESSION", "1")
    monkeypatch.setattr("desktop_manager.launching.ensure_terminal_command_initialized", lambda _entry: None)
    monkeypatch.setattr("desktop_manager.launching.find_windows_terminal_executable", lambda: "wt.exe")
    captured: dict = {}

    def fake_popen(command, **kwargs):
        captured.update(kwargs)
        return SimpleNamespace(pid=1234)

    monkeypatch.setattr("desktop_manager.launching.subprocess.Popen", fake_popen)
    entry = LaunchEntry(
        name="Workspace Terminal 1", type="terminal", shell="wt-pwsh", cwd=str(tmp_path), command="claude"
    )

    launch_entry(entry, {})

    assert "NO_COLOR" not in captured["env"]
    assert "CLAUDE_CODE_CHILD_SESSION" not in captured["env"]
    assert "PATH" in captured["env"]
