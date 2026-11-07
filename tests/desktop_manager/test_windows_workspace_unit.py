import json
import sys
from pathlib import Path
from types import SimpleNamespace

import pytest

from desktop_manager.models import ProcessDetails
from desktop_manager.placeholders import substitute_placeholders
from desktop_manager.plan import save_plan_bundle, with_stored_flags
from desktop_manager.window_reuse import window_identity_match_score
from desktop_manager.windows_workspace import (
    SAMPLE_PLAN_TEMPLATE_PATH,
    DesktopPlan,
    LaunchEntry,
    PlacementLayout,
    PlacementSpec,
    Rect,
    WindowSnapshot,
    WorkspacePlan,
    build_bespoke_machine_plan,
    build_launch_entries_for_desktop,
    build_sample_plan,
    build_terminal_command,
    captured_window_match_score,
    launch_entry,
    load_sample_plan_template,
    parse_plan_json,
    rename_desktop,
    render_workspace_diff,
    retire_switched_off_desktops,
    validate_plan,
    validate_plan_structure,
    verify_plan,
)
from tests.assertions import assert_deep_equal


def test_parse_plan_json_supports_bundle_desktop_files(tmp_path: Path):
    bundle_dir = tmp_path / "bundle"
    desktops_dir = bundle_dir / "desktops"
    desktops_dir.mkdir(parents=True)

    (desktops_dir / "work.json").write_text(
        json.dumps(
            {
                "name": "Work",
                "launch": [
                    {
                        "name": "Browser",
                        "type": "app",
                        "path": "{chrome-path}",
                        "args": ["--profile-directory={work-profile}"],
                        "placement": {"screen": 1, "layout": {"mode": "full", "slot": 1}},
                    }
                ],
            }
        ),
        encoding="utf-8",
    )
    (desktops_dir / "admin.json").write_text(
        json.dumps(
            {
                "name": "Admin",
                "launch": [
                    {
                        "name": "Terminal",
                        "type": "terminal",
                        "cwd": "{workspace}",
                        "command": "opencode",
                        "placement": {"screen": 1, "layout": {"mode": "full", "slot": 1}},
                    }
                ],
            }
        ),
        encoding="utf-8",
    )

    bundle_text = json.dumps(
        {
            "version": 1,
            "aliases": {
                "chrome-path": r"C:\Program Files\Google\Chrome\Application\chrome.exe",
                "work-profile": "Default",
            },
            "bundle": {
                "desktops": [
                    {"path": "desktops/work.json"},
                    {"path": "desktops/admin.json", "name": "Admin Override"},
                ]
            },
        }
    )

    plan = parse_plan_json(bundle_text, source="bundle", base_dir=bundle_dir)

    assert [desktop.name for desktop in plan.desktops] == ["Work", "Admin Override"]
    assert plan.aliases["chrome-path"].endswith("chrome.exe")
    assert plan.aliases["work-profile"] == "Default"
    assert plan.desktops[0].launch[0].path == "{chrome-path}"
    assert plan.desktops[0].launch[0].args == ["--profile-directory={work-profile}"]


def test_workspace_plan_substitutions_expand_nested_aliases():
    plan = WorkspacePlan(
        aliases={
            "code-root": r"C:\code",
            "workspace-root": r"{code-root}\workspace",
            "rig-root": r"{code-root}\personal\rig",
            "chrome-profile-work": "Default",
        },
        workspace="{workspace-root}",
        personal_workspace="{rig-root}",
    )

    substitutions = plan.substitutions()

    assert substitutions["code-root"] == r"C:\code"
    assert substitutions["workspace-root"] == r"C:\code\workspace"
    assert substitutions["rig-root"] == r"C:\code\personal\rig"
    assert substitutions["workspace"] == r"C:\code\workspace"
    assert substitutions["personal-workspace"] == r"C:\code\personal\rig"


def test_environment_aliases_fill_in_unless_the_plan_names_them(monkeypatch: pytest.MonkeyPatch):
    monkeypatch.setenv("LOCALAPPDATA", r"C:\Users\someone\AppData\Local")
    monkeypatch.setenv("APPDATA", r"C:\Users\someone\AppData\Roaming")
    plan = WorkspacePlan(aliases={"fork-path": r"{localappdata}\Fork\Fork.exe", "appdata": r"D:\roaming"})

    substitutions = plan.substitutions()

    assert substitutions["fork-path"] == r"C:\Users\someone\AppData\Local\Fork\Fork.exe"
    assert substitutions["appdata"] == r"D:\roaming"


def test_machine_aliases_win_at_resolution_and_stay_out_of_the_plan(tmp_path: Path, monkeypatch: pytest.MonkeyPatch):
    _ = (tmp_path / "config.json").write_text(
        json.dumps({"tools": {"desktop": {"aliases": {"chrome-profile-work": "Profile 4", "extra": "x"}}}}),
        encoding="utf-8",
    )
    monkeypatch.setenv("WORKSPACE_HOME", str(tmp_path))
    plan = WorkspacePlan(aliases={"chrome-profile-work": "Default", "code-root": r"C:\code"})

    substitutions = plan.substitutions()

    assert substitutions["chrome-profile-work"] == "Profile 4"
    assert substitutions["extra"] == "x"
    assert substitutions["code-root"] == r"C:\code"
    assert plan.to_dict()["aliases"] == {"chrome-profile-work": "Default", "code-root": r"C:\code"}


