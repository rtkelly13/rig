import base64
import sys
import time
from pathlib import Path
from types import SimpleNamespace

import pytest

from desktop_manager.models import PlacementOutcome, WindowMatch
from desktop_manager.restore_timing import _should_poll_for_launch_readiness, launch_settle_delay_seconds
from desktop_manager.windows_workspace import (
    LaunchEntry,
    LiveWindow,
    PlacementLayout,
    PlacementSpec,
    Rect,
    WindowSnapshot,
    adjust_rect_for_window_frame,
    apply_window_placement,
    build_terminal_command,
    capture_current_desktop_plan,
    choose_best_window_handle,
    current_desktop_status,
    current_window_handles_to_preserve,
    launch_entry,
    monitor_work_area_by_number,
    placement_rect_for,
    primary_monitor_work_area,
)


def test_capture_current_desktop_plan_builds_launchers_from_current_windows(monkeypatch, tmp_path: Path):
    windows = [WindowSnapshot(title="Open Terminal", process_name="WindowsTerminal.exe", exe_path=r"C:\wt.exe")]

    monkeypatch.setattr("desktop_manager.win32.inventory.list_current_desktop_windows", lambda: windows)

    desktop = capture_current_desktop_plan(1, tmp_path, desktop_name="Work")

    assert desktop.name == "Work"
    assert desktop.captured_windows == windows
    assert [entry.name for entry in desktop.launch] == ["Workspace Terminal 1"]


def test_current_desktop_status_uses_current_windows(monkeypatch):
    windows = [WindowSnapshot(title="Slack", process_name="slack.exe")]

    monkeypatch.setattr("desktop_manager.win32.inventory.list_current_desktop_windows", lambda: windows)

    desktop = current_desktop_status(desktop_name="Admin")

    assert desktop.name == "Admin"
    assert desktop.captured_windows == windows


def test_ensure_current_desktop_number_emits_virtual_desktop_change_event(monkeypatch):
    events: list[str] = []
    current = {"desktop": 1}

    monkeypatch.setattr("desktop_manager.win32.ensure_windows", lambda: None)
    monkeypatch.setattr(
        "desktop_manager.win32.desktops.send_hotkey",
        lambda direction: current.__setitem__("desktop", 2 if direction == "right" else 1),
    )
    monkeypatch.setattr("desktop_manager.win32.desktops.get_current_desktop_number", lambda: current["desktop"])
    monkeypatch.setattr("time.sleep", lambda _seconds: None)

    from desktop_manager.windows_workspace import (
        clear_virtual_desktop_event_callback,
        ensure_current_desktop_number,
        set_virtual_desktop_event_callback,
    )

    set_virtual_desktop_event_callback(events.append)
    try:
        assert ensure_current_desktop_number(2, settle_seconds=0.0, max_attempts=0) is True
    finally:
        clear_virtual_desktop_event_callback()

    assert events == ["Virtual desktop changed: 1 -> 2 (ensure_current_desktop_number)"]


def test_ensure_current_desktop_number_anchors_via_hotkeys_when_direct_navigation_stalls(monkeypatch):
    current = {"desktop": 1}
    go_to_calls: list[int] = []
    switch_calls: list[int] = []
    right_calls: list[int] = []
    hotkey_calls: list[str] = []

    monkeypatch.setattr("desktop_manager.win32.ensure_windows", lambda: None)
    monkeypatch.setattr("desktop_manager.win32.desktops.get_current_desktop_number", lambda: current["desktop"])
    monkeypatch.setattr(
        "desktop_manager.win32.desktops.go_to_desktop_number", lambda desktop_index: go_to_calls.append(desktop_index)
    )
    monkeypatch.setattr(
        "desktop_manager.win32.desktops._send_virtual_desktop_hotkey",
        lambda direction, **_kwargs: hotkey_calls.append(direction),
    )
    monkeypatch.setattr(
        "desktop_manager.win32.desktops.switch_leftmost",
        lambda steps: switch_calls.append(steps) or current.__setitem__("desktop", 1),
    )
    monkeypatch.setattr(
        "desktop_manager.win32.desktops.move_right",
        lambda steps: right_calls.append(steps) or current.__setitem__("desktop", 1 + steps),
    )
    monkeypatch.setattr("time.sleep", lambda _seconds: None)

    from desktop_manager.windows_workspace import ensure_current_desktop_number

    assert ensure_current_desktop_number(3, settle_seconds=0.1, max_attempts=1) is True
    assert go_to_calls == [3]
    assert hotkey_calls == ["right", "right"]
    assert switch_calls == [5]
    assert right_calls == [2]


def test_ensure_current_desktop_number_fails_when_current_desktop_stays_unknown(monkeypatch):
    switch_calls: list[int] = []
    right_calls: list[int] = []

    monkeypatch.setattr("desktop_manager.win32.ensure_windows", lambda: None)
    monkeypatch.setattr("desktop_manager.win32.desktops.get_current_desktop_number", lambda: None)
    monkeypatch.setattr("desktop_manager.win32.desktops.switch_leftmost", switch_calls.append)
    monkeypatch.setattr("desktop_manager.win32.desktops.move_right", right_calls.append)
    monkeypatch.setattr("time.sleep", lambda _seconds: None)

    from desktop_manager.windows_workspace import ensure_current_desktop_number

    assert ensure_current_desktop_number(2, settle_seconds=0.1, max_attempts=0) is False
    assert switch_calls == [4]
    assert right_calls == [1]


