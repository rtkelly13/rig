from __future__ import annotations

from pathlib import Path

from desktop_manager.window_reuse import (
    WindowMove,
    claim_reusable_window,
    competing_sibling_titles,
    move_existing_window_to_entry,
)
from desktop_manager.windows_workspace import (
    LaunchEntry,
    LiveWindow,
    PlacementLayout,
    PlacementSpec,
    match_reusable_window,
    reusable_window_match_score,
)


def test_reusable_window_match_score_prefers_already_correct_desktop_and_placement(tmp_path: Path):
    entry = LaunchEntry(
        name="Personal Browser",
        type="app",
        path=r"C:\Program Files\Google\Chrome\Application\chrome.exe",
        args=["--profile-directory=Profile 1"],
        placement=PlacementSpec(screen=2, layout=PlacementLayout(mode="halves", slot=2, orientation="vertical")),
        window_process_name="chrome.exe",
    )
    already_correct = LiveWindow(
        hwnd=10,
        title="Inbox - Chrome",
        process_name="chrome.exe",
        desktop_number=3,
        screen_number=2,
        inferred_placement=PlacementSpec(
            screen=2, layout=PlacementLayout(mode="halves", slot=2, orientation="vertical")
        ),
    )
    same_process_wrong_place = LiveWindow(
        hwnd=20,
        title="Inbox - Chrome",
        process_name="chrome.exe",
        desktop_number=1,
        screen_number=1,
        inferred_placement=PlacementSpec(
            screen=1, layout=PlacementLayout(mode="halves", slot=1, orientation="vertical")
        ),
    )

    assert reusable_window_match_score(
        entry, already_correct, {"workspace": str(tmp_path)}, target_desktop_number=3
    ) > reusable_window_match_score(
        entry, same_process_wrong_place, {"workspace": str(tmp_path)}, target_desktop_number=3
    )


def test_reusable_window_match_score_penalises_cross_desktop_terminal_with_changed_title(tmp_path: Path):
    entry = LaunchEntry(
        name="Workspace Terminal 1",
        type="terminal",
        command="opencode",
        cwd="{workspace}",
        shell="wt-pwsh",
        window_title="Workspace Terminal 1",
        placement=PlacementSpec(screen=1, layout=PlacementLayout(mode="thirds", slot=1)),
        window_process_name="WindowsTerminal.exe",
    )
    candidate = LiveWindow(
        hwnd=30,
        title="opencode - shared-utilities",
        process_name="WindowsTerminal.exe",
        desktop_number=3,
        screen_number=2,
        inferred_placement=PlacementSpec(
            screen=2,
            layout=PlacementLayout(mode="halves", slot=1, orientation="vertical"),
            subdivide=PlacementLayout(mode="halves", slot=1, orientation="horizontal"),
        ),
    )

    score = reusable_window_match_score(entry, candidate, {"workspace": str(tmp_path)}, target_desktop_number=1)

    assert score == 0


def test_reusable_window_match_score_allows_same_desktop_terminal_with_changed_title(tmp_path: Path):
    entry = LaunchEntry(
        name="Personal Terminal 1",
        type="terminal",
        command="opencode",
        cwd="{personal-workspace}",
        shell="wt-pwsh",
        window_title="Personal Terminal 1",
        placement=PlacementSpec(
            screen=2,
            layout=PlacementLayout(mode="halves", slot=1, orientation="vertical"),
            subdivide=PlacementLayout(mode="halves", slot=1, orientation="horizontal"),
        ),
        window_process_name="WindowsTerminal.exe",
    )
    candidate = LiveWindow(
        hwnd=40,
        title="opencode - shared-utilities",
        process_name="WindowsTerminal.exe",
        desktop_number=3,
        screen_number=2,
        inferred_placement=PlacementSpec(
            screen=2,
            layout=PlacementLayout(mode="halves", slot=1, orientation="vertical"),
            subdivide=PlacementLayout(mode="halves", slot=1, orientation="horizontal"),
        ),
    )

    score = reusable_window_match_score(
        entry, candidate, {"personal-workspace": str(tmp_path)}, target_desktop_number=3
    )

    assert score > 0


def test_reusable_window_match_score_allows_unknown_desktop_terminal_with_changed_title(tmp_path: Path):
    entry = LaunchEntry(
        name="Personal Terminal 1",
        type="terminal",
        command="opencode",
        cwd="{personal-workspace}",
        shell="wt-pwsh",
        window_title="Personal Terminal 1",
        placement=PlacementSpec(
            screen=2,
            layout=PlacementLayout(mode="halves", slot=1, orientation="vertical"),
            subdivide=PlacementLayout(mode="halves", slot=1, orientation="horizontal"),
        ),
        window_process_name="WindowsTerminal.exe",
    )
    candidate = LiveWindow(
        hwnd=50,
        title="opencode - shared-utilities",
        process_name="WindowsTerminal.exe",
        desktop_number=None,
        screen_number=2,
        inferred_placement=PlacementSpec(
            screen=2,
            layout=PlacementLayout(mode="halves", slot=1, orientation="vertical"),
            subdivide=PlacementLayout(mode="halves", slot=1, orientation="horizontal"),
        ),
    )

    score = reusable_window_match_score(
        entry, candidate, {"personal-workspace": str(tmp_path)}, target_desktop_number=3
    )

    assert score > 0