def test_substitute_placeholders_rejects_unresolved_placeholder():
    with pytest.raises(ValueError, match=r"Unresolved placeholders: \{workspace\}"):
        substitute_placeholders("{workspace}", {})


def test_substitute_placeholders_rejects_partial_unresolved_placeholder():
    with pytest.raises(ValueError, match=r"\{personal-workspace\}"):
        substitute_placeholders(r"C:\code\{personal-workspace}", {"workspace": r"C:\code\workspace"})


def test_window_identity_match_score_deprioritizes_chrome_window_without_url_hint():
    """A Chrome window whose title misses the URL hint is a last resort, not disqualified.

    Chrome titles drift -- a still-loading tab reads "New Tab" -- so a hard zero here
    reported a visible, correctly placed window as missing. The score must stay far
    below a genuine hint match so any better candidate wins.
    """
    entry = LaunchEntry(
        name="Google Calendar - Work Profile",
        type="app",
        path=r"C:\Program Files\Google\Chrome\Application\chrome.exe",
        args=["https://calendar.google.com/"],
        window_process_name="chrome.exe",
    )

    score = window_identity_match_score(entry, "Google Meet - Google Chrome", "chrome.exe", {})

    assert score == 1

    # A window from a different process gets nothing at all.
    assert window_identity_match_score(entry, "Google Meet - Google Chrome", "msedge.exe", {}) == 0


def test_window_identity_match_score_prefers_chrome_url_hint_match():
    entry = LaunchEntry(
        name="Admin Browser - Work Profile",
        type="app",
        path=r"C:\Program Files\Google\Chrome\Application\chrome.exe",
        args=["https://mail.google.com/"],
        window_process_name="chrome.exe",
    )

    score = window_identity_match_score(entry, "Inbox (12) - Gmail - Google Chrome", "chrome.exe", {})

    assert score >= 270


def test_window_identity_match_score_prefers_terminal_command_hint():
    copilot_entry = LaunchEntry(
        name="Workspace Terminal 4",
        type="terminal",
        command="copilot",
        cwd="{workspace}",
        shell="wt-pwsh",
        window_process_name="WindowsTerminal.exe",
    )
    opencode_entry = LaunchEntry(
        name="Workspace Terminal 1",
        type="terminal",
        command="opencode",
        cwd="{workspace}",
        shell="wt-pwsh",
        window_process_name="WindowsTerminal.exe",
    )

    copilot_score = window_identity_match_score(copilot_entry, "GitHub Copilot", "WindowsTerminal.exe", {})
    opencode_score = window_identity_match_score(opencode_entry, "GitHub Copilot", "WindowsTerminal.exe", {})

    assert copilot_score > opencode_score


def test_window_identity_match_score_prefers_claude_terminal_command_hint():
    claude_entry = LaunchEntry(
        name="Personal Terminal 2",
        type="terminal",
        command="claude --model claude-sonnet-4-6 --allow-dangerously-skip-permissions",
        cwd="{personal-workspace}",
        shell="wt-pwsh",
        window_process_name="WindowsTerminal.exe",
    )
    opencode_entry = LaunchEntry(
        name="Personal Terminal 1",
        type="terminal",
        command="opencode",
        cwd="{personal-workspace}",
        shell="wt-pwsh",
        window_process_name="WindowsTerminal.exe",
    )

    claude_score = window_identity_match_score(
        claude_entry,
        "⠐ Optimize Claude Code token usage and settings",
        "WindowsTerminal.exe",
        {},
    )
    opencode_score = window_identity_match_score(
        opencode_entry,
        "⠐ Optimize Claude Code token usage and settings",
        "WindowsTerminal.exe",
        {},
    )

    assert claude_score > opencode_score


def test_window_identity_match_score_does_not_match_slack_app_to_chrome_tab_title():
    entry = LaunchEntry(
        name="Slack",
        type="app",
        path=r"C:\Users\someone\AppData\Local\slack\Slack.exe",
        window_process_name="slack.exe",
    )

    score = window_identity_match_score(entry, "Redirecting... | Slack - Google Chrome", "chrome.exe", {})

    assert score == 0