def test_capture_workspace_plan_uses_cross_desktop_inventory(monkeypatch, tmp_path: Path):
    from desktop_manager.windows_workspace import capture_workspace_plan

    monkeypatch.setattr("desktop_manager.win32.ensure_windows", lambda: None)
    monkeypatch.setattr("desktop_manager.win32.desktops.get_virtual_desktop_count", lambda: 2)
    monkeypatch.setattr(
        "desktop_manager.win32.inventory.inventory_reusable_windows",
        lambda excluded_hwnds=None: [
            LiveWindow(
                hwnd=10,
                title="Open Terminal",
                process_name="WindowsTerminal.exe",
                exe_path=r"C:\wt.exe",
                desktop_number=1,
                screen_number=1,
            ),
            LiveWindow(
                hwnd=20,
                title="Slack",
                process_name="slack.exe",
                exe_path=r"C:\Slack.exe",
                desktop_number=2,
                screen_number=3,
            ),
        ],
    )

    plan = capture_workspace_plan(workspace=tmp_path)

    assert [desktop.name for desktop in plan.desktops] == ["Desktop 1", "Desktop 2"]
    assert [entry.name for entry in plan.desktops[0].launch] == ["Workspace Terminal 1"]
    assert [entry.name for entry in plan.desktops[1].launch] == ["Slack"]
    assert [window.title for window in plan.desktops[1].captured_windows] == ["Slack"]


def test_apply_window_placement_uses_title_lookup(monkeypatch):
    calls: list[tuple[int, Rect]] = []

    def fake_wait_for_window_by_title(
        title: str,
        timeout_seconds: float = 10.0,
        *,
        exact: bool = False,
        current_desktop_only: bool = True,
        excluded_hwnds: set[int] | None = None,
        diagnostic_callback=None,
    ) -> int:
        del title, timeout_seconds, exact, current_desktop_only, excluded_hwnds, diagnostic_callback
        return 42

    monkeypatch.setattr("desktop_manager.win32.ensure_windows", lambda: None)
    monkeypatch.setattr("desktop_manager.win32.entry_windows.wait_for_window_by_title", fake_wait_for_window_by_title)
    monkeypatch.setattr(
        "desktop_manager.win32.entry_windows.placement_rect_for",
        lambda _placement: Rect(left=0, top=0, width=100, height=200),
    )
    monkeypatch.setattr("desktop_manager.win32.geometry.move_window", lambda hwnd, rect: calls.append((hwnd, rect)))

    entry = LaunchEntry(
        name="Workspace Terminal 1",
        type="terminal",
        shell="pwsh",
        window_title="Workspace Terminal 1",
        placement=PlacementSpec(screen=2, layout=PlacementLayout(mode="thirds", slot=1)),
    )

    assert apply_window_placement(entry) == PlacementOutcome(placed=True)
    assert calls == [(42, Rect(left=0, top=0, width=100, height=200))]


def test_apply_window_placement_moves_window_to_target_desktop(monkeypatch):
    calls: list[tuple[str, int, int]] = []

    monkeypatch.setattr("desktop_manager.win32.ensure_windows", lambda: None)
    monkeypatch.setattr("desktop_manager.win32.entry_windows.wait_for_window_by_process", lambda *_args, **_kwargs: 77)
    monkeypatch.setattr("desktop_manager.win32.desktops.get_window_desktop_number", lambda _hwnd: 1)
    monkeypatch.setattr(
        "desktop_manager.win32.desktops.move_window_to_desktop_number",
        lambda hwnd, desktop: calls.append(("desktop", hwnd, desktop)),
    )
    monkeypatch.setattr(
        "desktop_manager.win32.entry_windows.placement_rect_for",
        lambda _placement: Rect(left=0, top=0, width=50, height=60),
    )
    monkeypatch.setattr(
        "desktop_manager.win32.geometry.move_window", lambda hwnd, _rect: calls.append(("rect", hwnd, 0))
    )
    monkeypatch.setattr("time.sleep", lambda _seconds: None)

    entry = LaunchEntry(
        name="Slack",
        type="app",
        placement=PlacementSpec(screen=1, layout=PlacementLayout(mode="thirds", slot=1)),
        window_process_name="slack.exe",
    )

    assert apply_window_placement(entry, target_desktop_number=3) == PlacementOutcome(
        placed=True, moved_to_target_desktop=True
    )
    assert calls == [("desktop", 77, 3), ("rect", 77, 0)]


def test_adjust_rect_for_window_frame_compensates_extended_frame(monkeypatch):
    monkeypatch.setattr(
        "desktop_manager.win32.geometry.window_bounds", lambda _hwnd: Rect(left=100, top=100, width=800, height=600)
    )
    monkeypatch.setattr(
        "desktop_manager.win32.geometry.extended_frame_bounds",
        lambda _hwnd: Rect(left=108, top=108, width=784, height=584),
    )

    adjusted = adjust_rect_for_window_frame(10, Rect(left=0, top=0, width=1920, height=1080))

    assert adjusted == Rect(left=-8, top=-8, width=1936, height=1096)


def test_apply_window_placement_falls_back_to_process_lookup(monkeypatch):
    calls: list[tuple[int, Rect]] = []

    def fake_wait_for_window_by_process(
        process_name: str,
        timeout_seconds: float = 10.0,
        *,
        target_placement: PlacementSpec | None = None,
        current_desktop_only: bool = True,
        target_desktop_number: int | None = None,
        excluded_hwnds: set[int] | None = None,
        single_instance: bool = False,
        alive_process_grace_seconds: float = 0.0,
        diagnostic_callback=None,
    ) -> int:
        del (
            process_name,
            timeout_seconds,
            target_placement,
            current_desktop_only,
            target_desktop_number,
            excluded_hwnds,
            single_instance,
            alive_process_grace_seconds,
            diagnostic_callback,
        )
        return 99

    monkeypatch.setattr("desktop_manager.win32.ensure_windows", lambda: None)
    monkeypatch.setattr("desktop_manager.win32.entry_windows.wait_for_window_by_title", lambda *_args, **_kwargs: None)
    monkeypatch.setattr(
        "desktop_manager.win32.entry_windows.wait_for_window_by_process", fake_wait_for_window_by_process
    )
    monkeypatch.setattr(
        "desktop_manager.win32.entry_windows.placement_rect_for",
        lambda _placement: Rect(left=1, top=2, width=3, height=4),
    )
    monkeypatch.setattr("desktop_manager.win32.geometry.move_window", lambda hwnd, rect: calls.append((hwnd, rect)))

    entry = LaunchEntry(
        name="Slack",
        type="app",
        placement=PlacementSpec(screen=1, layout=PlacementLayout(mode="thirds", slot=1)),
        window_process_name="slack.exe",
    )

    assert apply_window_placement(entry) == PlacementOutcome(placed=True)
    assert calls == [(99, Rect(left=1, top=2, width=3, height=4))]


