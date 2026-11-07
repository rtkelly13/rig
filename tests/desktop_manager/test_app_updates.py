"""Warp self-update detection, so an update cannot hijack a restore mid-run."""

from __future__ import annotations

import json
import time
from pathlib import Path

import pytest

from desktop_manager import app_updates
from desktop_manager.app_updates import (
    WarpUpdateState,
    entry_targets_warp,
    installed_warp_version,
    plan_launches_warp,
    settle_warp_update,
    staged_warp_installers,
    warp_update_log_active,
    warp_update_state,
)

pytestmark = pytest.mark.ci


def _write_version(tmp_path: Path, version: str, *, bom: bool = True) -> None:
    version_file = tmp_path / "Programs" / "Warp" / "resources" / "bundled" / "metadata" / "version.json"
    version_file.parent.mkdir(parents=True, exist_ok=True)
    # Warp writes this file with a UTF-8 BOM.
    version_file.write_text(json.dumps({"warp_version": version}), encoding="utf-8-sig" if bom else "utf-8")


def _state(**overrides: object) -> WarpUpdateState:
    defaults: dict[str, object] = {
        "installed_version": "v0.2026.07.29.09.05.stable_02",
        "staged_version": None,
        "staged_installer": None,
        "updater_running": False,
        "update_log_active": False,
    }
    defaults.update(overrides)
    return WarpUpdateState(**defaults)  # type: ignore[arg-type]


@pytest.fixture(autouse=True)
def _forget_triggered_versions() -> None:
    """``_TRIGGERED_VERSIONS`` is process-wide, so leaking it across tests hides bugs."""
    app_updates._TRIGGERED_VERSIONS.clear()


class TestPendingDetection:
    def test_no_staged_installer_is_not_pending(self):
        assert _state().pending is False

    def test_pending_update_is_not_triggerable_while_warp_holds_the_mutex(self):
        pending = _state(staged_version="v0.2026.08.05.09.03.stable_01")

        assert pending.triggerable is True
        assert _state(staged_version="v0.2026.08.05.09.03.stable_01", warp_running=True).triggerable is False

    def test_nothing_staged_is_never_triggerable(self):
        assert _state(warp_running=False).triggerable is False

    def test_stale_installer_matching_installed_build_is_not_pending(self):
        """Applied installers are never cleaned out of %TEMP%.

        Three were sitting there on 2026-08-05, so presence alone cannot mean
        "update pending" -- otherwise every restore would trigger a pointless
        Warp launch and wait.
        """
        state = _state(staged_version="v0.2026.07.29.09.05.stable_02")

        assert state.pending is False

    def test_newer_staged_build_is_pending(self):
        state = _state(staged_version="v0.2026.08.05.08.11.stable_01")

        assert state.pending is True

    def test_running_updater_counts_as_in_progress(self):
        assert _state(updater_running=True).in_progress is True

    def test_recent_log_write_counts_as_in_progress(self):
        """Catches the tail of an update whose installer already exited."""
        assert _state(update_log_active=True).in_progress is True


class TestFilesystemProbes:
    def test_installed_version_reads_through_the_bom(self, tmp_path: Path, monkeypatch: pytest.MonkeyPatch):
        _write_version(tmp_path, "v0.2026.07.29.09.05.stable_02")
        monkeypatch.setenv("LOCALAPPDATA", str(tmp_path))

        assert installed_warp_version() == "v0.2026.07.29.09.05.stable_02"

    def test_missing_version_file_returns_none(self, tmp_path: Path, monkeypatch: pytest.MonkeyPatch):
        monkeypatch.setenv("LOCALAPPDATA", str(tmp_path))

        assert installed_warp_version() is None

    def test_staged_installers_are_newest_first(self, tmp_path: Path, monkeypatch: pytest.MonkeyPatch):
        monkeypatch.setenv("TEMP", str(tmp_path))
        older = tmp_path / ".tmpv0.2026.07.01.00.00.stable_01-WarpSetup.exe"
        newer = tmp_path / "tmpv0.2026.08.05.08.11.stable_01-WarpSetup.exe"
        older.write_bytes(b"")
        newer.write_bytes(b"")
        now = time.time()
        import os

        os.utime(older, (now - 600, now - 600))
        os.utime(newer, (now, now))

        versions = [staged.version for staged in staged_warp_installers()]

        assert versions == ["v0.2026.08.05.08.11.stable_01", "v0.2026.07.01.00.00.stable_01"]

    def test_unrelated_temp_files_are_ignored(self, tmp_path: Path, monkeypatch: pytest.MonkeyPatch):
        monkeypatch.setenv("TEMP", str(tmp_path))
        (tmp_path / "SomeOtherSetup.exe").write_bytes(b"")

        assert staged_warp_installers() == []

    def test_update_log_activity_uses_the_time_window(self, tmp_path: Path, monkeypatch: pytest.MonkeyPatch):
        monkeypatch.setenv("LOCALAPPDATA", str(tmp_path))
        log_path = tmp_path / "warp" / "Warp" / "data" / "logs" / "warp_update.log"
        log_path.parent.mkdir(parents=True, exist_ok=True)
        log_path.write_text("update", encoding="utf-8")

        assert warp_update_log_active(within_seconds=600.0) is True

        import os

        stale = time.time() - 3600
        os.utime(log_path, (stale, stale))

        assert warp_update_log_active(within_seconds=90.0) is False


