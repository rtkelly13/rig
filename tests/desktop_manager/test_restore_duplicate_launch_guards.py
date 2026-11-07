"""Guards that stop a restore retry from duplicating windows it already placed.

Motivated by the 2026-08-05 logon restore, where a slow-starting Fork was launched
three times and a second VS Code window appeared because the retry pass re-matched
and relaunched an entry that was already correctly placed.
"""

from __future__ import annotations

import pytest

from desktop_manager.handlers import restore_steps
from desktop_manager.models import (
    DesktopPlan,
    LaunchEntry,
    PlacementLayout,
    PlacementSpec,
    WindowSnapshot,
)
from desktop_manager.restore_models import PlannedDesktopExecution
from desktop_manager.restore_timing import entry_window_wait_grace_seconds
from desktop_manager.visualization import entries_matched_on_planned_screen
from desktop_manager.window_reuse import competing_sibling_titles

pytestmark = pytest.mark.ci


def _full_screen(screen: int) -> PlacementSpec:
    return PlacementSpec(screen=screen, layout=PlacementLayout(mode="full"))


def test_app_entries_get_an_alive_process_grace_period():
    """An app whose process is up but window is not yet painted must not time out.

    The base window wait is sized for a warm machine; at logon it expires while the
    app is still starting, and the retry then launches a second instance.
    """
    fork = LaunchEntry(name="Fork", type="app", path=r"C:\Fork\Fork.exe", window_process_name="Fork.exe")

    assert entry_window_wait_grace_seconds(fork) > 0


def test_terminal_entries_get_no_grace_period():
    """Terminals are matched by restore token, so a missing window is a real failure."""
    terminal = LaunchEntry(
        name="Workspace Terminal 1",
        type="terminal",
        shell="wt-pwsh",
        window_process_name="WindowsTerminal.exe",
    )

    assert entry_window_wait_grace_seconds(terminal) == 0.0


def test_competing_sibling_titles_ignores_entries_for_other_processes():
    """Warp's window title must not disqualify the VS Code window it collides with.

    On the Personal desktop the Warp entry's title happened to be the repo name
    ``shared-utilities``, which also appears in the VS Code title. Treating that as a
    competing sibling reported a visible VS Code window as missing.
    """
    vscode = LaunchEntry(
        name="VS Code (shared-utilities)",
        type="app",
        path=r"C:\Code\Code.exe",
        window_title="shared-utilities",
        window_process_name="Code.exe",
    )
    warp = LaunchEntry(
        name="Personal Warp",
        type="app",
        path=r"C:\Warp\warp.exe",
        window_title="shared-utilities",
        window_process_name="warp.exe",
    )

    assert competing_sibling_titles([vscode, warp], vscode) == set()


def test_competing_sibling_titles_keeps_same_process_siblings():
    """Two VS Code entries genuinely compete, so the guard must still apply."""
    workspace = LaunchEntry(
        name="VS Code Workspace",
        type="app",
        path=r"C:\Code\Code.exe",
        window_title="shared-utilities",
        window_process_name="Code.exe",
    )
    reviews = LaunchEntry(
        name="VS Code PRs To Review",
        type="app",
        path=r"C:\Code\Code.exe",
        window_title="prs-to-review",
        window_process_name="Code.exe",
    )

    assert competing_sibling_titles([workspace, reviews], workspace) == {"prs-to-review"}


def test_entries_matched_on_planned_screen_reports_placed_entries():
    planned = DesktopPlan(
        name="Review",
        launch=[
            LaunchEntry(
                name="VS Code PRs To Review",
                type="app",
                path=r"C:\Code\Code.exe",
                window_title="prs-to-review",
                window_process_name="Code.exe",
                placement=_full_screen(1),
            ),
            LaunchEntry(
                name="Fork",
                type="app",
                path=r"C:\Fork\Fork.exe",
                window_process_name="Fork.exe",
                placement=_full_screen(2),
            ),
        ],
    )
    actual = DesktopPlan(
        name="Review",
        captured_windows=[
            WindowSnapshot(title="prs-to-review - Visual Studio Code", process_name="Code.exe", screen_number=1),
        ],
    )

    assert entries_matched_on_planned_screen(planned, actual, {}) == {"VS Code PRs To Review"}


