from __future__ import annotations

import json
from pathlib import Path

from desktop_manager.models import LaunchEntry, LiveWindow, PlacementLayout, PlacementSpec, WindowSnapshot
from desktop_manager.visualization import captured_window_match_score
from desktop_manager.window_pid_file import pid_verified, recorded_pid, window_owner_allowed
from desktop_manager.window_reuse import claim_reusable_window

_FULL = PlacementSpec(screen=1, layout=PlacementLayout(mode="full", slot=1))


def _orca(name: str, pid_file: str) -> LaunchEntry:
    return LaunchEntry(
        name=name,
        path=r"C:\Orca\Orca.exe",
        placement=_FULL,
        window_process_name="Orca.exe",
        window_pid_file=pid_file,
    )


def _window(hwnd: int, pid: int) -> LiveWindow:
    return LiveWindow(
        hwnd=hwnd,
        title="Orca",
        pid=pid,
        process_name="Orca.exe",
        desktop_number=2,
        screen_number=1,
        inferred_placement=_FULL,
    )


def _runtime(folder: Path, pid: int) -> None:
    folder.mkdir(parents=True, exist_ok=True)
    (folder / "orca-runtime.json").write_text(json.dumps({"runtimeId": "x", "pid": pid}))


def test_recorded_pid_reads_pid_and_tolerates_missing_or_bad_files(tmp_path: Path):
    _runtime(tmp_path / "ok", 41)
    (tmp_path / "bad.json").write_text("{not json")

    assert recorded_pid(tmp_path / "ok" / "orca-runtime.json") == 41
    assert recorded_pid(tmp_path / "missing.json") is None
    assert recorded_pid(tmp_path / "bad.json") is None


def test_entry_without_pid_file_allows_any_window():
    entry = LaunchEntry(name="Fork", window_process_name="Fork.exe")

    assert window_owner_allowed(entry, 7, {})
    assert window_owner_allowed(entry, None, {})


def test_each_profile_claims_only_its_own_window_whatever_the_order(tmp_path: Path):
    _runtime(tmp_path / "orca", 100)
    _runtime(tmp_path / "orca-personal", 200)
    subs = {"data": str(tmp_path)}
    work = _orca("Work Orca", "{data}/orca/orca-runtime.json")
    personal = _orca("Personal Orca", "{data}/orca-personal/orca-runtime.json")
    personal_window, work_window = _window(2, 200), _window(1, 100)

    first = claim_reusable_window(work, [personal_window, work_window], subs, target_desktop_number=2)
    second = claim_reusable_window(personal, first.remaining, subs, target_desktop_number=2)

    assert first.window == work_window
    assert second.window == personal_window


def test_profile_that_is_not_running_claims_nothing_so_it_launches(tmp_path: Path):
    _runtime(tmp_path / "orca", 100)
    subs = {"data": str(tmp_path)}
    personal = _orca("Personal Orca", "{data}/orca-personal/orca-runtime.json")

    claim = claim_reusable_window(personal, [_window(1, 100)], subs, target_desktop_number=2)

    assert claim.window is None


def test_captured_window_for_another_profile_does_not_satisfy_the_entry(tmp_path: Path):
    _runtime(tmp_path / "orca-personal", 200)
    personal = _orca("Personal Orca", str(tmp_path / "orca-personal" / "orca-runtime.json"))

    def snapshot(pid: int) -> WindowSnapshot:
        return WindowSnapshot(title="Orca", pid=pid, process_name="Orca.exe", screen_number=1, inferred_placement=_FULL)

    assert captured_window_match_score(personal, snapshot(100), {}) == 0
    assert captured_window_match_score(personal, snapshot(200), {}) > 0


def test_pid_verified_window_on_another_desktop_is_claimed_to_be_moved(tmp_path: Path):
    _runtime(tmp_path / "orca", 100)
    subs = {"data": str(tmp_path)}
    work = _orca("Work Orca", "{data}/orca/orca-runtime.json")
    elsewhere = _window(1, 100)

    claim = claim_reusable_window(work, [elsewhere], subs, target_desktop_number=1)

    assert claim.window == elsewhere
    assert pid_verified(work, 100, subs)


def test_unverified_window_on_another_desktop_is_not_claimed(tmp_path: Path):
    _runtime(tmp_path / "orca", 100)
    subs = {"data": str(tmp_path)}
    work = _orca("Work Orca", "{data}/orca/orca-runtime.json")
    plain = LaunchEntry(name="Fork", window_process_name="Orca.exe", placement=_FULL)

    assert claim_reusable_window(work, [_window(1, 999)], subs, target_desktop_number=1).window is None
    assert claim_reusable_window(plain, [_window(1, 100)], subs, target_desktop_number=1).window is None
    assert not pid_verified(plain, 100, subs)