@pytest.mark.windows
def test_apply_window_placement_prefers_fresh_process_window(monkeypatch):
    calls: list[tuple[int, Rect]] = []

    monkeypatch.setattr("desktop_manager.win32.ensure_windows", lambda: None)
    monkeypatch.setattr("desktop_manager.win32.entry_windows.wait_for_window_by_title", lambda *_args, **_kwargs: None)
    monkeypatch.setattr(
        "desktop_manager.win32.inventory.collect_windows_matching_process", lambda *_args, **_kwargs: [10, 20]
    )
    monkeypatch.setattr(
        "desktop_manager.win32.entry_windows.placement_rect_for",
        lambda _placement: Rect(left=1, top=2, width=3, height=4),
    )
    monkeypatch.setattr("desktop_manager.win32.geometry.move_window", lambda hwnd, rect: calls.append((hwnd, rect)))

    entry = LaunchEntry(
        name="Fork",
        type="app",
        placement=PlacementSpec(screen=1, layout=PlacementLayout(mode="thirds", slot=1)),
        window_process_name="Fork.exe",
    )

    assert apply_window_placement(entry, excluded_hwnds={10}) == PlacementOutcome(placed=True)
    assert calls == [(20, Rect(left=1, top=2, width=3, height=4))]


def test_current_window_handles_to_preserve_uses_top_level_windows(monkeypatch):
    fake_win32gui = SimpleNamespace(
        GetForegroundWindow=lambda: 100,
        GetAncestor=lambda hwnd, _flag: {100: 200, 300: 400}.get(hwnd, hwnd),
    )

    monkeypatch.setattr("desktop_manager.win32.ensure_windows", lambda: None)
    monkeypatch.setattr(
        "desktop_manager.win32.dlls.system_dll", lambda _name: SimpleNamespace(GetConsoleWindow=lambda: 300)
    )
    monkeypatch.setitem(sys.modules, "win32gui", fake_win32gui)

    assert current_window_handles_to_preserve() == {200, 400}


def test_launch_settle_delay_seconds_uses_minimum_for_wt_pwsh_terminals():
    terminal_entry = LaunchEntry(
        name="Workspace Terminal 1",
        type="terminal",
        command="opencode",
        cwd="{workspace}",
        shell="wt-pwsh",
        delay_ms=500,
    )
    app_entry = LaunchEntry(name="Fork", type="app", path=r"C:\Fork.exe", delay_ms=500)

    assert launch_settle_delay_seconds(terminal_entry) == 0.5
    assert launch_settle_delay_seconds(app_entry) == 0.5


def test_should_poll_for_launch_readiness_only_for_wt_pwsh_terminals():
    terminal_entry = LaunchEntry(
        name="Workspace Terminal 1",
        type="terminal",
        command="opencode",
        cwd="{workspace}",
        shell="wt-pwsh",
    )
    app_entry = LaunchEntry(name="Fork", type="app", path=r"C:\Fork.exe")

    assert _should_poll_for_launch_readiness(terminal_entry) is True
    assert _should_poll_for_launch_readiness(app_entry) is False


def test_build_terminal_command_includes_restore_metadata(tmp_path: Path):
    entry = LaunchEntry(
        name="Workspace Terminal 1",
        type="terminal",
        command="opencode",
        cwd="{workspace}",
        shell="wt-pwsh",
    )

    command = build_terminal_command(entry, {"workspace": str(tmp_path)}, restore_token="token-1234")

    encoded_command = command[-1]
    decoded_command = base64.b64decode(encoded_command).decode("utf-16le")
    assert "$env:OPENCODE_RESTORE_TOKEN = 'token-1234'" in decoded_command
    assert "$env:OPENCODE_RESTORE_ENTRY = 'Workspace Terminal 1'" in decoded_command
    assert "& { opencode }" in decoded_command


@pytest.mark.windows
def test_terminal_command_name_handles_bare_and_pathed_commands():
    from desktop_manager.terminal_init import terminal_command_name

    assert terminal_command_name("opencode") == "opencode"
    assert terminal_command_name("copilot --help") == "copilot"
    assert terminal_command_name(r'"C:\Tools\claude.exe" --version') == "claude.exe"


def test_terminal_command_initializer_matches_known_tools():
    from desktop_manager.terminal_init import terminal_command_initializer_for_entry

    assert (
        terminal_command_initializer_for_entry(
            LaunchEntry(name="OpenCode", type="terminal", command="opencode", cwd="{workspace}", shell="wt-pwsh")
        )
        is not None
    )
    assert (
        terminal_command_initializer_for_entry(
            LaunchEntry(name="Claude", type="terminal", command="claude", cwd="{workspace}", shell="wt-pwsh")
        )
        is not None
    )
    assert (
        terminal_command_initializer_for_entry(
            LaunchEntry(name="Copilot", type="terminal", command="copilot", cwd="{workspace}", shell="wt-pwsh")
        )
        is not None
    )
    assert (
        terminal_command_initializer_for_entry(
            LaunchEntry(name="Pwsh", type="terminal", command="pwsh", cwd="{workspace}", shell="wt-pwsh")
        )
        is None
    )