def test_reusable_window_match_score_ranks_terminal_candidates_by_location(tmp_path: Path):
    entry = LaunchEntry(
        name="Personal Terminal 1",
        type="terminal",
        command="opencode",
        cwd="{personal-workspace}",
        shell="wt-pwsh",
        window_title="Personal Terminal 1",
        placement=PlacementSpec(
            screen=2,
            layout=PlacementLayout(mode="full", slot=1),
            subdivide=PlacementLayout(mode="halves", slot=1, orientation="vertical"),
        ),
        window_process_name="WindowsTerminal.exe",
    )
    subs = {"personal-workspace": str(tmp_path)}

    same_desktop = LiveWindow(
        hwnd=60,
        title="opencode - shared-utilities",
        process_name="WindowsTerminal.exe",
        desktop_number=3,
        screen_number=2,
        inferred_placement=PlacementSpec(
            screen=2,
            layout=PlacementLayout(mode="full", slot=1),
            subdivide=PlacementLayout(mode="halves", slot=1, orientation="vertical"),
        ),
    )
    unknown_desktop = LiveWindow(
        hwnd=61,
        title="opencode - shared-utilities",
        process_name="WindowsTerminal.exe",
        desktop_number=None,
        screen_number=2,
        inferred_placement=PlacementSpec(
            screen=2,
            layout=PlacementLayout(mode="full", slot=1),
            subdivide=PlacementLayout(mode="halves", slot=1, orientation="vertical"),
        ),
    )
    cross_desktop = LiveWindow(
        hwnd=62,
        title="opencode - shared-utilities",
        process_name="WindowsTerminal.exe",
        desktop_number=1,
        screen_number=1,
    )

    score_same = reusable_window_match_score(entry, same_desktop, subs, target_desktop_number=3)
    score_unknown = reusable_window_match_score(entry, unknown_desktop, subs, target_desktop_number=3)
    score_cross = reusable_window_match_score(entry, cross_desktop, subs, target_desktop_number=3)

    assert score_same > score_unknown > 0
    assert score_cross == 0


def test_match_reusable_window_filters_known_cross_desktop_candidates(tmp_path: Path):
    entry = LaunchEntry(
        name="Workspace Terminal 1",
        type="terminal",
        command="opencode",
        cwd="{workspace}",
        shell="wt-pwsh",
        window_title="Workspace Terminal 1",
        placement=PlacementSpec(screen=1, layout=PlacementLayout(mode="thirds", slot=1)),
        window_process_name="WindowsTerminal.exe",
    )
    same_desktop = LiveWindow(
        hwnd=10, title="Workspace Terminal 1", process_name="WindowsTerminal.exe", desktop_number=1
    )
    cross_desktop = LiveWindow(
        hwnd=20, title="Workspace Terminal 1", process_name="WindowsTerminal.exe", desktop_number=3
    )

    match = match_reusable_window(
        entry, [cross_desktop, same_desktop], {"workspace": str(tmp_path)}, target_desktop_number=1
    )

    assert match is same_desktop


