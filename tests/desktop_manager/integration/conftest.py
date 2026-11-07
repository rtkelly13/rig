"""Shared fixtures for integration tests that exercise desktop-switching orchestration.

Every test in this package needs the win32 desktop navigation layer faked out so
that no real virtual-desktop switches happen.  The ``fake_desktop_navigation``
autouse fixture patches the common set of functions once, and individual tests
can layer on additional patches (e.g. ``inventory_reusable_windows``) as needed.
"""

from __future__ import annotations

from collections.abc import Generator
from typing import Any

import pytest

from desktop_manager.launching import LaunchedProcess


@pytest.fixture(autouse=True)
def close_launched_processes() -> Generator[list[LaunchedProcess]]:
    """Reap anything a test really launched.

    Most tests here patch ``launch_entry``, but any that exercise a non-dry-run
    restore without patching it start real apps — which is how a full suite run ends
    up leaving a pile of terminals on the developer's desktop. Wrapping every test in
    cleanup mode means an unpatched launch is a tidied-up mistake rather than litter.

    ``track_launched_processes`` records the launcher PID and image name for each start
    and kills the tree on the way out. That reaping is a backstop, not a licence: Warp and
    Windows Terminal hand their window to an existing instance before the launcher exits,
    so those launches cannot be reaped by PID at all and escape as real windows. The one
    test that used to do this leaked a Warp window per suite run for weeks.

    So a recorded launch also fails the test. Patch ``desktop_manager.launching.launch_entry``
    in the test — asserting on what *would* have been launched is both faster and a
    stronger check than letting it happen.
    """
    from desktop_manager.launching import track_launched_processes

    with track_launched_processes() as launched:
        yield launched
        escaped = sorted({item.image_name for item in launched})
        if escaped:
            pytest.fail(
                f"test really launched {', '.join(escaped)}; patch "
                "desktop_manager.launching.launch_entry instead of starting real processes"
            )


@pytest.fixture(autouse=True)
def no_terminal_command_upgrades(monkeypatch: pytest.MonkeyPatch) -> None:
    """Stop a restore under test from running real CLI self-updates.

    ``restore_workspace_with_options`` kicks off ``winget``/``npm`` upgrades for each
    terminal tool on background threads, and the launch path then *joins* those
    threads. In a test run that means minutes of real package-manager traffic per
    test — the bulk of the desktop_manager suite's ~10 minute wall time — plus stray
    processes and thread exceptions decoding winget's console output.
    """
    monkeypatch.setattr("desktop_manager.terminal_init.start_terminal_command_upgrades_for_plan", lambda _plan: None)
    monkeypatch.setattr("desktop_manager.launching.ensure_terminal_command_initialized", lambda _entry: None)


@pytest.fixture(autouse=True)
def no_app_update_settling(monkeypatch: pytest.MonkeyPatch) -> None:
    """Stop a restore under test from driving a real Warp self-update.

    ``restore_workspace_with_options`` settles pending app updates before placing
    anything, and settling a *staged* Warp update means launching ``warp.exe`` to make
    the installer run, then polling for the updater and the returning window. Under test
    that is both a real terminal on the developer's desktop and — because ``time.sleep``
    is patched to a no-op here — a busy-spin that burns the full poll timeout in real
    time. ``desktop_manager.app_updates`` is covered directly by its own unit tests.
    """
    monkeypatch.setattr(
        "desktop_manager.handlers.restore_handler._settle_pending_app_updates",
        lambda **_kwargs: False,
    )


@pytest.fixture(autouse=True)
def fake_desktop_navigation(monkeypatch: pytest.MonkeyPatch) -> Generator[dict[str, list[Any]]]:
    """Patch all win32 desktop-switching helpers so tests never touch real desktops.

    Yields a *tracking dict* that tests can inspect to verify navigation calls:

    * ``"switch_leftmost"`` – list of step counts passed to ``switch_leftmost``
    * ``"move_right"``      – list of step counts passed to ``move_right``
    * ``"go_to_desktop"``   – list of desktop numbers passed to ``go_to_desktop_number``
    * ``"sleeps"``          – list of sleep durations
    * ``"rename_desktop"``  – list of ``(desktop_index, name)`` renames
    * ``"retire_desktops"`` – list of ``(keep_count, retired_names)`` retire calls

    ``rename_desktop`` must be faked here: these tests restore the sample plan
    (desktops ``Work``/``Admin``) for real, and an unpatched rename relabelled the
    developer's live desktops 1 and 2 on every suite run.
    """
    tracker: dict[str, list[Any]] = {
        "switch_leftmost": [],
        "move_right": [],
        "go_to_desktop": [],
        "sleeps": [],
        "rename_desktop": [],
        "retire_desktops": [],
    }

    def _fake_rename(desktop_index: int, name: str) -> bool:
        tracker["rename_desktop"].append((desktop_index, name))
        return True

    def _fake_retire(keep_count: int, retired_names: list[str]) -> list[str]:
        tracker["retire_desktops"].append((keep_count, list(retired_names)))
        return []

    monkeypatch.setattr("desktop_manager.win32.ensure_windows", lambda: None)
    monkeypatch.setattr("desktop_manager.win32.desktops.get_virtual_desktop_count", lambda: 4)
    monkeypatch.setattr("desktop_manager.win32.desktops.ensure_desktop_count", lambda _count, _delay: None)
    monkeypatch.setattr("desktop_manager.win32.desktops.ensure_current_desktop_number", lambda *_args, **_kwargs: True)
    monkeypatch.setattr("desktop_manager.win32.desktops.switch_leftmost", tracker["switch_leftmost"].append)
    monkeypatch.setattr("desktop_manager.win32.desktops.move_right", tracker["move_right"].append)
    monkeypatch.setattr("desktop_manager.win32.desktops.go_to_desktop_number", tracker["go_to_desktop"].append)
    monkeypatch.setattr("desktop_manager.win32.desktops.rename_desktop", _fake_rename)
    monkeypatch.setattr("desktop_manager.win32.desktops.retire_switched_off_desktops", _fake_retire)
    monkeypatch.setattr("time.sleep", tracker["sleeps"].append)

    yield tracker


@pytest.fixture(autouse=True)
def no_live_window_seams(monkeypatch: pytest.MonkeyPatch) -> None:
    """Keep a restore under test away from the developer's real windows.

    A retry attempt asks ``is_process_running`` whether an entry's app is already up,
    and on a machine with Windows Terminal open the real answer is yes — so the restore
    adopted a live window instead of the faked launch the test was asserting on.
    Reusing a window calls ``move_existing_window_to_entry``, which drives pyvda and UI
    Automation against whatever real HWND the fake inventory happened to name.

    Treat every process as not running, and fail any test that reaches the real move
    rather than letting the COM error it raises steer the restore into a relaunch.
    ``pytest.fail`` raises a ``BaseException``, so the restore's own ``except Exception``
    cannot swallow it. Tests that exercise reuse patch the move themselves.
    """

    def _refuse_real_move(*_args: object, **_kwargs: object) -> None:
        pytest.fail("test reached the real move_existing_window_to_entry; patch it in the test")

    monkeypatch.setattr("desktop_manager.win32.inventory.is_process_running", lambda _name: False)
    monkeypatch.setattr("desktop_manager.window_reuse.move_existing_window_to_entry", _refuse_real_move)