def test_build_terminal_command_leaves_non_self_updating_command_unchanged(tmp_path: Path):
    entry = LaunchEntry(
        name="Workspace Terminal 1",
        type="terminal",
        command="pwsh",
        cwd="{workspace}",
        shell="wt-pwsh",
    )

    command = build_terminal_command(entry, {"workspace": str(tmp_path)})

    encoded_command = command[-1]
    decoded_command = base64.b64decode(encoded_command).decode("utf-16le")
    assert decoded_command == "pwsh"


def test_build_terminal_command_wraps_self_updating_command_in_wait_and_retry(tmp_path: Path):
    entry = LaunchEntry(
        name="Workspace Terminal 1",
        type="terminal",
        command="claude --continue",
        cwd="{workspace}",
        shell="wt-pwsh",
    )

    command = build_terminal_command(entry, {"workspace": str(tmp_path)})

    encoded_command = command[-1]
    decoded_command = base64.b64decode(encoded_command).decode("utf-16le")
    assert decoded_command.startswith("$dmName = 'claude'; ")
    assert "Test-DmReady" in decoded_command
    assert "& { claude --continue }" in decoded_command
    assert "$dmAttempt -le 6" in decoded_command


@pytest.mark.windows
def test_launch_entry_expands_home_directory_in_app_args(monkeypatch, tmp_path: Path):
    from pathlib import Path as PathlibPath

    from desktop_manager.launching import launch_entry

    vscode_path = tmp_path / "Code.exe"
    vscode_path.write_text("", encoding="utf-8")

    popen_calls: list[list[str]] = []

    expected_target = str(PathlibPath(r"~\Documents\prs-to-review.md").expanduser())
    monkeypatch.setattr("desktop_manager.launching._validate_launch_path_exists", lambda *_args, **_kwargs: None)
    monkeypatch.setattr(
        "desktop_manager.launching.subprocess.Popen",
        lambda args, **_kwargs: popen_calls.append(args) or SimpleNamespace(pid=1234),
    )

    entry = LaunchEntry(
        name="VS Code PRs To Review",
        type="app",
        path=str(vscode_path),
        args=[r"~\Documents\prs-to-review.md"],
    )

    launch_entry(entry, {})

    assert popen_calls == [[str(vscode_path), expected_target]]


def test_launch_entry_passes_command_to_cmd_without_escaping_its_quotes(monkeypatch, tmp_path: Path):
    """A `command` entry must reach cmd.exe as one verbatim command line.

    Passing ["cmd.exe", "/c", command] runs it through list2cmdline, which
    backslash-escapes the command's own double quotes. cmd.exe then received
    \\"C:\\Program Files\\Microsoft VS Code\\Code.exe\\" and answered "is not
    recognized as an internal or external command". stdout/stderr are discarded and
    cmd.exe still exits 1, so the entry silently never opened a window.
    """
    from desktop_manager.launching import launch_entry

    popen_calls: list[object] = []
    monkeypatch.setattr("desktop_manager.launching._validate_launch_path_exists", lambda *_args, **_kwargs: None)
    monkeypatch.setattr(
        "desktop_manager.launching.subprocess.Popen",
        lambda args, **_kwargs: popen_calls.append(args) or SimpleNamespace(pid=1234),
    )

    command = (
        "uv run --no-sync desktop-manager workspace vscode && "
        '"C:\\Program Files\\Code.exe" --new-window "w.code-workspace"'
    )
    entry = LaunchEntry(name="VS Code Workspace", type="command", command=command, cwd=str(tmp_path))

    launch_entry(entry, {})

    assert popen_calls == [f"cmd.exe /c {command}"]
    assert '\\"' not in str(popen_calls[0])


def test_terminal_command_initializer_runs_opencode_upgrade_once(monkeypatch, tmp_path: Path):
    runs: list[tuple[list[str], dict[str, str]]] = []

    monkeypatch.setattr("desktop_manager.terminal_init._TERMINAL_COMMAND_INIT_RESULTS", {})
    monkeypatch.setattr(
        "desktop_manager.terminal_init._resolve_opencode_update_command",
        lambda: [r"C:\baseline\opencode.exe", "upgrade"],
    )
    monkeypatch.setattr(
        "desktop_manager.terminal_init.subprocess.run",
        lambda args, **kwargs: runs.append((args, kwargs["env"])) or SimpleNamespace(returncode=0, stderr=""),
    )
    monkeypatch.setattr("desktop_manager.terminal_init._OPENCODE_INIT_LOCK_PATH", tmp_path / "opencode-init.lock")

    from desktop_manager.terminal_init import _ensure_terminal_command_initializer, _terminal_command_initializers

    initializer = _terminal_command_initializers()[0]
    _ensure_terminal_command_initializer(initializer)
    _ensure_terminal_command_initializer(initializer)

    assert len(runs) == 1
    assert runs[0][0] == [r"C:\baseline\opencode.exe", "upgrade"]


def test_terminal_command_initializer_runs_claude_update_once(monkeypatch, tmp_path: Path):
    runs: list[list[str]] = []

    monkeypatch.setattr("desktop_manager.terminal_init._TERMINAL_COMMAND_INIT_RESULTS", {})
    monkeypatch.setattr(
        "desktop_manager.terminal_init._resolve_claude_update_command", lambda: [r"C:\npm\claude.exe", "update"]
    )
    monkeypatch.setattr(
        "desktop_manager.terminal_init.subprocess.run",
        lambda args, **kwargs: runs.append(args) or SimpleNamespace(returncode=0, stderr=""),
    )
    monkeypatch.setattr("desktop_manager.terminal_init._CLAUDE_INIT_LOCK_PATH", tmp_path / "claude-init.lock")

    from desktop_manager.terminal_init import _ensure_terminal_command_initializer, _terminal_command_initializers

    initializer = next(i for i in _terminal_command_initializers() if "claude" in i.command_names)
    _ensure_terminal_command_initializer(initializer)
    _ensure_terminal_command_initializer(initializer)

    assert len(runs) == 1
    assert runs[0] == [r"C:\npm\claude.exe", "update"]