def test_window_identity_match_score_rejects_title_match_from_the_wrong_process():
    """A declared ``window_process_name`` disqualifies, it does not merely lose points.

    ``Personal Warp`` anchors on the title "shared-utilities", which is also a
    substring of the VS Code window title for that repo. A title substring scores 150
    against the process match's 100, so the Warp entry used to claim the VS Code window
    and the real ``VS Code (shared-utilities)`` entry then launched a duplicate.
    """
    entry = LaunchEntry(
        name="Personal Warp",
        type="app",
        path=r"C:\Users\someone\AppData\Local\Programs\Warp\warp.exe",
        window_title="shared-utilities",
        window_process_name="warp.exe",
    )

    wrong_process = window_identity_match_score(
        entry,
        "metadata-defects-meeting-summary.md - shared-utilities - Visual Studio Code",
        "Code.exe",
        {},
    )
    right_process = window_identity_match_score(entry, "pwsh in shared-utilities", "warp.exe", {})

    assert wrong_process == 0
    assert right_process > 0


def test_window_identity_match_score_still_matches_when_the_window_process_is_unknown():
    """An unidentifiable window stays eligible -- the gate must not punish missing data."""
    entry = LaunchEntry(
        name="Personal Warp",
        type="app",
        path=r"C:\Users\someone\AppData\Local\Programs\Warp\warp.exe",
        window_title="shared-utilities",
        window_process_name="warp.exe",
    )

    assert window_identity_match_score(entry, "pwsh in shared-utilities", None, {}) > 0


def test_parse_plan_json_bundle_requires_base_dir_for_relative_files():
    bundle_text = json.dumps(
        {
            "bundle": {
                "desktops": [
                    {"path": "desktops/work.json"},
                ]
            }
        }
    )

    with pytest.raises(ValueError, match="require a filesystem base directory"):
        parse_plan_json(bundle_text, source="bundle")


def test_save_plan_bundle_writes_root_and_desktop_files(tmp_path: Path):
    plan = WorkspacePlan(
        workspace=str(tmp_path / "workspace"),
        aliases={"chrome-path": r"C:\Program Files\Google\Chrome\Application\chrome.exe"},
        desktops=[
            DesktopPlan(name="Work Desktop", launch=[]),
            DesktopPlan(name="Work Desktop", launch=[]),
        ],
    )

    bundle_path = tmp_path / "bundle.json"
    save_plan_bundle(plan, bundle_path)

    bundle_root = json.loads(bundle_path.read_text(encoding="utf-8"))
    desktop_refs = bundle_root["bundle"]["desktops"]

    assert bundle_root["workspace"] == str(tmp_path / "workspace")
    assert bundle_root["aliases"]["chrome-path"].endswith("chrome.exe")
    assert desktop_refs == [
        {"path": "desktops/work-desktop.json"},
        {"path": "desktops/work-desktop-2.json"},
    ]
    assert (tmp_path / "desktops" / "work-desktop.json").exists()
    assert (tmp_path / "desktops" / "work-desktop-2.json").exists()


def test_build_sample_plan_creates_workspace_terminals(tmp_path: Path):
    plan = build_sample_plan(tmp_path, terminal_count=2)
    expected = build_sample_plan(tmp_path, terminal_count=2)

    assert_deep_equal(plan, expected)


def test_build_launch_entries_for_first_desktop_converts_terminals(tmp_path: Path):
    windows = [
        WindowSnapshot(title="Open Terminal", process_name="WindowsTerminal.exe", exe_path=r"C:\wt.exe"),
        WindowSnapshot(title="Obsidian", process_name="Obsidian.exe", exe_path=r"C:\Obsidian.exe"),
    ]

    entries = build_launch_entries_for_desktop(windows, desktop_number=1, workspace=tmp_path)
    expected = [
        LaunchEntry(
            name="Workspace Terminal 1",
            type="terminal",
            cwd="{workspace}",
            command="opencode",
            shell="wt-pwsh",
            window_title="Workspace Terminal 1",
            placement=PlacementSpec(screen=2, layout=PlacementLayout(mode="thirds", slot=1)),
            window_process_name="WindowsTerminal.exe",
            notes="Generated from captured terminal window: Open Terminal",
        ),
        LaunchEntry(
            name="Obsidian",
            type="app",
            path=r"C:\Obsidian.exe",
            window_process_name="Obsidian.exe",
            notes=("Captured from the current desktop state. Add args if this app needs a project, URL, or profile."),
        ),
    ]

    assert_deep_equal(entries, expected)


def test_build_launch_entries_for_first_desktop_uses_inferred_terminal_placements_and_copilot(tmp_path: Path):
    windows = [
        WindowSnapshot(
            title="GitHub Copilot",
            process_name="WindowsTerminal.exe",
            exe_path=r"C:\wt.exe",
            inferred_placement=PlacementSpec(
                screen=3,
                layout=PlacementLayout(mode="halves", slot=1, orientation="vertical"),
            ),
        ),
        WindowSnapshot(
            title="Fork",
            process_name="Fork.exe",
            exe_path=r"C:\Fork.exe",
            inferred_placement=PlacementSpec(
                screen=2, layout=PlacementLayout(mode="halves", slot=1, orientation="vertical")
            ),
        ),
    ]

    entries = build_launch_entries_for_desktop(windows, desktop_number=1, workspace=tmp_path)

    assert entries[0].command == "copilot"
    assert entries[0].placement == PlacementSpec(
        screen=3,
        layout=PlacementLayout(mode="halves", slot=1, orientation="vertical"),
    )
    assert entries[1].placement == PlacementSpec(
        screen=2,
        layout=PlacementLayout(mode="halves", slot=1, orientation="vertical"),
    )
    assert entries[1].window_process_name == "Fork.exe"


