"""Tests for the display guard's state machine, driven entirely by fakes.

Nothing here touches monitors, windows, or virtual desktops. ``FakeMachine`` stands
in for the win32 ports. The tests move its clock, monitor set, and windows the way a
DisplayPort drop-out and a sleep cycle do.
"""

from __future__ import annotations

import sys
from collections.abc import Sequence
from dataclasses import dataclass, field, replace
from pathlib import Path

import pytest

from desktop_manager.display_guard import (
    DisplayGuard,
    DisplayGuardSettings,
    GuardPhase,
    MonitorBounds,
    MonitorInfo,
    MonitorSignature,
    ShowState,
    WindowPlacementRecord,
    describe_signature,
    is_lock_held,
    placement_differs,
    render_guard_task_xml,
    try_hold_lock,
)
from desktop_manager.models import Rect

pytestmark = pytest.mark.ci

G32QC = MonitorInfo(device="DISPLAY3", bounds=MonitorBounds(0, 0, 2560, 1440), primary=True)
BENQ = MonitorInfo(device="DISPLAY1", bounds=MonitorBounds(2560, -427, 4480, 653), primary=False)
IIYAMA = MonitorInfo(device="DISPLAY2", bounds=MonitorBounds(2560, 700, 4480, 1900), primary=False)
ALL_THREE: MonitorSignature = (G32QC, BENQ, IIYAMA)
G32QC_DROPPED: MonitorSignature = (BENQ, IIYAMA)


def _record(hwnd: int, rect: Rect, state: ShowState = ShowState.NORMAL, *, pid: int = 100) -> WindowPlacementRecord:
    return WindowPlacementRecord(
        hwnd=hwnd,
        pid=pid,
        title=f"Window {hwnd}",
        process_name="app.exe",
        show_state=state,
        normal_rect=rect,
        window_rect=rect,
    )


LEFT_HALF = Rect(left=0, top=0, width=1280, height=1392)
RIGHT_HALF = Rect(left=1280, top=0, width=1280, height=1392)
PARKED = Rect(left=2560, top=700, width=1280, height=1152)


@dataclass
class FakeMachine:
    signature: MonitorSignature = ALL_THREE
    windows: dict[int, WindowPlacementRecord] = field(default_factory=lambda: dict[int, WindowPlacementRecord]())
    clock: float = 1000.0
    events: int = 0
    restoring: bool = False
    applied: list[list[int]] = field(default_factory=lambda: list[list[int]]())
    messages: list[str] = field(default_factory=lambda: list[str]())
    # Simulates a monitor dropping out while a snapshot is being captured.
    change_during_capture: MonitorSignature | None = None

    def now(self) -> float:
        return self.clock

    def monitor_signature(self) -> MonitorSignature:
        return self.signature

    def display_event_count(self) -> int:
        return self.events

    def restore_in_progress(self) -> bool:
        return self.restoring

    def capture_placements(self) -> list[WindowPlacementRecord]:
        records = list(self.windows.values())
        if self.change_during_capture is not None:
            self.signature = self.change_during_capture
            self.change_during_capture = None
        return records

    def apply_placements(self, records: Sequence[WindowPlacementRecord]) -> list[int]:
        self.applied.append([record.hwnd for record in records])
        for record in records:
            self.windows[record.hwnd] = record
        return [record.hwnd for record in records]

    def log(self, message: str) -> None:
        self.messages.append(message)

    def park(self, hwnd: int) -> None:
        self.windows[hwnd] = replace(self.windows[hwnd], normal_rect=PARKED, window_rect=PARKED)


def _guard(machine: FakeMachine, **settings: float | bool) -> DisplayGuard:
    return DisplayGuard(ports=machine, settings=DisplayGuardSettings(**settings))  # type: ignore[arg-type]


def _run(guard: DisplayGuard, machine: FakeMachine, seconds: float, *, step: float = 1.0) -> None:
    elapsed = 0.0
    while elapsed < seconds:
        guard.step()
        machine.clock += step
        elapsed += step


def _settled_guard(machine: FakeMachine) -> DisplayGuard:
    guard = _guard(machine, recovery_passes=0)
    _run(guard, machine, 10)
    assert guard.phase is GuardPhase.WATCHING
    assert ALL_THREE in guard.snapshots
    return guard