def test_resolve_claude_update_command_prefers_npm_binary_behind_cmd_shim(monkeypatch, tmp_path: Path):
    from desktop_manager.terminal_init import _resolve_claude_update_command

    npm_binary = tmp_path / "node_modules" / "@anthropic-ai" / "claude-code" / "bin" / "claude.exe"
    npm_binary.parent.mkdir(parents=True)
    npm_binary.write_bytes(b"")
    monkeypatch.setattr(
        "desktop_manager.terminal_init.resolve_executable_on_path",
        lambda name: {"claude.cmd": str(tmp_path / "claude.cmd"), "claude.exe": r"C:\native\claude.exe"}.get(name),
    )

    assert _resolve_claude_update_command() == [str(npm_binary.resolve()), "update"]


def test_resolve_claude_update_command_falls_back_to_native_install(monkeypatch, tmp_path: Path):
    from desktop_manager.terminal_init import _resolve_claude_update_command

    monkeypatch.setattr(
        "desktop_manager.terminal_init.resolve_executable_on_path",
        lambda name: {"claude.cmd": str(tmp_path / "claude.cmd"), "claude.exe": r"C:\native\claude.exe"}.get(name),
    )

    assert _resolve_claude_update_command() == [r"C:\native\claude.exe", "update"]


def test_resolve_update_commands_for_supported_tools(monkeypatch):
    from desktop_manager.terminal_init import _resolve_opencode_update_command, _resolve_path_update_command

    monkeypatch.setattr(
        "desktop_manager.terminal_init._resolve_opencode_baseline_binary", lambda: r"C:\baseline\opencode.exe"
    )
    monkeypatch.setattr(
        "desktop_manager.terminal_init.resolve_executable_on_path",
        lambda name: {
            "claude": r"C:\Tools\claude.exe",
            "copilot": r"C:\Tools\copilot.exe",
        }.get(name),
    )

    assert _resolve_opencode_update_command() == [r"C:\baseline\opencode.exe", "upgrade"]
    assert _resolve_path_update_command("claude", "update") == [r"C:\Tools\claude.exe", "update"]
    assert _resolve_path_update_command("copilot", "update") == [r"C:\Tools\copilot.exe", "update"]


def test_launch_entry_runs_terminal_initializer_once_before_multiple_terminal_launches(monkeypatch, tmp_path: Path):
    entry = LaunchEntry(
        name="Workspace Terminal 1",
        type="terminal",
        command="opencode",
        cwd="{workspace}",
        shell="wt-pwsh",
    )
    init_runs: list[list[str]] = []
    popen_commands: list[list[str]] = []

    monkeypatch.setattr("desktop_manager.terminal_init._TERMINAL_COMMAND_INIT_RESULTS", {})
    monkeypatch.setattr("desktop_manager.terminal_init._TERMINAL_COMMAND_INIT_THREADS", {})
    monkeypatch.setattr("desktop_manager.terminal_init._OPENCODE_INIT_LOCK_PATH", tmp_path / "opencode-init.lock")
    monkeypatch.setattr(
        "desktop_manager.terminal_init._resolve_opencode_update_command",
        lambda: [r"C:\baseline\opencode.exe", "upgrade"],
    )
    monkeypatch.setattr(
        "desktop_manager.terminal_init.subprocess.run",
        lambda args, **_kwargs: init_runs.append(args) or SimpleNamespace(returncode=0, stderr=""),
    )
    monkeypatch.setattr(
        "desktop_manager.launching.subprocess.Popen",
        lambda command, **_kwargs: popen_commands.append(command) or SimpleNamespace(pid=1234),
    )
    monkeypatch.setattr("desktop_manager.launching.find_windows_terminal_executable", lambda: "wt.exe")

    launch_entry(entry, {"workspace": str(tmp_path)})
    launch_entry(entry, {"workspace": str(tmp_path)})

    assert init_runs == [[r"C:\baseline\opencode.exe", "upgrade"]]
    assert len(popen_commands) == 2


def test_wait_for_window_for_entry_allows_terminal_process_fallback(monkeypatch):
    from desktop_manager.windows_workspace import wait_for_window_for_entry

    entry = LaunchEntry(
        name="Workspace Terminal 1",
        type="terminal",
        command="opencode",
        cwd="{workspace}",
        shell="wt-pwsh",
        window_title="Workspace Terminal 1",
        window_process_name="WindowsTerminal.exe",
    )

    monkeypatch.setattr("desktop_manager.win32.entry_windows.wait_for_window_by_title", lambda *_args, **_kwargs: None)
    monkeypatch.setattr("desktop_manager.win32.entry_windows.wait_for_window_by_process", lambda *_args, **_kwargs: 77)

    assert wait_for_window_for_entry(entry, allow_process_fallback_for_terminals=True) == WindowMatch(hwnd=77)
    assert wait_for_window_for_entry(entry, allow_process_fallback_for_terminals=False) == WindowMatch(hwnd=None)