class TestSettle:
    def test_no_pending_update_does_nothing(self, monkeypatch: pytest.MonkeyPatch):
        monkeypatch.setattr(app_updates, "warp_update_state", lambda: _state())
        launched: list[bool] = []
        monkeypatch.setattr(app_updates, "_trigger_warp_launch", lambda **_kwargs: launched.append(True) or True)

        assert settle_warp_update() is False
        assert launched == []

    def test_pending_update_is_applied_before_returning(self, monkeypatch: pytest.MonkeyPatch):
        """The update must run up front, not mid-restore.

        Warp only applies a staged build when a running Warp exits, so leaving it to
        the restore's own first Warp launch meant the swap-and-relaunch landed after
        three desktops were already placed -- and Warp's session restore then dumped
        every window onto the active desktop.
        """
        monkeypatch.setattr(
            app_updates,
            "warp_update_state",
            lambda: _state(staged_version="v0.2026.08.05.08.11.stable_01"),
        )
        calls: list[str] = []
        monkeypatch.setattr(app_updates, "_trigger_warp_launch", lambda **_kwargs: (calls.append("launch"), True)[1])
        monkeypatch.setattr(
            app_updates, "_wait_for_warp_updater_to_start", lambda **_kwargs: (calls.append("start"), True)[1]
        )
        monkeypatch.setattr(
            app_updates,
            "wait_for_warp_updater_to_finish",
            lambda **_kwargs: (calls.append("finish"), True)[1],
        )
        monkeypatch.setattr(app_updates, "_wait_for_warp_window", lambda **_kwargs: (calls.append("window"), True)[1])
        monkeypatch.setattr(app_updates, "installed_warp_version", lambda: "v0.2026.08.05.08.11.stable_01")

        assert settle_warp_update() is True
        assert calls == ["launch", "start", "finish", "window"]

    def test_pending_update_is_not_triggered_while_warp_is_running(self, monkeypatch: pytest.MonkeyPatch):
        """Launching Warp cannot apply an update that a running Warp is blocking.

        The installer waits on ``Local\\WarpStable_SingleInstance``, so with Warp already
        up the launch only hands a window to the running instance -- then the 45s updater
        wait times out and the state is still pending, so the next restore does it again.
        On 2026-08-09 that left 42 stray Warp windows spaced at exactly 45s.
        """
        monkeypatch.setattr(
            app_updates,
            "warp_update_state",
            lambda: _state(staged_version="v0.2026.08.05.09.03.stable_01", warp_running=True),
        )
        launched: list[bool] = []
        monkeypatch.setattr(app_updates, "_trigger_warp_launch", lambda **_kwargs: launched.append(True) or True)
        monkeypatch.setattr(
            app_updates,
            "_wait_for_warp_updater_to_start",
            lambda **_kwargs: pytest.fail("waited for an updater that cannot start"),
        )

        assert settle_warp_update() is False
        assert launched == []

    def test_a_staged_version_is_only_triggered_once_per_process(self, monkeypatch: pytest.MonkeyPatch):
        """A trigger that produced no updater will not produce one on a retry either."""
        monkeypatch.setattr(
            app_updates,
            "warp_update_state",
            lambda: _state(staged_version="v0.2026.08.05.09.03.stable_01"),
        )
        launched: list[bool] = []
        monkeypatch.setattr(app_updates, "_trigger_warp_launch", lambda **_kwargs: launched.append(True) or True)
        monkeypatch.setattr(app_updates, "_wait_for_warp_updater_to_start", lambda **_kwargs: False)

        assert settle_warp_update() is True
        assert settle_warp_update() is False
        assert launched == [True]

    def test_update_already_running_is_waited_out_without_relaunching(self, monkeypatch: pytest.MonkeyPatch):
        monkeypatch.setattr(app_updates, "warp_update_state", lambda: _state(updater_running=True))
        launched: list[bool] = []
        monkeypatch.setattr(app_updates, "_trigger_warp_launch", lambda **_kwargs: launched.append(True) or True)
        monkeypatch.setattr(app_updates, "wait_for_warp_updater_to_finish", lambda **_kwargs: True)
        monkeypatch.setattr(app_updates, "_wait_for_warp_window", lambda **_kwargs: True)

        assert settle_warp_update() is True
        assert launched == []

    def test_missing_updater_after_trigger_still_reports_warp_started(self, monkeypatch: pytest.MonkeyPatch):
        """Warp may decline the update this run; it is still running and reusable."""
        monkeypatch.setattr(
            app_updates,
            "warp_update_state",
            lambda: _state(staged_version="v0.2026.08.05.08.11.stable_01"),
        )
        monkeypatch.setattr(app_updates, "_trigger_warp_launch", lambda **_kwargs: True)
        monkeypatch.setattr(app_updates, "_wait_for_warp_updater_to_start", lambda **_kwargs: False)

        assert settle_warp_update() is True

    def test_trigger_can_be_disabled(self, monkeypatch: pytest.MonkeyPatch):
        monkeypatch.setattr(
            app_updates,
            "warp_update_state",
            lambda: _state(staged_version="v0.2026.08.05.08.11.stable_01"),
        )
        launched: list[bool] = []
        monkeypatch.setattr(app_updates, "_trigger_warp_launch", lambda **_kwargs: launched.append(True) or True)

        assert settle_warp_update(allow_trigger=False) is False
        assert launched == []