def test_build_terminal_command_rejects_missing_generated_cwd(tmp_path: Path):
    entry = LaunchEntry(
        name="Workspace Terminal 1",
        type="terminal",
        cwd="{workspace}",
        command="opencode",
        shell="wt-pwsh",
    )

    with pytest.raises(ValueError, match="resolved cwd does not exist"):
        build_terminal_command(entry, {"workspace": str(tmp_path / "missing")})


def test_launch_entry_rejects_missing_local_path_arg(tmp_path: Path, monkeypatch):
    entry = LaunchEntry(
        name="VS Code Workspace",
        type="app",
        path=str(tmp_path / "Code.exe"),
        args=[str(tmp_path / "missing.code-workspace")],
    )
    exe = tmp_path / "Code.exe"
    exe.write_text("", encoding="utf-8")

    monkeypatch.setattr("subprocess.Popen", lambda *_args, **_kwargs: None)

    with pytest.raises(ValueError, match="resolved arg does not exist"):
        launch_entry(entry, {})


def _install_fake_pyvda(monkeypatch, names: dict[int, str], *, rename_raises: bool = False):
    class _FakeDesktop:
        def __init__(self, number: int) -> None:
            self.number = number

        @property
        def name(self) -> str:
            return names.get(self.number, "")

        def rename(self, value: str) -> None:
            if rename_raises:
                raise RuntimeError("rename failed")
            names[self.number] = value

    fake_pyvda = SimpleNamespace(VirtualDesktop=_FakeDesktop)
    monkeypatch.setattr("desktop_manager.win32.ensure_windows", lambda: None)
    monkeypatch.setitem(sys.modules, "pyvda", fake_pyvda)
    return names


def test_rename_desktop_sets_name(monkeypatch):
    names = _install_fake_pyvda(monkeypatch, {1: ""})

    assert rename_desktop(1, "Review") is True
    assert names[1] == "Review"


def test_rename_desktop_noops_when_already_named(monkeypatch):
    calls: list[str] = []
    names = _install_fake_pyvda(monkeypatch, {2: "Work"})

    # Re-wrap rename to record whether it is called when the name already matches.
    original = sys.modules["pyvda"].VirtualDesktop.rename

    def _tracking_rename(self, value):
        calls.append(value)
        return original(self, value)

    monkeypatch.setattr(sys.modules["pyvda"].VirtualDesktop, "rename", _tracking_rename)

    assert rename_desktop(2, "Work") is True
    assert calls == []
    assert names[2] == "Work"


def test_rename_desktop_returns_false_on_failure(monkeypatch):
    _install_fake_pyvda(monkeypatch, {3: ""}, rename_raises=True)

    assert rename_desktop(3, "Admin") is False


def _install_fake_pyvda_desktops(monkeypatch, names: list[str], *, remove_raises: bool = False):
    """Fake pyvda with an ordered desktop list; ``remove`` renumbers like Windows does."""
    live = list(names)
    fallbacks: list[int] = []

    class _FakeDesktop:
        def __init__(self, number: int) -> None:
            self.number = number

        @property
        def name(self) -> str:
            return live[self.number - 1]

        def remove(self, fallback=None) -> None:
            if remove_raises:
                raise RuntimeError("remove failed")
            fallbacks.append(fallback.number if fallback is not None else 0)
            del live[self.number - 1]

    fake_pyvda = SimpleNamespace(
        VirtualDesktop=_FakeDesktop,
        get_virtual_desktops=lambda: [_FakeDesktop(number) for number in range(1, len(live) + 1)],
    )
    monkeypatch.setattr("desktop_manager.win32.ensure_windows", lambda: None)
    monkeypatch.setitem(sys.modules, "pyvda", fake_pyvda)
    return live, fallbacks


def test_retire_switched_off_desktops_removes_a_switched_off_desktop(monkeypatch):
    live, fallbacks = _install_fake_pyvda_desktops(monkeypatch, ["Review", "Work", "Admin", "Personal"])

    assert retire_switched_off_desktops(3, ["Personal"]) == ["Personal"]
    assert live == ["Review", "Work", "Admin"]
    # Its windows land on the last kept desktop rather than jumping to desktop 1.
    assert fallbacks == [3]


def test_retire_switched_off_desktops_removes_a_leftover_among_the_planned_ones(monkeypatch):
    # Personal was on ahead of Lab; with it off the plan is Review/Work/Admin/Lab.
    live, fallbacks = _install_fake_pyvda_desktops(monkeypatch, ["Review", "Work", "Admin", "Personal", "Lab"])

    assert retire_switched_off_desktops(4, ["Personal"]) == ["Personal"]
    assert live == ["Review", "Work", "Admin", "Lab"]
    # Its windows go to the desktop that ends up last of the planned four: Lab.
    assert fallbacks == [5]