def test_wait_for_window_by_process_skips_excluded_matches_until_fresh_handle(monkeypatch):
    from desktop_manager.windows_workspace import wait_for_window_by_process

    matches = iter([[101], [101], [101, 202]])

    monkeypatch.setattr("desktop_manager.win32.ensure_windows", lambda: None)
    monkeypatch.setattr(
        "desktop_manager.win32.inventory.collect_windows_matching_process", lambda *_args, **_kwargs: next(matches)
    )
    monkeypatch.setattr(
        "desktop_manager.win32.entry_windows.choose_best_window_handle", lambda handles, *_args, **_kwargs: handles[-1]
    )
    monkeypatch.setattr("time.sleep", lambda _seconds: None)

    assert wait_for_window_by_process("claude.exe", timeout_seconds=1.0, excluded_hwnds={101}) == 202


def test_wait_for_window_title_change_returns_updated_title(monkeypatch):
    from desktop_manager.windows_workspace import wait_for_window_title_change

    titles = iter(["Workspace Terminal 2", "Workspace Terminal 2", "opencode - workspace"])

    monkeypatch.setattr("desktop_manager.win32.ensure_windows", lambda: None)
    monkeypatch.setattr("desktop_manager.win32.geometry.window_title", lambda _hwnd: next(titles))
    monkeypatch.setattr("time.sleep", lambda _seconds: None)

    assert wait_for_window_title_change(123, "Workspace Terminal 2", timeout_seconds=1.0) == "opencode - workspace"


def test_choose_best_window_handle_prefers_target_overlap(monkeypatch):
    monkeypatch.setattr(
        "desktop_manager.win32.geometry.window_bounds",
        lambda hwnd: {10: Rect(left=0, top=0, width=300, height=300), 20: Rect(left=300, top=0, width=300, height=300)}[
            hwnd
        ],
    )
    monkeypatch.setattr(
        "desktop_manager.win32.entry_windows.placement_rect_for",
        lambda _placement: Rect(left=300, top=0, width=300, height=300),
    )

    chosen = choose_best_window_handle([10, 20], PlacementSpec(screen=1, layout=PlacementLayout(mode="full", slot=1)))

    assert chosen == 20


def test_placement_rect_for_terminal_layout(monkeypatch):
    monkeypatch.setattr("desktop_manager.win32.ensure_windows", lambda: None)
    monkeypatch.setattr(
        "desktop_manager.win32.geometry.monitor_work_area_by_number",
        lambda *_args: Rect(left=0, top=0, width=1920, height=1080),
    )

    assert placement_rect_for(PlacementSpec(screen=2, layout=PlacementLayout(mode="thirds", slot=1))) == Rect(
        left=0, top=0, width=640, height=1080
    )
    assert placement_rect_for(
        PlacementSpec(
            screen=2,
            layout=PlacementLayout(mode="thirds", slot=2),
            subdivide=PlacementLayout(mode="halves", slot=1, orientation="horizontal"),
        )
    ) == Rect(left=640, top=0, width=640, height=540)
    assert placement_rect_for(
        PlacementSpec(
            screen=2,
            layout=PlacementLayout(mode="thirds", slot=2),
            subdivide=PlacementLayout(mode="halves", slot=2, orientation="horizontal"),
        )
    ) == Rect(left=640, top=540, width=640, height=540)
    assert placement_rect_for(PlacementSpec(screen=2, layout=PlacementLayout(mode="thirds", slot=3))) == Rect(
        left=1280, top=0, width=640, height=1080
    )
    assert placement_rect_for(PlacementSpec(screen=3, layout=PlacementLayout(mode="quarters", slot=3))) == Rect(
        left=960, top=540, width=960, height=540
    )
    assert placement_rect_for(
        PlacementSpec(screen=2, layout=PlacementLayout(mode="halves", slot=1, orientation="vertical"))
    ) == Rect(left=0, top=0, width=960, height=1080)
    assert placement_rect_for(
        PlacementSpec(
            screen=1,
            layout=PlacementLayout(mode="thirds", slot=1),
            subdivide=PlacementLayout(mode="halves", slot=2, orientation="horizontal"),
        )
    ) == Rect(left=0, top=540, width=640, height=540)


def test_monitor_work_area_by_number_returns_sorted_screen(monkeypatch):
    monkeypatch.setattr(
        "desktop_manager.win32.geometry.monitor_work_areas",
        lambda: [
            Rect(left=-1600, top=0, width=1600, height=900),
            Rect(left=0, top=0, width=1920, height=1080),
            Rect(left=1920, top=0, width=1920, height=1080),
        ],
    )

    assert monitor_work_area_by_number(1) == Rect(left=-1600, top=0, width=1600, height=900)
    assert monitor_work_area_by_number(2) == Rect(left=0, top=0, width=1920, height=1080)
    assert monitor_work_area_by_number(3) == Rect(left=1920, top=0, width=1920, height=1080)


def test_primary_monitor_work_area_uses_monitor_work_rect(monkeypatch):
    monkeypatch.setattr("desktop_manager.win32.ensure_windows", lambda: None)

    fake_win32api = type(
        "FakeWin32Api",
        (),
        {
            "MonitorFromPoint": staticmethod(lambda *_args: "monitor-1"),
            "GetMonitorInfo": staticmethod(
                lambda *_args: {"Monitor": (0, 0, 2020, 1180), "Work": (100, 50, 2020, 1130)}
            ),
        },
    )
    fake_win32con = type("FakeWin32Con", (), {"MONITOR_DEFAULTTOPRIMARY": 1})

    monkeypatch.setitem(sys.modules, "win32api", fake_win32api)
    monkeypatch.setitem(sys.modules, "win32con", fake_win32con)

    assert primary_monitor_work_area() == Rect(left=100, top=50, width=1920, height=1080)


