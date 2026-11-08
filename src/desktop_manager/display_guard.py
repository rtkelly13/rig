"""Put windows back after a monitor drops out and returns.

Windows moves every window off a monitor that disappears, and it does not reliably
move them back when the monitor returns. A DisplayPort monitor that is "surprise
removed" on each display sleep/wake cycle scrambles a restored workspace within
minutes of the restore finishing.

The guard keeps the latest window placements for each monitor topology it has seen
settle. When the topology changes it stops taking snapshots. Once the topology
settles again, it re-applies the snapshot it holds for that topology to every window
that has moved since. A system sleep counts as a topology change even when the
monitor set looks the same afterwards, because the guard cannot see what happened
while it was suspended.

This module is the platform-independent core. ``display_guard_windows`` supplies the
real ports. The tests drive :class:`DisplayGuard` with fakes.
"""

from __future__ import annotations

import contextlib
import enum
import sys
from collections.abc import Callable, Generator, Sequence
from dataclasses import dataclass, field
from pathlib import Path
from typing import IO, Protocol

from desktop_manager import scheduled_tasks as tasks
from desktop_manager.models import Rect

GUARD_LOCK_FILE_NAME = "display-guard.lock"
RESTORE_LOCK_FILE_NAME = "workspace-restore.lock"
GUARD_LOG_FILE_NAME = "display-guard.log"


def lock_dir() -> Path:
    """Where the restore and guard locks live: one per-user place, whatever plan or
    runtime copy the restore runs from, so the guard always sees a running restore."""
    from desktop_manager.runtime import base_dir

    return base_dir() / "workspace-restore"


# Small frame and DPI rounding differences are not worth a SetWindowPlacement call.
PLACEMENT_TOLERANCE_PX = 8


@dataclass(frozen=True, slots=True)
class MonitorBounds:
    """A monitor's rectangle in virtual-screen pixels, as Win32 reports it."""

    left: int
    top: int
    right: int
    bottom: int


@dataclass(frozen=True, slots=True)
class MonitorInfo:
    """One attached monitor, as it appears in the topology signature.

    ``device`` is for logs only. A monitor that drops out can come back under a
    different DISPLAYn device name, so only its bounds and primary flag decide
    whether two topologies are the same. Every field is hashable, so signatures can
    be dict keys.
    """

    device: str = field(compare=False)
    bounds: MonitorBounds
    primary: bool


MonitorSignature = tuple[MonitorInfo, ...]


def describe_signature(signature: MonitorSignature) -> str:
    if not signature:
        return "no monitors"
    parts: list[str] = []
    for monitor in signature:
        left, top, right, bottom = monitor.bounds.left, monitor.bounds.top, monitor.bounds.right, monitor.bounds.bottom
        primary = "*" if monitor.primary else ""
        parts.append(f"{monitor.device}{primary}@{left},{top} {right - left}x{bottom - top}")
    return f"{len(signature)} monitor(s): " + "; ".join(parts)


class ShowState(enum.Enum):
    NORMAL = "normal"
    MINIMIZED = "minimized"
    MAXIMIZED = "maximized"


@dataclass(frozen=True, slots=True)
class WindowPlacementRecord:
    """A top-level window's placement, in GetWindowPlacement terms.

    ``normal_rect`` is ``rcNormalPosition``: the restored rect, which also decides
    which monitor a maximized window maximizes onto. ``window_rect`` is where the
    window actually is now, which catches a maximized window that Windows moved to
    another monitor without changing its restored rect.
    """

    hwnd: int
    # Windows recycles hwnds, so a record only matches a live window with the same
    # hwnd and owning process.
    pid: int
    title: str
    process_name: str | None
    show_state: ShowState
    normal_rect: Rect
    window_rect: Rect
    restore_to_maximized: bool = False


def _rects_close(left: Rect, right: Rect, tolerance: int = PLACEMENT_TOLERANCE_PX) -> bool:
    return (
        abs(left.left - right.left) <= tolerance
        and abs(left.top - right.top) <= tolerance
        and abs(left.width - right.width) <= tolerance
        and abs(left.height - right.height) <= tolerance
    )