class TestSiblingOwnedHwnds:
    """Adoption must never claim a window that plainly belongs to another entry."""

    @staticmethod
    def _review_desktop() -> DesktopPlan:
        return DesktopPlan(
            name="Review",
            launch=[
                LaunchEntry(
                    name="VS Code Workspace",
                    type="command",
                    command="code --new-window rig.code-workspace",
                    window_title="(Workspace)",
                    window_process_name="Code.exe",
                    placement=_full_screen(1),
                ),
                LaunchEntry(
                    name="VS Code PRs To Review",
                    type="app",
                    path=r"C:\Code\Code.exe",
                    window_title="prs-to-review.md",
                    window_process_name="Code.exe",
                    placement=_full_screen(3),
                ),
            ],
        )

    def test_reserves_a_siblings_window(self, monkeypatch: pytest.MonkeyPatch):
        """The 2026-08-05 failure: an entry whose own window never appeared adopted
        its already-placed sibling's window and dragged it to the wrong screen."""
        desktop = self._review_desktop()
        monkeypatch.setattr(
            "desktop_manager.win32.inventory.window_titles_for_process",
            lambda _process, **_kwargs: {
                330112: "prs-to-review.md - Visual Studio Code",
                461332: "some-other-file.py - Visual Studio Code",
            },
        )
        step = PlannedDesktopExecution(desktop=desktop, desktop_index=1, close_existing=False)

        reserved = restore_steps._sibling_owned_hwnds(step, desktop.launch[0], logger=None)

        assert reserved == {330112}

    def test_does_not_reserve_the_entrys_own_window(self, monkeypatch: pytest.MonkeyPatch):
        desktop = self._review_desktop()
        monkeypatch.setattr(
            "desktop_manager.win32.inventory.window_titles_for_process",
            lambda _process, **_kwargs: {900: "shared-utilities (Workspace) - Visual Studio Code"},
        )
        step = PlannedDesktopExecution(desktop=desktop, desktop_index=1, close_existing=False)

        assert restore_steps._sibling_owned_hwnds(step, desktop.launch[0], logger=None) == set()

    def test_ignores_siblings_running_other_processes(self, monkeypatch: pytest.MonkeyPatch):
        desktop = DesktopPlan(
            name="Personal",
            launch=[
                LaunchEntry(
                    name="VS Code (shared-utilities)",
                    type="app",
                    path=r"C:\Code\Code.exe",
                    window_process_name="Code.exe",
                    placement=_full_screen(3),
                ),
                LaunchEntry(
                    name="Personal Warp",
                    type="app",
                    path=r"C:\Warp\warp.exe",
                    window_title="shared-utilities",
                    window_process_name="warp.exe",
                    placement=_full_screen(2),
                ),
            ],
        )
        monkeypatch.setattr(
            "desktop_manager.win32.inventory.window_titles_for_process",
            lambda _process, **_kwargs: {700: "design.md - shared-utilities - Visual Studio Code"},
        )
        step = PlannedDesktopExecution(desktop=desktop, desktop_index=4, close_existing=False)

        assert restore_steps._sibling_owned_hwnds(step, desktop.launch[0], logger=None) == set()


def test_entries_matched_on_planned_screen_excludes_wrong_screen():
    """A window on the wrong screen still needs the retry to move it."""
    planned = DesktopPlan(
        name="Review",
        launch=[
            LaunchEntry(
                name="Fork",
                type="app",
                path=r"C:\Fork\Fork.exe",
                window_process_name="Fork.exe",
                placement=_full_screen(2),
            ),
        ],
    )
    actual = DesktopPlan(
        name="Review",
        captured_windows=[WindowSnapshot(title="Fork", process_name="Fork.exe", screen_number=1)],
    )

    assert entries_matched_on_planned_screen(planned, actual, {}) == set()
