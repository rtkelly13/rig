"""Orca updates applied by rig, so both Orca profiles come back after an update."""

from __future__ import annotations

import base64
import hashlib
import json
from pathlib import Path

import pytest

from desktop_manager import orca_update
from desktop_manager.models import DesktopPlan, LaunchEntry, WorkspacePlan
from desktop_manager.orca_update import (
    OrcaUpdateState,
    StagedOrcaUpdate,
    entry_targets_orca,
    installer_matches_sha512,
    orca_entries,
    settle_orca_update,
    staged_orca_update,
    update_orca,
    version_key,
)
from desktop_manager.win32 import file_version as file_version_module
from rig.proc import Ran

pytestmark = pytest.mark.ci

INSTALLER = Path("orca-windows-setup.exe")


def _staged(version: str | None = "1.4.251.0", **overrides: object) -> StagedOrcaUpdate:
    values: dict[str, object] = {"version": version, "installer": INSTALLER, "sha512": "x"}
    values.update(overrides)
    return StagedOrcaUpdate(**values)  # type: ignore[arg-type]


def _state(staged: StagedOrcaUpdate | None = None, **overrides: object) -> OrcaUpdateState:
    values: dict[str, object] = {"installed_version": "1.4.250", "staged": staged}
    values.update(overrides)
    return OrcaUpdateState(**values)  # type: ignore[arg-type]


def _plan() -> WorkspacePlan:
    return WorkspacePlan(
        desktops=[
            DesktopPlan(
                name="Work",
                launch=[
                    LaunchEntry(name="Work Orca", path="{orca-path}", window_process_name="Orca.exe"),
                    LaunchEntry(name="Terminal", path="wt.exe", window_process_name="WindowsTerminal.exe"),
                    LaunchEntry(
                        name="Personal Orca",
                        path="{orca-path}",
                        args=["--user-data-dir=x"],
                        window_process_name="Orca.exe",
                    ),
                ],
            )
        ]
    )


@pytest.fixture(autouse=True)
def _forget_attempted_versions() -> None:
    orca_update._ATTEMPTED_VERSIONS.clear()