def placement_differs(expected: WindowPlacementRecord, actual: WindowPlacementRecord) -> bool:
    if expected.show_state is not actual.show_state:
        return True
    if not _rects_close(expected.normal_rect, actual.normal_rect):
        return True
    # A minimized window's live rect is the off-screen parking spot, not a placement.
    if expected.show_state is ShowState.MINIMIZED:
        return False
    return not _rects_close(expected.window_rect, actual.window_rect)


class DisplayGuardPorts(Protocol):
    """Everything the guard needs from the machine."""

    def now(self) -> float:
        """Wall-clock seconds. Unlike a monotonic clock, it must keep advancing through system sleep."""
        ...

    def monitor_signature(self) -> MonitorSignature: ...

    def display_event_count(self) -> int:
        """A counter that increases on every display-change, power-resume, or session unlock event."""
        ...

    def restore_in_progress(self) -> bool: ...

    def capture_placements(self) -> list[WindowPlacementRecord]: ...

    def apply_placements(self, records: Sequence[WindowPlacementRecord]) -> list[int]:
        """Apply each record. Return the hwnds that were placed successfully."""
        ...

    def log(self, message: str) -> None: ...


class GuardPhase(enum.Enum):
    WATCHING = "watching"
    UNSETTLED = "unsettled"
    PAUSED = "paused"


@dataclass(frozen=True, slots=True)
class DisplayGuardSettings:
    # A topology must hold this long before the guard trusts it. The 2026-10-09 flaps
    # were about 4s apart, so the default must be longer than that.
    settle_seconds: float = 6.0
    snapshot_interval_seconds: float = 5.0
    # A gap between steps longer than this means the process was suspended (system
    # sleep). Treat it as a topology change.
    suspend_gap_seconds: float = 30.0
    # Re-check placements this many times after a recovery. Windows sometimes moves
    # windows again a few seconds after the monitor comes back.
    recovery_passes: int = 2
    recovery_pass_interval_seconds: float = 3.0
    dry_run: bool = False