def test_retire_switched_off_desktops_matches_any_case_and_leaves_user_desktops_alone(monkeypatch):
    live, _ = _install_fake_pyvda_desktops(monkeypatch, ["Personal", "Work", "Admin", "Scratch", "personal"])

    assert retire_switched_off_desktops(3, ["Personal"]) == ["personal", "Personal"]
    assert live == ["Work", "Admin", "Scratch"]


def test_retire_switched_off_desktops_always_keeps_one(monkeypatch):
    live, _ = _install_fake_pyvda_desktops(monkeypatch, ["Personal"])

    assert retire_switched_off_desktops(1, ["Personal"]) == []
    assert live == ["Personal"]


def test_retire_switched_off_desktops_never_raises(monkeypatch):
    _install_fake_pyvda_desktops(monkeypatch, ["Review", "Work", "Admin", "Personal"], remove_raises=True)

    assert retire_switched_off_desktops(3, ["Personal"]) == []


def test_list_current_desktop_windows_filters_other_desktops(monkeypatch):
    monkeypatch.setattr("desktop_manager.win32.ensure_windows", lambda: None)

    fake_win32gui = SimpleNamespace(
        EnumWindows=lambda callback, lparam: (callback(10, lparam), callback(20, lparam)),
        IsWindowVisible=lambda _hwnd: True,
        GetWindowText=lambda hwnd: {10: "Chrome", 20: "Slack"}[hwnd],
        GetWindowLong=lambda _hwnd, _index: 0,
        GetClassName=lambda _hwnd: "Window",
        GetWindowRect=lambda hwnd: {10: (100, 0, 200, 100), 20: (0, 0, 100, 100)}[hwnd],
    )

    fake_win32process = SimpleNamespace(GetWindowThreadProcessId=lambda hwnd: (1, {10: 100, 20: 200}[hwnd]))
    fake_win32con = SimpleNamespace(GWL_EXSTYLE=0, WS_EX_TOOLWINDOW=1)

    monkeypatch.setitem(__import__("sys").modules, "win32gui", fake_win32gui)
    monkeypatch.setitem(__import__("sys").modules, "win32process", fake_win32process)
    monkeypatch.setitem(__import__("sys").modules, "win32con", fake_win32con)
    monkeypatch.setattr("desktop_manager.win32.desktops.is_window_on_current_desktop", lambda hwnd: hwnd == 10)
    monkeypatch.setattr(
        "desktop_manager.win32.inventory.get_process_details",
        lambda pid: {
            100: ProcessDetails("chrome.exe", r"C:\chrome.exe"),
            200: ProcessDetails("slack.exe", r"C:\slack.exe"),
        }[pid],
    )
    monkeypatch.setattr(
        "desktop_manager.win32.geometry.monitor_display_areas",
        lambda: [
            SimpleNamespace(screen_number=1, bounds=Rect(left=0, top=0, width=100, height=100)),
            SimpleNamespace(screen_number=2, bounds=Rect(left=100, top=0, width=100, height=100)),
            SimpleNamespace(screen_number=3, bounds=Rect(left=200, top=0, width=100, height=100)),
        ],
    )

    from desktop_manager.windows_workspace import list_current_desktop_windows

    windows = list_current_desktop_windows()

    assert [window.title for window in windows] == ["Chrome"]
    assert windows[0].screen_number == 2


def test_screen_number_for_window_uses_window_center(monkeypatch):
    monkeypatch.setattr("desktop_manager.win32.ensure_windows", lambda: None)
    monkeypatch.setattr(
        "desktop_manager.win32.geometry.monitor_display_areas",
        lambda: [
            SimpleNamespace(screen_number=1, bounds=Rect(left=0, top=0, width=100, height=100)),
            SimpleNamespace(screen_number=2, bounds=Rect(left=100, top=0, width=100, height=100)),
            SimpleNamespace(screen_number=3, bounds=Rect(left=200, top=0, width=100, height=100)),
        ],
    )

    fake_win32gui = SimpleNamespace(GetWindowRect=lambda _hwnd: (120, 10, 180, 90))
    monkeypatch.setitem(__import__("sys").modules, "win32gui", fake_win32gui)

    from desktop_manager.windows_workspace import screen_number_for_window

    assert screen_number_for_window(10) == 2


def test_infer_placement_for_window_matches_known_layout(monkeypatch):
    monkeypatch.setattr("desktop_manager.win32.ensure_windows", lambda: None)
    monkeypatch.setattr(
        "desktop_manager.win32.geometry.monitor_display_areas",
        lambda: [SimpleNamespace(screen_number=1, bounds=Rect(left=0, top=0, width=300, height=120))],
    )

    fake_win32gui = SimpleNamespace(GetWindowRect=lambda _hwnd: (100, 0, 200, 120))
    monkeypatch.setitem(__import__("sys").modules, "win32gui", fake_win32gui)

    from desktop_manager.windows_workspace import infer_placement_for_window

    placement = infer_placement_for_window(10)

    assert placement is not None
    assert placement.to_dict() == {"screen": 1, "layout": {"mode": "thirds", "slot": 2}}