class TestRecovery:
    def test_puts_windows_back_when_the_dropped_monitor_returns(self) -> None:
        machine = FakeMachine(windows={1: _record(1, LEFT_HALF), 2: _record(2, RIGHT_HALF)})
        guard = _settled_guard(machine)

        machine.signature = G32QC_DROPPED
        machine.park(1)
        machine.park(2)
        _run(guard, machine, 3)
        machine.signature = ALL_THREE
        _run(guard, machine, 8)

        assert machine.applied == [[1, 2]]
        assert machine.windows[1].normal_rect == LEFT_HALF
        assert machine.windows[2].normal_rect == RIGHT_HALF

    def test_only_moves_windows_that_differ_from_the_snapshot(self) -> None:
        machine = FakeMachine(windows={1: _record(1, LEFT_HALF), 2: _record(2, RIGHT_HALF)})
        guard = _settled_guard(machine)

        machine.signature = G32QC_DROPPED
        machine.park(2)
        _run(guard, machine, 3)
        machine.signature = ALL_THREE
        _run(guard, machine, 8)

        assert machine.applied == [[2]]

    def test_waits_for_the_topology_to_settle_through_a_flap(self) -> None:
        machine = FakeMachine(windows={1: _record(1, LEFT_HALF)})
        guard = _settled_guard(machine)

        # Drop, return, drop, return, 2-4s apart, as on 2026-10-09.
        for signature in (G32QC_DROPPED, ALL_THREE, G32QC_DROPPED):
            machine.signature = signature
            machine.park(1)
            _run(guard, machine, 3)
        assert machine.applied == []

        machine.signature = ALL_THREE
        _run(guard, machine, 5)
        assert machine.applied == []
        _run(guard, machine, 3)
        assert machine.applied == [[1]]

    def test_a_display_event_without_a_signature_change_still_triggers_a_check(self) -> None:
        # A drop-and-return faster than one poll leaves the same signature but bumps the event count.
        machine = FakeMachine(windows={1: _record(1, LEFT_HALF)})
        guard = _settled_guard(machine)

        machine.events += 2
        machine.park(1)
        _run(guard, machine, 8)

        assert machine.applied == [[1]]

    def test_a_suspend_gap_triggers_a_check(self) -> None:
        machine = FakeMachine(windows={1: _record(1, LEFT_HALF)})
        guard = _settled_guard(machine)

        machine.park(1)
        machine.clock += 3600  # the process was asleep for an hour
        _run(guard, machine, 8)

        assert machine.applied == [[1]]
        assert any("suspend gap" in message for message in machine.messages)

    def test_ignores_windows_that_closed_and_leaves_new_ones_alone(self) -> None:
        machine = FakeMachine(windows={1: _record(1, LEFT_HALF), 2: _record(2, RIGHT_HALF)})
        guard = _settled_guard(machine)

        machine.signature = G32QC_DROPPED
        del machine.windows[2]
        machine.windows[3] = _record(3, PARKED)
        machine.park(1)
        _run(guard, machine, 3)
        machine.signature = ALL_THREE
        _run(guard, machine, 8)

        assert machine.applied == [[1]]
        assert machine.windows[3].normal_rect == PARKED

    def test_a_degraded_topology_keeps_its_own_snapshot(self) -> None:
        machine = FakeMachine(windows={1: _record(1, LEFT_HALF)})
        guard = _settled_guard(machine)

        machine.signature = G32QC_DROPPED
        machine.park(1)
        _run(guard, machine, 10)

        assert guard.snapshots[ALL_THREE][0].normal_rect == LEFT_HALF
        assert guard.snapshots[G32QC_DROPPED][0].normal_rect == PARKED

    def test_dry_run_reports_without_moving(self) -> None:
        machine = FakeMachine(windows={1: _record(1, LEFT_HALF)})
        guard = _guard(machine, recovery_passes=0, dry_run=True)
        _run(guard, machine, 10)

        machine.signature = G32QC_DROPPED
        machine.park(1)
        _run(guard, machine, 3)
        machine.signature = ALL_THREE
        _run(guard, machine, 8)

        assert machine.applied == []
        assert any("Dry run: would restore 1 window(s)." in message for message in machine.messages)

    def test_follow_up_passes_catch_windows_moved_again_after_recovery(self) -> None:
        machine = FakeMachine(windows={1: _record(1, LEFT_HALF)})
        guard = _guard(machine, recovery_passes=2, recovery_pass_interval_seconds=3.0)
        _run(guard, machine, 10)

        machine.signature = G32QC_DROPPED
        machine.park(1)
        _run(guard, machine, 3)
        machine.signature = ALL_THREE
        _run(guard, machine, 7)
        assert machine.applied == [[1]]

        machine.park(1)  # Windows shuffles it again a few seconds later
        _run(guard, machine, 4)
        assert machine.applied == [[1], [1]]


class TestSnapshots:
    def test_discards_a_snapshot_when_the_topology_changes_mid_capture(self) -> None:
        machine = FakeMachine(windows={1: _record(1, LEFT_HALF)}, change_during_capture=G32QC_DROPPED)
        guard = _guard(machine)
        _run(guard, machine, 7)

        assert ALL_THREE not in guard.snapshots
        assert any("discarding" in message for message in machine.messages)

    def test_refreshes_the_snapshot_while_stable(self) -> None:
        machine = FakeMachine(windows={1: _record(1, LEFT_HALF)})
        guard = _settled_guard(machine)

        machine.windows[1] = _record(1, RIGHT_HALF)  # the user moves it deliberately
        _run(guard, machine, 6)

        assert guard.snapshots[ALL_THREE][0].normal_rect == RIGHT_HALF