@dataclass(slots=True)
class DisplayGuard:
    ports: DisplayGuardPorts
    settings: DisplayGuardSettings = field(default_factory=DisplayGuardSettings)
    phase: GuardPhase = GuardPhase.UNSETTLED
    snapshots: dict[MonitorSignature, list[WindowPlacementRecord]] = field(
        default_factory=dict[MonitorSignature, list[WindowPlacementRecord]]
    )
    _signature: MonitorSignature | None = None
    _event_count: int | None = None
    _last_step_at: float | None = None
    _changed_at: float = 0.0
    _last_snapshot_at: float | None = None
    _pending_recovery_passes: int = 0
    _next_recovery_pass_at: float = 0.0

    def step(self) -> None:
        now = self.ports.now()
        suspended = self._last_step_at is not None and now - self._last_step_at > self.settings.suspend_gap_seconds
        self._last_step_at = now

        signature = self.ports.monitor_signature()
        event_count = self.ports.display_event_count()

        if self.ports.restore_in_progress():
            if self.phase is not GuardPhase.PAUSED:
                self.ports.log("Workspace restore running; pausing until it finishes.")
            self.phase = GuardPhase.PAUSED
            self._observe(signature, event_count, now)
            # The restore leaves the layout it wants, so it supersedes every snapshot,
            # including those for topologies that are not current. The next settled
            # snapshot for each topology reflects it.
            self.snapshots.clear()
            self._pending_recovery_passes = 0
            return

        if self.phase is GuardPhase.PAUSED:
            self.ports.log("Workspace restore finished; resuming.")
            self.phase = GuardPhase.UNSETTLED
            self._changed_at = now

        changed = signature != self._signature or event_count != self._event_count or suspended
        if changed:
            self._note_change(signature, suspended=suspended)
            self._observe(signature, event_count, now)
            self.phase = GuardPhase.UNSETTLED
            self._pending_recovery_passes = 0
            return

        if self.phase is GuardPhase.UNSETTLED:
            if now - self._changed_at < self.settings.settle_seconds:
                return
            self.phase = GuardPhase.WATCHING
            self.ports.log(f"Display topology settled: {describe_signature(signature)}")
            if signature in self.snapshots:
                self._recover(signature)
                self._pending_recovery_passes = self.settings.recovery_passes
                self._next_recovery_pass_at = now + self.settings.recovery_pass_interval_seconds
            else:
                self._take_snapshot(signature, event_count, now)
            return

        if self._pending_recovery_passes > 0:
            if now >= self._next_recovery_pass_at:
                self._pending_recovery_passes -= 1
                self._next_recovery_pass_at = now + self.settings.recovery_pass_interval_seconds
                self._recover(signature)
            return

        if self._last_snapshot_at is None or now - self._last_snapshot_at >= self.settings.snapshot_interval_seconds:
            self._take_snapshot(signature, event_count, now)

    def _observe(self, signature: MonitorSignature, event_count: int, now: float) -> None:
        self._signature = signature
        self._event_count = event_count
        self._changed_at = now

    def _note_change(self, signature: MonitorSignature, *, suspended: bool) -> None:
        if self._signature is None:
            self.ports.log(f"Watching displays: {describe_signature(signature)}")
            return
        if suspended:
            self.ports.log("Resumed after a suspend gap; treating it as a display change.")
        if signature != self._signature:
            self.ports.log(
                f"Display topology changed: {describe_signature(self._signature)} -> {describe_signature(signature)}"
            )
        elif not suspended:
            self.ports.log("Display change event with an unchanged topology; waiting for it to settle.")

    def _take_snapshot(self, signature: MonitorSignature, event_count: int, now: float) -> None:
        records = self.ports.capture_placements()
        # A change that lands mid-capture would mix pre- and post-change placements.
        if self.ports.monitor_signature() != signature or self.ports.display_event_count() != event_count:
            self.ports.log("Display changed during snapshot; discarding it.")
            return
        first = signature not in self.snapshots
        self.snapshots[signature] = records
        self._last_snapshot_at = now
        if first:
            self.ports.log(f"Snapshot taken: {len(records)} window(s) for {describe_signature(signature)}")

    def _recover(self, signature: MonitorSignature) -> None:
        expected = self.snapshots[signature]
        live = {(record.hwnd, record.pid): record for record in self.ports.capture_placements()}
        moved = [
            record
            for record in expected
            if (record.hwnd, record.pid) in live and placement_differs(record, live[record.hwnd, record.pid])
        ]
        if not moved:
            self.ports.log("Windows already in place; nothing to restore.")
            return
        for record in moved:
            self.ports.log(f"  {describe_move(record, live[record.hwnd, record.pid])}")
        if self.settings.dry_run:
            self.ports.log(f"Dry run: would restore {len(moved)} window(s).")
            return
        placed = self.ports.apply_placements(moved)
        failed = len(moved) - len(placed)
        suffix = f", {failed} failed" if failed else ""
        self.ports.log(f"Restored {len(placed)} window(s){suffix}.")


def describe_move(expected: WindowPlacementRecord, actual: WindowPlacementRecord) -> str:
    label = expected.process_name or "?"
    title = expected.title if len(expected.title) <= 60 else expected.title[:57] + "..."
    return (
        f"{label} [{expected.hwnd}] '{title}': {actual.show_state.value} {_rect_label(actual.window_rect)} -> "
        f"{expected.show_state.value} {_rect_label(expected.normal_rect)}"
    )


def _rect_label(rect: Rect) -> str:
    return f"{rect.left},{rect.top} {rect.width}x{rect.height}"


def run_guard(
    guard: DisplayGuard,
    *,
    poll_seconds: float,
    sleep: Callable[[float], None],
    should_stop: Callable[[], bool] = lambda: False,
) -> None:
    while not should_stop():
        guard.step()
        sleep(poll_seconds)