def test_load_sample_plan_template_creates_generic_layout(tmp_path: Path):
    plan = load_sample_plan_template(tmp_path)

    assert {
        "desktop_names": [desktop.name for desktop in plan.desktops],
        "work_entries": [entry.name for entry in plan.desktops[0].launch],
        "work_entry_types": [entry.type for entry in plan.desktops[0].launch],
        "admin_entries": [entry.name for entry in plan.desktops[1].launch],
        "browser_profile": plan.desktops[0].launch[1].args,
        "workspace": plan.workspace,
    } == {
        "desktop_names": ["Work", "Admin"],
        "work_entries": ["Terminal A", "Browser", "Editor"],
        "work_entry_types": ["terminal", "app", "app"],
        "admin_entries": ["Mail", "Chat"],
        "browser_profile": ["--profile-directory=Default"],
        "workspace": str(tmp_path),
    }


def test_build_bespoke_machine_plan_loads_repo_template(tmp_path: Path):
    plan = with_stored_flags(build_bespoke_machine_plan(tmp_path))

    review = plan.desktops[0]
    work = plan.desktops[1]
    assert {
        "desktop_names": [desktop.name for desktop in plan.desktops],
        "review_entries": [entry.name for entry in review.launch],
        "review_disabled": [entry.name for entry in review.launch if entry.disabled],
        "work_entries": [entry.name for entry in work.launch],
        "work_features": plan.features,
        "work_active_entries": [
            entry.name for entry in work.launch if entry.feature is None or plan.features.get(entry.feature, False)
        ],
        "admin_entries": [entry.name for entry in plan.desktops[2].launch],
        "personal_entries": [entry.name for entry in plan.desktops[3].launch],
        "work_screen_3_entries": [
            {
                "name": entry.name,
                "screen": entry.placement.screen if entry.placement is not None else None,
                "slot": entry.placement.layout.slot if entry.placement is not None else None,
                "orientation": entry.placement.layout.orientation if entry.placement is not None else None,
            }
            for entry in work.launch
            if entry.placement is not None and entry.placement.screen == 3
        ],
    } == {
        "desktop_names": ["Review", "Work", "Admin", "Personal", "Lab"],
        "review_entries": [
            "VS Code Workspace",
            "Rig Web",
            "Fork",
            "T3 Code",
        ],
        "review_disabled": ["T3 Code"],
        "work_entries": [
            "Work Orca",
            "Personal Orca",
            "Workspace Terminal 1",
            "Workspace Terminal 2",
            "Workspace Terminal 5",
            "Work Browser - Work Profile",
            "Fork",
            "Workspace Terminal 3",
            "Workspace Terminal 4",
            "Warp Workspace 1",
            "Warp Workspace 2",
            "Warp (Claude experiment)",
        ],
        "work_features": {
            "experimental-desktop": True,
            "personal-desktop": False,
            "warp-terminals": False,
            "work-chrome": True,
            "work-fork": False,
            "wt-terminals": False,
            "orca-sessions": True,
            "claude-session-restore": False,
        },
        "work_active_entries": [
            "Work Orca",
            "Personal Orca",
            "Work Browser - Work Profile",
        ],
        "admin_entries": [
            "Admin Browser - Work Profile",
            "Google Meet + Calendar - Work Profile",
            "Slack",
            "1Password",
        ],
        "personal_entries": [
            "Obsidian Vault",
            "Personal Warp",
            "Personal Browser",
            "VS Code (rig)",
            "Fork",
        ],
        "work_screen_3_entries": [
            {"name": "Personal Orca", "screen": 3, "slot": 1, "orientation": None},
            {"name": "Workspace Terminal 3", "screen": 3, "slot": 1, "orientation": "vertical"},
            {"name": "Workspace Terminal 4", "screen": 3, "slot": 2, "orientation": "vertical"},
            {"name": "Warp (Claude experiment)", "screen": 3, "slot": 1, "orientation": None},
        ],
    }


def test_validate_plan_accepts_bespoke_machine_plan(tmp_path: Path):
    plan = build_bespoke_machine_plan(tmp_path)

    assert validate_plan(plan) == []


def test_validate_plan_reports_invalid_entries():
    from desktop_manager.windows_workspace import DesktopPlan, LaunchEntry, WorkspacePlan

    plan = WorkspacePlan(
        desktops=[
            DesktopPlan(
                name="Work",
                launch=[
                    LaunchEntry(name="Broken Terminal", type="terminal", cwd="{workspace}"),
                    LaunchEntry(name="Broken App", type="app"),
                    LaunchEntry(
                        name="Bad Placement",
                        type="app",
                        path=r"C:\tool.exe",
                        placement=PlacementSpec(screen=0, layout=PlacementLayout(mode="halves", slot=3)),
                    ),
                ],
            ),
            DesktopPlan(name="Work", launch=[LaunchEntry(name="Broken App", type="app")]),
        ]
    )

    assert validate_plan(plan, require_existing_paths=False) == [
        "Work/Broken Terminal: terminal missing command",
        "Work/Broken App: app missing path",
        "Work/Bad Placement: placement screen must be >= 1",
        "Work/Bad Placement: halves slot must be 1 or 2",
        "Work/Bad Placement: halves orientation must be vertical or horizontal",
        "Duplicate desktop name: Work",
        "Work/Broken App: app missing path",
    ]