def test_rename_desktop_returns_false_when_pyvda_unavailable(monkeypatch):
    """A missing/failed pyvda import must never abort a restore (returns False).

    ``rename_desktop`` runs on every restore for desktop naming. Its pyvda import
    lives inside the try/except so hosts without the win32 desktop layer (e.g. the
    Linux CI runner, or a machine where the import fails) degrade to "not renamed"
    instead of raising and aborting the whole restore.
    """
    from desktop_manager import win32
    from desktop_manager.win32 import desktops

    monkeypatch.setattr(win32, "ensure_windows", lambda: None)
    # Poison the import so `import pyvda` raises ImportError even where it exists.
    monkeypatch.setitem(sys.modules, "pyvda", None)

    assert desktops.rename_desktop(1, "Review") is False


def test_wait_for_window_by_title_matches_substrings_for_entries(monkeypatch):
    """Configured `window_title` values are anchors, not whole titles.

    Every bundle title (e.g. "(Workspace)", "prs-to-review.md") is a fragment of the
    real window title, so exact matching made each entry burn its whole timeout before
    falling back to process polling. `wait_for_window_for_entry` polls with
    `exact=False` so the anchor matches immediately.
    """
    from desktop_manager import win32
    from desktop_manager.win32 import entry_windows

    recorded: dict[str, object] = {}

    def fake_wait_for_window_by_title(title: str, timeout_seconds: float = 10.0, **kwargs):
        recorded["title"] = title
        recorded["exact"] = kwargs.get("exact")
        recorded["excluded_hwnds"] = kwargs.get("excluded_hwnds")
        return 4242

    monkeypatch.setattr(win32, "ensure_windows", lambda: None)
    monkeypatch.setattr(entry_windows, "wait_for_window_by_title", fake_wait_for_window_by_title)

    entry = LaunchEntry(
        name="VS Code Workspace",
        type="app",
        path=r"C:\Code.exe",
        window_title="(Workspace)",
        window_process_name="Code.exe",
    )

    match = entry_windows.wait_for_window_for_entry(entry, excluded_hwnds={131720})

    assert match == WindowMatch(hwnd=4242)
    assert recorded["exact"] is False
    assert recorded["excluded_hwnds"] == {131720}


def test_wait_for_window_by_title_skips_excluded_hwnds(monkeypatch):
    """Substring anchors are shared between sibling entries, so exclusions must apply.

    The three Warp entries all anchor on "workspace" and all render as
    "pwsh in workspace". Without honouring the caller exclusion set, the second entry
    would adopt the window the first one just placed and leave a duplicate unplaced.
    """
    from desktop_manager import win32
    from desktop_manager.win32 import entry_windows, inventory

    monkeypatch.setattr(win32, "ensure_windows", lambda: None)
    monkeypatch.setattr(
        inventory,
        "collect_matching_windows",
        lambda _target, exact=False, current_desktop_only=True: [111, 222],
    )

    assert entry_windows.wait_for_window_by_title("workspace", timeout_seconds=0.5, excluded_hwnds={111}) == 222
    assert entry_windows.wait_for_window_by_title("workspace", timeout_seconds=0.5, excluded_hwnds={111, 222}) is None


def test_get_virtual_desktop_count_uses_com(monkeypatch):
    """get_virtual_desktop_count uses pyvda COM list when available."""
    from desktop_manager import win32
    from desktop_manager.win32 import desktops

    monkeypatch.setattr(win32, "ensure_windows", lambda: None)
    fake_pyvda = SimpleNamespace(
        get_virtual_desktops=lambda: [
            SimpleNamespace(number=1),
            SimpleNamespace(number=2),
            SimpleNamespace(number=3),
        ]
    )
    monkeypatch.setitem(sys.modules, "pyvda", fake_pyvda)

    assert desktops.get_virtual_desktop_count() == 3


def test_get_virtual_desktop_count_falls_back_on_pyvda_error(monkeypatch):
    """When pyvda raises PYVDA_ERRORS and platform is non-win32, returns 0."""
    from desktop_manager import win32
    from desktop_manager.win32 import desktops

    monkeypatch.setattr(win32, "ensure_windows", lambda: None)

    def _broken():
        raise RuntimeError("COM error")

    fake_pyvda = SimpleNamespace(get_virtual_desktops=_broken)
    monkeypatch.setitem(sys.modules, "pyvda", fake_pyvda)
    monkeypatch.setattr(sys, "platform", "linux")

    assert desktops.get_virtual_desktop_count() == 0


def test_get_virtual_desktop_count_uses_registry_fallback(monkeypatch):
    """When pyvda raises PYVDA_ERRORS, get_virtual_desktop_count reads registry on Windows."""
    from desktop_manager import win32
    from desktop_manager.win32 import desktops

    monkeypatch.setattr(win32, "ensure_windows", lambda: None)

    def _broken():
        raise RuntimeError("COM error")

    fake_pyvda = SimpleNamespace(get_virtual_desktops=_broken)
    monkeypatch.setitem(sys.modules, "pyvda", fake_pyvda)
    monkeypatch.setattr(sys, "platform", "win32")

    class _FakeKey:
        def __enter__(self):
            return self

        def __exit__(self, *args):
            pass

    fake_winreg = SimpleNamespace(
        HKEY_CURRENT_USER=1,
        OpenKey=lambda *args, **kwargs: _FakeKey(),
        QueryValueEx=lambda key, name: (b"1234567890123456" * 3, 1),
    )
    monkeypatch.setitem(sys.modules, "winreg", fake_winreg)

    assert desktops.get_virtual_desktop_count() == 3