@pytest.fixture
def _no_waiting(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setattr(orca_update, "_POLL_INTERVAL_SECONDS", 0.0)
    monkeypatch.setattr(orca_update, "_SELF_INSTALL_APPEAR_SECONDS", 0.0)


class TestPending:
    def test_installer_matching_the_installed_build_is_not_pending(self):
        """The pending folder is never cleared after an install, so 1.4.250.0 staged vs 1.4.250 is current."""
        assert _state(_staged("1.4.250.0")).pending is False

    def test_newer_staged_build_is_pending(self):
        assert _state(_staged("1.4.251.0")).pending is True

    def test_older_staged_build_is_not_pending(self):
        assert _state(_staged("1.4.249.0")).pending is False

    def test_versions_compare_numerically(self):
        assert _state(_staged("1.10.0.0"), installed_version="1.9.300").pending is True

    def test_nothing_staged_or_not_installed_is_not_pending(self):
        assert _state(None).pending is False
        assert _state(_staged(None)).pending is False
        assert _state(_staged(), installed_version=None).pending is False

    @pytest.mark.parametrize(
        ("version", "expected"),
        [("1.4.250", (1, 4, 250)), ("1.4.250.0", (1, 4, 250)), ("v2.0", (2,)), ("1.4.x", None), ("", None)],
    )
    def test_version_key(self, version: str, expected: tuple[int, ...] | None):
        assert version_key(version) == expected


class TestStagedUpdate:
    def _write_pending(self, tmp_path: Path, info: dict[str, object], *, installer: bool = True) -> Path:
        pending = tmp_path / "orca-updater" / "pending"
        pending.mkdir(parents=True)
        (pending / "update-info.json").write_text(json.dumps(info), encoding="utf-8")
        if installer:
            (pending / "orca-windows-setup.exe").write_bytes(b"setup")
        return pending

    def test_reads_installer_and_version(self, tmp_path: Path, monkeypatch: pytest.MonkeyPatch):
        monkeypatch.setenv("LOCALAPPDATA", str(tmp_path))
        pending = self._write_pending(
            tmp_path, {"fileName": "orca-windows-setup.exe", "sha512": "abc", "isAdminRightsRequired": False}
        )
        monkeypatch.setattr(file_version_module, "file_version", lambda _path: "1.4.251.0")

        staged = staged_orca_update()

        assert staged == StagedOrcaUpdate("1.4.251.0", pending / "orca-windows-setup.exe", "abc", False)

    def test_missing_installer_means_nothing_staged(self, tmp_path: Path, monkeypatch: pytest.MonkeyPatch):
        monkeypatch.setenv("LOCALAPPDATA", str(tmp_path))
        self._write_pending(tmp_path, {"fileName": "orca-windows-setup.exe"}, installer=False)

        assert staged_orca_update() is None

    def test_file_name_cannot_escape_the_pending_folder(self, tmp_path: Path, monkeypatch: pytest.MonkeyPatch):
        monkeypatch.setenv("LOCALAPPDATA", str(tmp_path))
        (tmp_path / "evil.exe").write_bytes(b"x")
        self._write_pending(tmp_path, {"fileName": "..\\..\\evil.exe"}, installer=False)
        monkeypatch.setattr(file_version_module, "file_version", lambda _path: "9.9.9.9")

        assert staged_orca_update() is None

    def test_sha512_check(self, tmp_path: Path):
        installer = tmp_path / "setup.exe"
        installer.write_bytes(b"payload")
        good = base64.b64encode(hashlib.sha512(b"payload").digest()).decode()

        assert installer_matches_sha512(installer, good) is True
        assert installer_matches_sha512(installer, base64.b64encode(b"other").decode()) is False
        assert installer_matches_sha512(installer, None) is False
        assert installer_matches_sha512(installer, "not base64!") is False


class TestRunInstaller:
    def test_unverified_installer_is_never_run(self, monkeypatch: pytest.MonkeyPatch):
        monkeypatch.setattr(orca_update, "installer_matches_sha512", lambda *_args: False)
        ran: list[list[str]] = []
        monkeypatch.setattr(orca_update.proc, "run", lambda argv, **_kw: ran.append(argv) or Ran(0, "", ""))

        assert orca_update.run_orca_installer(_staged()) is False
        assert ran == []

    def test_admin_installer_is_not_run_unattended(self, monkeypatch: pytest.MonkeyPatch):
        monkeypatch.setattr(orca_update, "installer_matches_sha512", lambda *_args: True)
        ran: list[list[str]] = []
        monkeypatch.setattr(orca_update.proc, "run", lambda argv, **_kw: ran.append(argv) or Ran(0, "", ""))

        assert orca_update.run_orca_installer(_staged(admin_required=True)) is False
        assert ran == []

    def test_runs_silently_without_relaunching(self, monkeypatch: pytest.MonkeyPatch):
        monkeypatch.setattr(orca_update, "installer_matches_sha512", lambda *_args: True)
        ran: list[list[str]] = []
        monkeypatch.setattr(orca_update.proc, "run", lambda argv, **_kw: ran.append(argv) or Ran(0, "", ""))

        assert orca_update.run_orca_installer(_staged()) is True
        assert ran == [[str(INSTALLER), "--updated", "/S"]]


class TestSettleAtRestore:
    def test_up_to_date_does_nothing(self, monkeypatch: pytest.MonkeyPatch):
        monkeypatch.setattr(orca_update, "orca_update_state", lambda: _state(_staged("1.4.250.0")))
        monkeypatch.setattr(orca_update, "run_orca_installer", lambda *_a, **_k: pytest.fail("must not install"))

        assert settle_orca_update() is False

    def test_installs_while_no_orca_is_running(self, monkeypatch: pytest.MonkeyPatch):
        monkeypatch.setattr(orca_update, "orca_update_state", lambda: _state(_staged()))
        monkeypatch.setattr(orca_update, "installed_orca_version", lambda: "1.4.251")
        installs: list[StagedOrcaUpdate] = []
        monkeypatch.setattr(orca_update, "run_orca_installer", lambda staged, **_k: installs.append(staged) or True)

        assert settle_orca_update() is True
        assert installs == [_staged()]

    def test_running_orca_leaves_the_update_staged(self, monkeypatch: pytest.MonkeyPatch):
        """The installer kills every Orca.exe and relaunches one profile -- the bug being fixed."""
        monkeypatch.setattr(orca_update, "orca_update_state", lambda: _state(_staged(), orca_running=True))
        monkeypatch.setattr(orca_update, "run_orca_installer", lambda *_a, **_k: pytest.fail("must not install"))
        messages: list[str] = []

        assert settle_orca_update(log=messages.append) is False
        assert any("update-orca" in message for message in messages)

    def test_a_failed_install_is_not_retried_in_the_same_run(self, monkeypatch: pytest.MonkeyPatch):
        monkeypatch.setattr(orca_update, "orca_update_state", lambda: _state(_staged()))
        attempts: list[bool] = []
        monkeypatch.setattr(orca_update, "run_orca_installer", lambda *_a, **_k: attempts.append(True) and False)

        assert settle_orca_update() is False
        assert settle_orca_update() is False
        assert len(attempts) == 1

    def test_waits_out_an_installer_already_running(self, monkeypatch: pytest.MonkeyPatch):
        monkeypatch.setattr(orca_update, "orca_update_state", lambda: _state(_staged(), installer_running=True))
        waited: list[bool] = []
        monkeypatch.setattr(orca_update, "wait_for_orca_installer", lambda **_k: waited.append(True) or True)

        assert settle_orca_update() is True
        assert waited == [True]


@pytest.mark.usefixtures("_no_waiting")
class TestUpdateCommand:
    def _wire(self, monkeypatch: pytest.MonkeyPatch, states: list[OrcaUpdateState]) -> list[str]:
        calls: list[str] = []
        monkeypatch.setattr(orca_update, "orca_update_state", lambda: states.pop(0) if len(states) > 1 else states[0])
        monkeypatch.setattr(orca_update, "orca_installer_running", lambda: False)
        monkeypatch.setattr(orca_update, "installed_orca_version", lambda: "1.4.251")
        monkeypatch.setattr(orca_update, "close_all_orca", lambda **_k: calls.append("close") or "closed")
        monkeypatch.setattr(orca_update, "run_orca_installer", lambda *_a, **_k: calls.append("install") or True)
        monkeypatch.setattr(
            orca_update, "_relaunch_entry", lambda _plan, item, **_k: calls.append(f"launch {item.entry}") or []
        )
        return calls

    def test_nothing_to_do_when_up_to_date(self, monkeypatch: pytest.MonkeyPatch):
        calls = self._wire(monkeypatch, [_state(_staged("1.4.250.0"), orca_running=True)])

        result = update_orca(_plan())

        assert result.ok is True
        assert "nothing to do" in result.actions[0]
        assert calls == []

    def test_closes_installs_and_relaunches_both_profiles(self, monkeypatch: pytest.MonkeyPatch):
        pending = _state(_staged(), orca_running=True)
        calls = self._wire(monkeypatch, [pending, _state(_staged())])

        result = update_orca(_plan())

        assert result.ok is True
        assert calls == ["close", "install", "launch Work Orca", "launch Personal Orca"]

    def test_skips_install_when_orca_installed_it_on_quit(self, monkeypatch: pytest.MonkeyPatch):
        pending = _state(_staged(), orca_running=True)
        calls = self._wire(monkeypatch, [pending, _state(_staged(), installed_version="1.4.251")])

        update_orca(_plan())

        assert calls == ["close", "launch Work Orca", "launch Personal Orca"]

    def test_failed_install_still_relaunches_and_reports_failure(self, monkeypatch: pytest.MonkeyPatch):
        calls = self._wire(monkeypatch, [_state(_staged(), orca_running=True), _state(_staged())])
        monkeypatch.setattr(orca_update, "run_orca_installer", lambda *_a, **_k: calls.append("install") and False)

        result = update_orca(_plan())

        assert result.ok is False
        assert calls == ["close", "install", "launch Work Orca", "launch Personal Orca"]

    def test_relaunches_both_profiles_after_waiting_out_orcas_own_installer(self, monkeypatch: pytest.MonkeyPatch):
        """Orca's installer restores at most the profile that started it, so rig still relaunches every profile."""
        in_flight = _state(_staged(), installer_running=True, orca_running=False)
        done = _state(_staged(), installed_version="1.4.251", orca_running=True)
        calls = self._wire(monkeypatch, [in_flight, done, done])

        result = update_orca(_plan())

        assert result.ok is True
        assert calls == ["close", "launch Work Orca", "launch Personal Orca"]

    def test_installer_that_outlasts_the_wait_blocks_install_and_relaunch(self, monkeypatch: pytest.MonkeyPatch):
        calls = self._wire(monkeypatch, [_state(_staged(), installer_running=True)])
        monkeypatch.setattr(orca_update, "wait_for_orca_installer", lambda **_k: False)

        result = update_orca(_plan())

        assert result.ok is False
        assert calls == []

    def test_self_started_installer_that_outlasts_the_wait_blocks_install(self, monkeypatch: pytest.MonkeyPatch):
        calls = self._wire(monkeypatch, [_state(_staged(), orca_running=True)])
        monkeypatch.setattr(orca_update, "_installer_appears", lambda **_k: True)
        monkeypatch.setattr(orca_update, "wait_for_orca_installer", lambda **_k: False)

        result = update_orca(_plan())

        assert result.ok is False
        assert calls == ["close"]

    def test_dry_run_touches_nothing(self, monkeypatch: pytest.MonkeyPatch):
        calls = self._wire(monkeypatch, [_state(_staged(), orca_running=True)])

        result = update_orca(_plan(), dry_run=True)

        assert calls == []
        assert any("would relaunch `Personal Orca`" in action for action in result.actions)


class TestPlanEntries:
    def test_orca_entries_in_plan_order(self):
        assert [item.entry for item in orca_entries(_plan())] == ["Work Orca", "Personal Orca"]

    def test_disabled_entries_are_not_relaunched(self):
        plan = _plan()
        plan.desktops[0].launch[2].disabled = True

        assert [item.entry for item in orca_entries(plan)] == ["Work Orca"]

    @pytest.mark.parametrize(
        ("process_name", "path", "expected"),
        [
            ("Orca.exe", "{orca-path}", True),
            (None, r"C:\Users\x\AppData\Local\Programs\orca\Orca.exe", True),
            ("WindowsTerminal.exe", "wt.exe", False),
            (None, None, False),
        ],
    )
    def test_entry_targets_orca(self, process_name: str | None, path: str | None, expected: bool):
        assert entry_targets_orca(process_name, path) is expected