def test_validate_plan_reports_missing_command_cwd_when_path_checks_enabled(tmp_path: Path):
    plan = WorkspacePlan(
        desktops=[
            DesktopPlan(
                name="Work",
                launch=[
                    LaunchEntry(
                        name="Sync Config",
                        type="command",
                        command="git status",
                        cwd=str(tmp_path / "missing"),
                    )
                ],
            )
        ]
    )

    assert validate_plan(plan, require_existing_paths=True) == [
        f"Work/Sync Config: cwd does not exist: {tmp_path / 'missing'}"
    ]


def test_validate_plan_structure_accepts_full_coverage(tmp_path: Path):
    plan = build_bespoke_machine_plan(tmp_path)

    assert validate_plan_structure(plan, expected_screen_count=3) == []


def test_validate_plan_structure_reports_empty_space_and_empty_screens():
    from desktop_manager.windows_workspace import DesktopPlan, LaunchEntry, WorkspacePlan

    plan = WorkspacePlan(
        desktops=[
            DesktopPlan(
                name="Sparse",
                launch=[
                    LaunchEntry(
                        name="One Window",
                        type="app",
                        path=r"C:\tool.exe",
                        placement=PlacementSpec(
                            screen=1,
                            layout=PlacementLayout(mode="halves", slot=1, orientation="vertical"),
                        ),
                    )
                ],
            ),
            DesktopPlan(name="Empty", launch=[]),
        ]
    )

    assert validate_plan_structure(plan, expected_screen_count=2) == [
        "Sparse: screen 2 is empty",
        "Sparse: screen 1 leaves 50.0% empty",
        "Empty: virtual desktop is empty",
    ]


def test_validate_plan_structure_strict_reports_overlap(tmp_path: Path):
    plan = build_bespoke_machine_plan(tmp_path)

    assert validate_plan_structure(plan, expected_screen_count=3, strict=True) == []


def test_parse_plan_json_verifies_sample_plan(tmp_path: Path):
    sample_text = SAMPLE_PLAN_TEMPLATE_PATH.read_text(encoding="utf-8")

    plan = parse_plan_json(sample_text, source="sample")
    plan.workspace = str(tmp_path)

    assert verify_plan(plan, source="sample", expected_screen_count=2, require_existing_paths=False) is plan


def test_parse_plan_json_rejects_invalid_plan():
    raw_text = json.dumps(
        {
            "desktops": [
                {
                    "name": "Broken",
                    "launch": [
                        {"name": "Terminal", "type": "terminal", "cwd": "{workspace}"},
                    ],
                }
            ]
        }
    )

    try:
        verify_plan(parse_plan_json(raw_text, source="broken"), source="broken")
    except ValueError as err:
        assert "terminal missing command" in str(err)
    else:
        msg = "expected ValueError"
        raise AssertionError(msg)


def test_load_plan_allows_missing_paths_by_default(tmp_path: Path):
    from desktop_manager.windows_workspace import load_plan, save_plan

    plan_path = tmp_path / "workspace.json"
    save_plan(
        WorkspacePlan(
            desktops=[
                DesktopPlan(
                    name="Work",
                    launch=[
                        LaunchEntry(
                            name="Missing App",
                            type="app",
                            path=r"C:\missing\app.exe",
                        )
                    ],
                )
            ]
        ),
        plan_path,
    )

    loaded = load_plan(plan_path)

    assert loaded.desktops[0].launch[0].name == "Missing App"


def test_load_plan_can_require_existing_paths(tmp_path: Path):
    from desktop_manager.windows_workspace import load_plan, save_plan

    plan_path = tmp_path / "workspace.json"
    save_plan(
        WorkspacePlan(
            desktops=[
                DesktopPlan(
                    name="Work",
                    launch=[
                        LaunchEntry(
                            name="Missing App",
                            type="app",
                            path=r"C:\missing\app.exe",
                        )
                    ],
                )
            ]
        ),
        plan_path,
    )

    with pytest.raises(ValueError, match="Missing App: app path does not exist"):
        load_plan(plan_path, require_existing_paths=True)