class TestRestoreCoexistence:
    def test_pauses_while_a_restore_runs_and_does_not_undo_it(self) -> None:
        machine = FakeMachine(windows={1: _record(1, LEFT_HALF)})
        guard = _settled_guard(machine)

        machine.restoring = True
        _run(guard, machine, 3)
        assert guard.phase is GuardPhase.PAUSED
        machine.windows[1] = _record(1, RIGHT_HALF)
        machine.events += 1  # the restore's own window moves can fire display events
        _run(guard, machine, 3)
        machine.restoring = False
        _run(guard, machine, 10)

        assert machine.applied == []
        assert guard.snapshots[ALL_THREE][0].normal_rect == RIGHT_HALF

    def test_a_restore_drops_snapshots_for_other_topologies(self) -> None:
        machine = FakeMachine(windows={1: _record(1, LEFT_HALF)})
        guard = _settled_guard(machine)
        machine.signature = G32QC_DROPPED
        _run(guard, machine, 10)
        assert set(guard.snapshots) == {ALL_THREE, G32QC_DROPPED}

        machine.restoring = True
        _run(guard, machine, 2)
        machine.windows[1] = _record(1, RIGHT_HALF)
        machine.restoring = False
        _run(guard, machine, 10)
        machine.signature = ALL_THREE
        _run(guard, machine, 10)

        assert machine.applied == []


class TestRecycledHandles:
    def test_leaves_a_new_window_alone_when_its_hwnd_is_reused(self) -> None:
        machine = FakeMachine(windows={1: _record(1, LEFT_HALF, pid=100)})
        guard = _settled_guard(machine)
        machine.signature = G32QC_DROPPED
        _run(guard, machine, 3)

        machine.windows[1] = _record(1, PARKED, pid=200)  # old window closed, hwnd recycled
        machine.signature = ALL_THREE
        _run(guard, machine, 10)

        assert machine.applied == []


class TestPlacementDiffers:
    def test_small_frame_differences_are_ignored(self) -> None:
        nudged = Rect(left=3, top=-2, width=1284, height=1390)
        assert not placement_differs(_record(1, LEFT_HALF), _record(1, nudged))

    def test_show_state_change_counts(self) -> None:
        assert placement_differs(_record(1, LEFT_HALF), _record(1, LEFT_HALF, ShowState.MAXIMIZED))

    def test_maximized_window_on_another_monitor_counts_even_with_the_same_restore_rect(self) -> None:
        expected = replace(_record(1, LEFT_HALF, ShowState.MAXIMIZED), window_rect=Rect(-8, -8, 2576, 1408))
        actual = replace(expected, window_rect=Rect(2552, -435, 1936, 1048))
        assert placement_differs(expected, actual)

    def test_minimized_windows_ignore_the_parking_rect(self) -> None:
        expected = replace(_record(1, LEFT_HALF, ShowState.MINIMIZED), window_rect=Rect(-32000, -32000, 160, 28))
        actual = replace(expected, window_rect=Rect(-31000, -32000, 160, 28))
        assert not placement_differs(expected, actual)


class TestSignature:
    def test_device_names_do_not_affect_equality(self) -> None:
        renamed = MonitorInfo(device="DISPLAY4", bounds=G32QC.bounds, primary=True)
        assert (renamed, BENQ, IIYAMA) == ALL_THREE
        assert hash((renamed, BENQ, IIYAMA)) == hash(ALL_THREE)

    def test_describe(self) -> None:
        assert describe_signature(()) == "no monitors"
        assert describe_signature((G32QC,)) == "1 monitor(s): DISPLAY3*@0,0 2560x1440"


class TestLocks:
    def test_a_held_lock_is_reported_and_released(self, tmp_path: Path) -> None:
        lock_path = tmp_path / "restore.lock"
        assert not is_lock_held(lock_path)
        with try_hold_lock(lock_path) as acquired:
            assert acquired
            if sys.platform == "win32":
                assert is_lock_held(lock_path)
        assert not is_lock_held(lock_path)


class TestGuardTaskXml:
    def test_runs_pythonw_with_no_time_limit(self, tmp_path: Path) -> None:
        xml = render_guard_task_xml(tmp_path / "repo", tmp_path / "plan & co.json", user_id="EXAMPLE\\ryan")

        assert "<Command>" + str(tmp_path / "repo" / ".venv" / "Scripts" / "pythonw.exe") + "</Command>" in xml
        assert "workspace guard --config &quot;" in xml
        assert "plan &amp; co.json" in xml
        assert "<ExecutionTimeLimit>PT0S</ExecutionTimeLimit>" in xml
        assert "<UserId>EXAMPLE\\ryan</UserId>" in xml