def test_ensure_current_desktop_number_bounds_guard(monkeypatch):
    """ensure_current_desktop_number rejects desktop indices beyond the available count."""
    from desktop_manager import win32
    from desktop_manager.win32 import desktops

    events: list[str] = []
    monkeypatch.setattr(win32, "ensure_windows", lambda: None)
    monkeypatch.setattr(desktops, "get_virtual_desktop_count", lambda: 3)
    monkeypatch.setattr(desktops, "get_current_desktop_number", lambda: 1)
    monkeypatch.setattr(desktops, "_emit_virtual_desktop_event", events.append)
    monkeypatch.setattr(desktops, "go_to_desktop_number", lambda _idx: pytest.fail("go_to_desktop_number called"))
    monkeypatch.setattr(desktops, "_anchor_to_desktop_via_hotkeys", lambda _idx, **_kw: pytest.fail("anchor called"))

    # Above upper bound
    assert desktops.ensure_current_desktop_number(4) is False
    assert any("Target desktop 4 exceeds desktop count 3; skipping navigation" in e for e in events)

    # Below lower bound
    events.clear()
    assert desktops.ensure_current_desktop_number(0) is False
    assert any("Target desktop 0 exceeds desktop count 3; skipping navigation" in e for e in events)


def test_anchor_to_desktop_via_hotkeys_bounds_guard(monkeypatch):
    """_anchor_to_desktop_via_hotkeys returns None immediately if out of bounds."""
    from desktop_manager.win32 import desktops

    monkeypatch.setattr(desktops, "get_virtual_desktop_count", lambda: 2)
    monkeypatch.setattr(desktops, "switch_leftmost", lambda _steps: pytest.fail("switch_leftmost called"))

    assert desktops._anchor_to_desktop_via_hotkeys(3, settle_seconds=0.0) is None
    assert desktops._anchor_to_desktop_via_hotkeys(0, settle_seconds=0.0) is None


def test_go_to_desktop_number_bounds_guard(monkeypatch):
    """go_to_desktop_number emits event and does not call pyvda if out of bounds."""
    from desktop_manager import win32
    from desktop_manager.win32 import desktops

    events: list[str] = []
    monkeypatch.setattr(win32, "ensure_windows", lambda: None)
    monkeypatch.setattr(desktops, "get_virtual_desktop_count", lambda: 2)
    monkeypatch.setattr(desktops, "_emit_virtual_desktop_event", events.append)

    desktops.go_to_desktop_number(3)
    assert any("Target desktop 3 exceeds desktop count 2; skipping navigation" in e for e in events)


def test_move_window_to_desktop_number_bounds_guard(monkeypatch):
    """move_window_to_desktop_number emits event and skips if out of bounds."""
    from desktop_manager import win32
    from desktop_manager.win32 import desktops

    events: list[str] = []
    monkeypatch.setattr(win32, "ensure_windows", lambda: None)
    monkeypatch.setattr(desktops, "get_virtual_desktop_count", lambda: 2)
    monkeypatch.setattr(desktops, "_emit_virtual_desktop_event", events.append)

    desktops.move_window_to_desktop_number(hwnd=1001, desktop_index=3)
    assert any("Target desktop 3 exceeds desktop count 2; skipping window move for hwnd=1001" in e for e in events)


def test_ensure_desktop_count_uses_com_creation(monkeypatch):
    """ensure_desktop_count uses pyvda VirtualDesktop.create to reach target count."""
    from desktop_manager import win32
    from desktop_manager.win32 import desktops

    created: list[bool] = []
    current_count = [2]

    monkeypatch.setattr(win32, "ensure_windows", lambda: None)
    monkeypatch.setattr(desktops, "get_virtual_desktop_count", lambda: current_count[0])

    class _FakeDesktop:
        @classmethod
        def create(cls):
            created.append(True)
            current_count[0] += 1

    fake_pyvda = SimpleNamespace(VirtualDesktop=_FakeDesktop)
    monkeypatch.setitem(sys.modules, "pyvda", fake_pyvda)
    monkeypatch.setattr(desktops, "switch_leftmost", lambda _steps: pytest.fail("switch_leftmost called"))
    monkeypatch.setattr(
        desktops, "_send_virtual_desktop_hotkey", lambda *_args, **_kwargs: pytest.fail("hotkey called")
    )

    desktops.ensure_desktop_count(4, switch_delay_seconds=0.0)
    assert len(created) == 2


def test_ensure_desktop_count_falls_back_to_hotkeys(monkeypatch):
    """ensure_desktop_count falls back to hotkeys when COM creation fails."""
    from desktop_manager import win32
    from desktop_manager.win32 import desktops

    hotkey_calls: list[str] = []
    current_count = [2]

    monkeypatch.setattr(win32, "ensure_windows", lambda: None)
    monkeypatch.setattr(desktops, "get_virtual_desktop_count", lambda: current_count[0])

    class _FailingDesktop:
        @classmethod
        def create(cls):
            raise RuntimeError("COM creation failed")

    fake_pyvda = SimpleNamespace(VirtualDesktop=_FailingDesktop)
    monkeypatch.setitem(sys.modules, "pyvda", fake_pyvda)

    monkeypatch.setattr(desktops, "switch_leftmost", lambda _steps: None)
    monkeypatch.setattr(time, "sleep", lambda _s: None)

    def _fake_hotkey(direction: str, *, settle_seconds: float, reason: str):
        hotkey_calls.append(direction)
        current_count[0] += 1

    monkeypatch.setattr(desktops, "_send_virtual_desktop_hotkey", _fake_hotkey)

    desktops.ensure_desktop_count(3, switch_delay_seconds=0.0)
    assert hotkey_calls == ["create"]


def test_move_window_to_target_desktop_catches_pyvda_error(monkeypatch):
    """_move_window_to_target_desktop catches PYVDA_ERRORS and returns False."""
    from desktop_manager.win32 import entry_windows

    monkeypatch.setattr(entry_windows.desktops, "get_window_desktop_number", lambda _hwnd: 1)

    def _failing_move(_hwnd: int, _desktop: int) -> None:
        raise ValueError("Invalid desktop")

    monkeypatch.setattr(entry_windows.desktops, "move_window_to_desktop_number", _failing_move)

    assert entry_windows._move_window_to_target_desktop(hwnd=42, target_desktop_number=2) is False