def test_match_reusable_window_prefers_distinct_work_terminal_slots(tmp_path: Path):
    subs = {"workspace": str(tmp_path)}
    entries = [
        LaunchEntry(
            name="Workspace Terminal 1",
            type="terminal",
            command="opencode",
            cwd="{workspace}",
            shell="wt-pwsh",
            window_title="Workspace Terminal 1",
            placement=PlacementSpec(screen=1, layout=PlacementLayout(mode="thirds", slot=1)),
            window_process_name="WindowsTerminal.exe",
        ),
        LaunchEntry(
            name="Workspace Terminal 2",
            type="terminal",
            command="opencode",
            cwd="{workspace}",
            shell="wt-pwsh",
            window_title="Workspace Terminal 2",
            placement=PlacementSpec(
                screen=1,
                layout=PlacementLayout(mode="thirds", slot=2),
                subdivide=PlacementLayout(mode="halves", slot=1, orientation="horizontal"),
            ),
            window_process_name="WindowsTerminal.exe",
        ),
        LaunchEntry(
            name="Workspace Terminal 3",
            type="terminal",
            command="opencode",
            cwd="{workspace}",
            shell="wt-pwsh",
            window_title="Workspace Terminal 3",
            placement=PlacementSpec(
                screen=1,
                layout=PlacementLayout(mode="thirds", slot=2),
                subdivide=PlacementLayout(mode="halves", slot=2, orientation="horizontal"),
            ),
            window_process_name="WindowsTerminal.exe",
        ),
        LaunchEntry(
            name="Workspace Terminal 4",
            type="terminal",
            command="copilot",
            cwd="{workspace}",
            shell="wt-pwsh",
            window_title="Workspace Terminal 4",
            placement=PlacementSpec(screen=1, layout=PlacementLayout(mode="thirds", slot=3)),
            window_process_name="WindowsTerminal.exe",
        ),
    ]
    windows = [
        LiveWindow(
            hwnd=11,
            title="OpenCode",
            process_name="WindowsTerminal.exe",
            desktop_number=1,
            screen_number=1,
            inferred_placement=PlacementSpec(screen=1, layout=PlacementLayout(mode="thirds", slot=1)),
        ),
        LiveWindow(
            hwnd=12,
            title="OpenCode",
            process_name="WindowsTerminal.exe",
            desktop_number=1,
            screen_number=1,
            inferred_placement=PlacementSpec(
                screen=1,
                layout=PlacementLayout(mode="thirds", slot=2),
                subdivide=PlacementLayout(mode="halves", slot=1, orientation="horizontal"),
            ),
        ),
        LiveWindow(
            hwnd=13,
            title="OpenCode",
            process_name="WindowsTerminal.exe",
            desktop_number=1,
            screen_number=1,
            inferred_placement=PlacementSpec(
                screen=1,
                layout=PlacementLayout(mode="thirds", slot=2),
                subdivide=PlacementLayout(mode="halves", slot=2, orientation="horizontal"),
            ),
        ),
        LiveWindow(
            hwnd=14,
            title="GitHub Copilot",
            process_name="WindowsTerminal.exe",
            desktop_number=1,
            screen_number=1,
            inferred_placement=PlacementSpec(screen=1, layout=PlacementLayout(mode="thirds", slot=3)),
        ),
    ]

    remaining = list(windows)
    matched_hwnds: list[int] = []
    for entry in entries:
        matched = match_reusable_window(entry, remaining, subs, target_desktop_number=1)
        assert matched is not None
        matched_hwnds.append(matched.hwnd)
        remaining = [window for window in remaining if window.hwnd != matched.hwnd]

    assert matched_hwnds == [11, 12, 13, 14]


def test_match_reusable_window_prefers_existing_copilot_terminal_for_terminal_four(tmp_path: Path):
    entry = LaunchEntry(
        name="Workspace Terminal 4",
        type="terminal",
        command="copilot",
        cwd="{workspace}",
        shell="wt-pwsh",
        window_title="Workspace Terminal 4",
        placement=PlacementSpec(screen=1, layout=PlacementLayout(mode="thirds", slot=3)),
        window_process_name="WindowsTerminal.exe",
    )
    opencode_candidate = LiveWindow(
        hwnd=10,
        title="OpenCode",
        process_name="WindowsTerminal.exe",
        desktop_number=1,
        screen_number=1,
        inferred_placement=PlacementSpec(screen=1, layout=PlacementLayout(mode="thirds", slot=1)),
    )
    copilot_candidate = LiveWindow(
        hwnd=20,
        title="GitHub Copilot",
        process_name="WindowsTerminal.exe",
        desktop_number=1,
        screen_number=1,
        inferred_placement=PlacementSpec(screen=1, layout=PlacementLayout(mode="thirds", slot=3)),
    )

    match = match_reusable_window(
        entry,
        [opencode_candidate, copilot_candidate],
        {"workspace": str(tmp_path)},
        target_desktop_number=1,
    )

    assert match is copilot_candidate


def test_move_existing_window_to_entry_skips_reposition_when_inventory_already_matches(monkeypatch):
    move_calls: list[tuple[int, int]] = []

    monkeypatch.setattr("desktop_manager.win32.desktops.get_window_desktop_number", lambda _hwnd: 1)
    monkeypatch.setattr(
        "desktop_manager.win32.desktops.move_window_to_desktop_number",
        lambda hwnd, desktop_index: move_calls.append((hwnd, desktop_index)),
    )
    monkeypatch.setattr(
        "desktop_manager.win32.geometry.move_window",
        lambda *_args, **_kwargs: (_ for _ in ()).throw(AssertionError("window should not be repositioned")),
    )

    entry = LaunchEntry(
        name="Workspace Terminal 1",
        type="terminal",
        command="opencode",
        cwd="{workspace}",
        shell="wt-pwsh",
        placement=PlacementSpec(screen=1, layout=PlacementLayout(mode="thirds", slot=1)),
    )
    window = LiveWindow(
        hwnd=42,
        title="OpenCode",
        process_name="WindowsTerminal.exe",
        desktop_number=1,
        screen_number=1,
        inferred_placement=PlacementSpec(screen=1, layout=PlacementLayout(mode="thirds", slot=1)),
    )

    assert move_existing_window_to_entry(window, 1, entry) == WindowMove(False, False)
    assert move_calls == []