# --- cross-process locks ---------------------------------------------------------
#
# The restore command holds RESTORE_LOCK_FILE_NAME for its whole run, so the guard
# can pause instead of fighting it. The guard holds GUARD_LOCK_FILE_NAME for its own
# lifetime so a second instance exits. Both are byte-range locks, so the OS drops
# them when a holder crashes.


def _lock_byte(handle: IO[bytes]) -> bool:
    if sys.platform != "win32":  # pragma: no cover - no cross-process locking elsewhere
        return True
    import msvcrt

    handle.seek(0)
    try:
        msvcrt.locking(handle.fileno(), msvcrt.LK_NBLCK, 1)
    except OSError:
        return False
    return True


def _unlock_byte(handle: IO[bytes]) -> None:
    if sys.platform != "win32":  # pragma: no cover
        return
    import msvcrt

    handle.seek(0)
    with contextlib.suppress(OSError):
        msvcrt.locking(handle.fileno(), msvcrt.LK_UNLCK, 1)


def _open_lock_file(path: Path) -> IO[bytes]:
    path.parent.mkdir(parents=True, exist_ok=True)
    handle = path.open("a+b")
    if handle.seek(0, 2) == 0:
        handle.write(b"0")
        handle.flush()
    return handle


@contextlib.contextmanager
def try_hold_lock(path: Path) -> Generator[bool]:
    """Hold the lock at ``path`` for the block. Yield False if another process holds it."""
    handle = _open_lock_file(path)
    acquired = _lock_byte(handle)
    try:
        yield acquired
    finally:
        if acquired:
            _unlock_byte(handle)
        handle.close()


@contextlib.contextmanager
def hold_lock(path: Path, *, timeout_seconds: float, sleep: Callable[[float], None]) -> Generator[bool]:
    """Like :func:`try_hold_lock`, but retry for ``timeout_seconds``.

    The guard probes the restore lock by taking it for an instant, so a restore that
    starts at the same moment needs a short retry, not a single attempt.
    """
    handle = _open_lock_file(path)
    acquired = _lock_byte(handle)
    waited = 0.0
    while not acquired and waited < timeout_seconds:
        sleep(0.05)
        waited += 0.05
        acquired = _lock_byte(handle)
    try:
        yield acquired
    finally:
        if acquired:
            _unlock_byte(handle)
        handle.close()


def is_lock_held(path: Path) -> bool:
    """Return whether another process currently holds the lock at ``path``."""
    if not path.exists():
        return False
    with try_hold_lock(path) as acquired:
        return not acquired


# --- scheduled task ---------------------------------------------------------------

GUARD_TASK_NAME = "Workspace Display Guard"


def render_guard_task_xml(
    repo_root: Path,
    config_path: Path,
    *,
    user_id: str,
    delay: str = "PT30S",
    task_name: str = GUARD_TASK_NAME,
) -> str:
    """Render Task Scheduler XML that keeps the display guard running for the session.

    The guard runs under ``pythonw.exe`` so no console window sits on a desktop for
    the whole session. It writes to its own log file instead. There is no execution
    time limit. Restart-on-failure brings it back if it crashes. A second copy exits
    straight away because of the guard lock, so the task can be started by hand at
    any time.
    """
    pythonw = repo_root / ".venv" / "Scripts" / "pythonw.exe"
    return tasks.render_task_xml(
        tasks.ScheduledTask(
            uri=task_name,
            description="Put windows back after a monitor drops out or the machine wakes.",
            user_id=user_id,
            trigger=tasks.logon_trigger(user_id, delay=delay),
            command=str(pythonw),
            arguments=f'-m desktop_manager.cli workspace guard --config "{config_path}"',
            execution_time_limit="PT0S",
            priority=4,
            restart_on_failure=tasks.RestartOnFailure(interval="PT1M", count=10),
            working_directory=str(repo_root),
        )
    )