class TestEntryTargeting:
    @pytest.mark.parametrize(
        ("process_name", "path", "expected"),
        [
            ("warp.exe", None, True),
            ("Warp.exe", None, True),
            (None, r"C:\Users\me\AppData\Local\Programs\Warp\warp.exe", True),
            ("WindowsTerminal.exe", r"C:\Windows\System32\wt.exe", False),
            (None, None, False),
        ],
    )
    def test_entry_targets_warp(self, process_name: str | None, path: str | None, expected: bool):
        assert entry_targets_warp(process_name, path) is expected

    def test_a_plan_launches_warp_only_while_its_feature_is_on(self):
        from desktop_manager.models import DesktopPlan, LaunchEntry, WorkspacePlan
        from desktop_manager.plan import apply_feature_flags

        warp = LaunchEntry(name="warp", path="warp.exe", window_process_name="warp.exe", feature="warp-terminals")
        plan = WorkspacePlan(
            desktops=[DesktopPlan(name="Work", launch=[warp, LaunchEntry(name="wt", path="wt.exe")])],
            features={"warp-terminals": False},
        )

        assert plan_launches_warp(plan) is True
        assert plan_launches_warp(apply_feature_flags(plan)) is False
        assert plan_launches_warp(apply_feature_flags(plan, enable=["warp-terminals"])) is True


def test_state_describe_includes_the_decision_inputs(monkeypatch: pytest.MonkeyPatch, tmp_path: Path):
    """The restore log needs enough to explain why it did or did not wait."""
    monkeypatch.setenv("LOCALAPPDATA", str(tmp_path))
    monkeypatch.setenv("TEMP", str(tmp_path))
    _write_version(tmp_path, "v0.2026.07.29.09.05.stable_02")
    monkeypatch.setattr(app_updates, "warp_updater_running", lambda: False)
    # warp_running also reads the live process table, which is Windows-only.
    monkeypatch.setattr(app_updates, "warp_running", lambda: False)

    description = warp_update_state().describe()

    assert "installed=v0.2026.07.29.09.05.stable_02" in description
    assert "staged=none" in description
    assert "pending=False" in description