@pytest.mark.skipif(sys.platform != "win32", reason="Windows path parsing required")
def test_captured_window_match_score_prefers_screen_and_layout_match(tmp_path: Path):
    entry = LaunchEntry(
        name="Chrome - Default Profile",
        type="app",
        path=r"C:\Program Files\Google\Chrome\Application\chrome.exe",
        placement=PlacementSpec(screen=2, layout=PlacementLayout(mode="thirds", slot=2)),
    )
    matching_window = WindowSnapshot(
        title="Docs",
        process_name="chrome.exe",
        screen_number=2,
        inferred_placement=PlacementSpec(screen=2, layout=PlacementLayout(mode="thirds", slot=2)),
    )
    wrong_screen_window = WindowSnapshot(
        title="Docs",
        process_name="chrome.exe",
        screen_number=1,
        inferred_placement=PlacementSpec(screen=1, layout=PlacementLayout(mode="thirds", slot=2)),
    )

    assert captured_window_match_score(
        entry, matching_window, {"workspace": str(tmp_path)}
    ) > captured_window_match_score(entry, wrong_screen_window, {"workspace": str(tmp_path)})


@pytest.mark.skipif(sys.platform != "win32", reason="Windows path parsing required")
def test_render_workspace_diff_uses_restore_style_matching_labels():
    plan = WorkspacePlan(
        desktops=[
            DesktopPlan(
                name="Work",
                launch=[
                    LaunchEntry(
                        name="Chrome - Default Profile",
                        type="app",
                        path=r"C:\Program Files\Google\Chrome\Application\chrome.exe",
                        placement=PlacementSpec(screen=2, layout=PlacementLayout(mode="thirds", slot=2)),
                    )
                ],
            )
        ]
    )
    actual_plan = WorkspacePlan(
        desktops=[
            DesktopPlan(
                name="Work",
                captured_windows=[
                    WindowSnapshot(
                        title="Docs",
                        process_name="chrome.exe",
                        screen_number=2,
                        inferred_placement=PlacementSpec(screen=2, layout=PlacementLayout(mode="thirds", slot=2)),
                    )
                ],
            )
        ]
    )

    diff = render_workspace_diff(plan, actual_plan)

    assert "matched: Chrome - Default Profile <- Docs [chrome]" in diff
    assert "status:  ok" in diff
    assert "missing:" not in diff
    assert "extra:" not in diff


def test_render_workspace_diff_ignores_disabled_entries():
    plan = WorkspacePlan(
        desktops=[
            DesktopPlan(
                name="Review",
                launch=[
                    LaunchEntry(
                        name="Fork",
                        type="app",
                        path=r"C:\Fork\Fork.exe",
                        window_process_name="Fork.exe",
                        placement=PlacementSpec(screen=1, layout=PlacementLayout(mode="full", slot=1)),
                    ),
                    LaunchEntry(
                        name="T3 Code",
                        type="app",
                        path=r"C:\T3\T3 Code (Alpha).exe",
                        window_process_name="T3 Code (Alpha).exe",
                        disabled=True,
                        placement=PlacementSpec(screen=1, layout=PlacementLayout(mode="full", slot=1)),
                    ),
                ],
            )
        ]
    )
    actual_plan = WorkspacePlan(
        desktops=[
            DesktopPlan(
                name="Review",
                captured_windows=[
                    WindowSnapshot(
                        title="Fork - Work",
                        process_name="Fork.exe",
                        screen_number=1,
                        inferred_placement=PlacementSpec(screen=1, layout=PlacementLayout(mode="full", slot=1)),
                    )
                ],
            )
        ]
    )

    diff = render_workspace_diff(plan, actual_plan)

    # The disabled T3 Code entry must not be treated as planned/missing.
    assert "T3 Code" not in diff
    assert "missing:" not in diff
    assert "status:  ok" in diff


def test_render_workspace_diff_omits_disabled_entries():
    plan = WorkspacePlan(
        desktops=[
            DesktopPlan(
                name="Review",
                launch=[
                    LaunchEntry(
                        name="VS Code Workspace",
                        type="app",
                        path=r"C:\Code.exe",
                        placement=PlacementSpec(screen=1, layout=PlacementLayout(mode="halves", slot=1)),
                    ),
                    LaunchEntry(
                        name="T3 Code",
                        type="app",
                        path=r"C:\Code.exe",
                        placement=PlacementSpec(screen=1, layout=PlacementLayout(mode="halves", slot=2)),
                        disabled=True,
                    ),
                ],
            )
        ]
    )
    actual_plan = WorkspacePlan(desktops=[DesktopPlan(name="Review")])

    diff = render_workspace_diff(plan, actual_plan)

    assert "T3 Code" not in diff
    assert "VS Code Workspace" in diff


def test_parse_plan_json_preserves_entry_summary():
    plan = parse_plan_json(
        json.dumps(
            {
                "desktops": [
                    {
                        "name": "Work",
                        "launch": [
                            {
                                "name": "Workspace Terminal 1",
                                "type": "terminal",
                                "command": "opencode",
                                "cwd": "{workspace}",
                                "summary": "Opens OpenCode in the repo root.",
                            }
                        ],
                    }
                ]
            }
        )
    )

    assert plan.desktops[0].launch[0].summary == "Opens OpenCode in the repo root."