def test_claim_reusable_window_removes_allocated_candidate(tmp_path: Path):
    entry = LaunchEntry(
        name="Workspace Terminal 1",
        type="terminal",
        command="opencode",
        cwd="{workspace}",
        shell="wt-pwsh",
        window_title="Workspace Terminal 1",
        placement=PlacementSpec(screen=1, layout=PlacementLayout(mode="thirds", slot=1)),
        window_process_name="WindowsTerminal.exe",
    )
    matched = LiveWindow(
        hwnd=10,
        title="OpenCode",
        process_name="WindowsTerminal.exe",
        desktop_number=1,
        screen_number=1,
        inferred_placement=PlacementSpec(screen=1, layout=PlacementLayout(mode="thirds", slot=1)),
    )
    other = LiveWindow(
        hwnd=20,
        title="GitHub Copilot",
        process_name="WindowsTerminal.exe",
        desktop_number=1,
        screen_number=1,
        inferred_placement=PlacementSpec(screen=1, layout=PlacementLayout(mode="thirds", slot=3)),
    )

    claim = claim_reusable_window(
        entry,
        [matched, other],
        {"workspace": str(tmp_path)},
        target_desktop_number=1,
    )

    assert claim.window is matched
    assert claim.remaining == [other]


def _vscode_entry(name: str, window_title: str, slot: int) -> LaunchEntry:
    return LaunchEntry(
        name=name,
        type="app",
        path=r"C:\Users\me\AppData\Local\Programs\Microsoft VS Code\Code.exe",
        window_title=window_title,
        window_process_name="Code.exe",
        placement=PlacementSpec(screen=1, layout=PlacementLayout(mode="halves", slot=slot, orientation="vertical")),
    )


def test_competing_sibling_titles_excludes_own_title():
    workspace = _vscode_entry("VS Code Workspace", "(Workspace)", slot=1)
    prs = _vscode_entry("PRs To Review", "prs-to-review.md", slot=2)

    assert competing_sibling_titles([workspace, prs], workspace) == frozenset({"prs-to-review.md"})
    assert competing_sibling_titles([workspace, prs], prs) == frozenset({"(workspace)"})


def test_reusable_window_match_score_rejects_window_owned_by_titled_sibling(tmp_path: Path):
    """A VS Code Workspace entry must not grab a prs-to-review window on Code.exe alone."""
    workspace = _vscode_entry("VS Code Workspace", "(Workspace)", slot=1)
    prs = _vscode_entry("PRs To Review", "prs-to-review.md", slot=2)
    prs_window = LiveWindow(
        hwnd=70,
        title="prs-to-review.md - shared-utilities - Visual Studio Code",
        process_name="Code.exe",
        desktop_number=1,
        screen_number=1,
    )
    subs = {"workspace": str(tmp_path)}
    competing = competing_sibling_titles([workspace, prs], workspace)

    # Without the guard the process-name match would score this window for the Workspace entry.
    assert reusable_window_match_score(workspace, prs_window, subs, target_desktop_number=1) > 0
    assert (
        reusable_window_match_score(workspace, prs_window, subs, target_desktop_number=1, competing_titles=competing)
        == 0
    )
    # The rightful owner still matches.
    prs_competing = competing_sibling_titles([workspace, prs], prs)
    assert (
        reusable_window_match_score(prs, prs_window, subs, target_desktop_number=1, competing_titles=prs_competing) > 0
    )


def test_claim_reusable_window_leaves_prs_window_for_its_owner(tmp_path: Path):
    workspace = _vscode_entry("VS Code Workspace", "(Workspace)", slot=1)
    prs = _vscode_entry("PRs To Review", "prs-to-review.md", slot=2)
    entries = [workspace, prs]
    prs_window = LiveWindow(
        hwnd=71,
        title="prs-to-review.md - shared-utilities - Visual Studio Code",
        process_name="Code.exe",
        desktop_number=1,
        screen_number=1,
    )
    subs = {"workspace": str(tmp_path)}

    remaining = [prs_window]
    claimed: dict[str, LiveWindow | None] = {}
    for entry in entries:
        claim = claim_reusable_window(
            entry,
            remaining,
            subs,
            target_desktop_number=1,
            competing_titles=competing_sibling_titles(entries, entry),
        )
        remaining = claim.remaining
        claimed[entry.name] = claim.window

    assert claimed["VS Code Workspace"] is None
    assert claimed["PRs To Review"] is prs_window
