from __future__ import annotations

import contextlib
from collections.abc import Generator, Sequence
from dataclasses import dataclass, field
from pathlib import Path

import pytest
from typer.testing import CliRunner

from desktop_manager.align import (
    AlignContext,
    AlignDecision,
    AlignOutcome,
    AlignReport,
    SkipReason,
    decide_alignment,
    desktop_name_mismatch,
    match_plan_windows,
    placement_in_place,
    run_alignment,
    summary_line,
)
from desktop_manager.display_guard import ShowState
from desktop_manager.models import (
    DesktopPlan,
    LaunchEntry,
    LiveWindow,
    PlacementLayout,
    PlacementSpec,
    Rect,
    WorkspacePlan,
)

pytestmark = pytest.mark.ci

LEFT = PlacementSpec(screen=1, layout=PlacementLayout(mode="halves", slot=1, orientation="vertical"))
RIGHT = PlacementSpec(screen=1, layout=PlacementLayout(mode="halves", slot=2, orientation="vertical"))
LEFT_RECT = Rect(0, 0, 1000, 1000)
RIGHT_RECT = Rect(1000, 0, 1000, 1000)


@pytest.fixture(autouse=True)
def one_monitor(monkeypatch: pytest.MonkeyPatch) -> None:
    """Restore's reuse scoring reads the monitors; give it one that LEFT_RECT and RIGHT_RECT halve."""
    monkeypatch.setattr("desktop_manager.win32.geometry.monitor_work_areas", lambda: [Rect(0, 0, 2000, 1000)])


def fake_target_rect(placement: PlacementSpec) -> Rect:
    return LEFT_RECT if placement.layout.slot == 1 else RIGHT_RECT


def fork_entry(*, window_title: str | None = None, placement: PlacementSpec | None = LEFT) -> LaunchEntry:
    return LaunchEntry(
        name="Fork",
        path=r"C:\Fork\Fork.exe",
        window_process_name="Fork.exe",
        window_title=window_title,
        placement=placement,
    )


def slack_entry() -> LaunchEntry:
    return LaunchEntry(
        name="Slack",
        path=r"C:\Slack\slack.exe",
        window_process_name="slack.exe",
        window_title="Slack",
        placement=RIGHT,
    )


def plan_of(*desktops: tuple[str, list[LaunchEntry]]) -> WorkspacePlan:
    return WorkspacePlan(desktops=[DesktopPlan(name=name, launch=entries) for name, entries in desktops])


def fork_window(
    *, hwnd: int = 10, desktop: int | None = 1, bounds: Rect = LEFT_RECT, title: str = "Fork"
) -> LiveWindow:
    return LiveWindow(hwnd=hwnd, title=title, process_name="Fork.exe", desktop_number=desktop, bounds=bounds)


def slack_window(*, hwnd: int = 20, desktop: int | None = 2, bounds: Rect = RIGHT_RECT) -> LiveWindow:
    return LiveWindow(
        hwnd=hwnd, title="Slack | general", process_name="slack.exe", desktop_number=desktop, bounds=bounds
    )


def context(
    *,
    desktop_count: int = 2,
    show_states: dict[int, ShowState] | None = None,
    foreground: int | None = None,
) -> AlignContext:
    return AlignContext(
        desktop_count=desktop_count,
        target_rect=fake_target_rect,
        show_states=show_states or {},
        foreground_hwnd=foreground,
    )


TWO_DESKTOPS = plan_of(("Work", [fork_entry(window_title="Fork")]), ("Admin", [slack_entry()]))


def outcomes(decisions: list[AlignDecision]) -> list[tuple[str, AlignOutcome, SkipReason | None]]:
    return [(decision.entry.name, decision.outcome, decision.skip_reason) for decision in decisions]


def test_windows_on_their_desktop_and_slot_are_in_place():
    decisions = decide_alignment(TWO_DESKTOPS, [fork_window(), slack_window()], context())

    assert outcomes(decisions) == [("Fork", AlignOutcome.IN_PLACE, None), ("Slack", AlignOutcome.IN_PLACE, None)]


def test_window_on_another_desktop_moves_back_when_its_title_identifies_it():
    decisions = decide_alignment(TWO_DESKTOPS, [fork_window(desktop=2), slack_window()], context())

    fork = decisions[0]
    assert (fork.outcome, fork.move_desktop, fork.move_placement) == (AlignOutcome.MOVE, True, False)
    assert fork.describe() == "to move: Work/Fork (desktop 2 -> 1)"


def test_window_out_of_its_slot_moves_placement_only():
    decisions = decide_alignment(TWO_DESKTOPS, [fork_window(bounds=RIGHT_RECT), slack_window()], context())

    fork = decisions[0]
    assert (fork.outcome, fork.move_desktop, fork.move_placement) == (AlignOutcome.MOVE, False, True)


def test_entry_without_a_window_is_missing_and_never_launched():
    decisions = decide_alignment(TWO_DESKTOPS, [slack_window()], context())

    assert outcomes(decisions)[0] == ("Fork", AlignOutcome.MISSING, None)


def test_disabled_entries_are_ignored():
    plan = plan_of(("Work", [LaunchEntry(name="Fork", window_process_name="Fork.exe", disabled=True)]))

    assert decide_alignment(plan, [fork_window(bounds=RIGHT_RECT)], context()) == []


@pytest.mark.parametrize(
    ("show_state", "reason"),
    [(ShowState.MINIMIZED, SkipReason.MINIMIZED), (ShowState.MAXIMIZED, SkipReason.MAXIMIZED)],
)
def test_minimized_and_maximized_windows_are_skipped(show_state: ShowState, reason: SkipReason):
    decisions = decide_alignment(
        TWO_DESKTOPS, [fork_window(desktop=2), slack_window()], context(show_states={10: show_state})
    )

    assert outcomes(decisions)[0] == ("Fork", AlignOutcome.SKIPPED, reason)


def test_foreground_window_is_skipped():
    decisions = decide_alignment(TWO_DESKTOPS, [fork_window(desktop=2), slack_window()], context(foreground=10))

    assert outcomes(decisions)[0] == ("Fork", AlignOutcome.SKIPPED, SkipReason.FOREGROUND)


def test_window_whose_planned_desktop_does_not_exist_is_skipped():
    decisions = decide_alignment(TWO_DESKTOPS, [fork_window(), slack_window(desktop=1)], context(desktop_count=1))

    assert outcomes(decisions)[1] == ("Slack", AlignOutcome.SKIPPED, SkipReason.NO_DESKTOP)


def test_window_on_an_unknown_desktop_is_not_moved_across_desktops():
    decisions = decide_alignment(TWO_DESKTOPS, [fork_window(desktop=None), slack_window()], context())

    assert outcomes(decisions)[0] == ("Fork", AlignOutcome.IN_PLACE, None)


def test_process_name_alone_never_pulls_a_window_across_desktops():
    plan = plan_of(("Work", [fork_entry()]), ("Admin", []))

    decisions = decide_alignment(plan, [fork_window(desktop=2, title="Fork - some repo")], context())

    assert outcomes(decisions) == [("Fork", AlignOutcome.MISSING, None)]


def test_terminal_needs_its_own_title_to_move_across_desktops():
    terminal = LaunchEntry(
        name="Workspace Terminal 1",
        type="terminal",
        command="claude",
        window_title="Workspace Terminal 1",
        window_process_name="WindowsTerminal.exe",
        placement=LEFT,
    )
    plan = plan_of(("Work", [terminal]), ("Admin", []))
    other_agent = LiveWindow(hwnd=30, title="claude", process_name="WindowsTerminal.exe", desktop_number=2)
    own = LiveWindow(
        hwnd=31, title="Workspace Terminal 1 - claude", process_name="WindowsTerminal.exe", desktop_number=2
    )

    assert outcomes(decide_alignment(plan, [other_agent], context()))[0][1] is AlignOutcome.MISSING
    matched = match_plan_windows(plan, [other_agent, own])[0].window
    assert matched is not None and matched.hwnd == 31


def test_window_on_its_own_desktop_is_not_pulled_to_fill_another_desktop():
    plan = plan_of(("Work", [fork_entry(window_title="Fork")]), ("Admin", [fork_entry(window_title="Fork")]))

    matches = match_plan_windows(plan, [fork_window(hwnd=10, desktop=2)])

    assert [(match.desktop_index, match.window.hwnd if match.window else None) for match in matches] == [
        (1, None),
        (2, 10),
    ]


def test_one_desktop_run_keeps_only_that_desktops_decisions():
    decisions = decide_alignment(TWO_DESKTOPS, [fork_window(), slack_window()], context(), target_desktop_index=2)

    assert outcomes(decisions) == [("Slack", AlignOutcome.IN_PLACE, None)]


def test_equivalent_layout_names_count_as_in_place():
    full = PlacementSpec(screen=1, layout=PlacementLayout(mode="full", slot=1))
    split_once = PlacementSpec(
        screen=1,
        layout=PlacementLayout(mode="full", slot=1),
        subdivide=PlacementLayout(mode="full", slot=1),
    )
    window = LiveWindow(hwnd=1, title="x", inferred_placement=split_once, bounds=Rect(0, 0, 1, 1))

    assert placement_in_place(window, full, fake_target_rect)


def test_desktop_name_mismatch_blocks_but_unnamed_desktops_do_not():
    assert desktop_name_mismatch(TWO_DESKTOPS, ["work", ""]) is None
    assert desktop_name_mismatch(TWO_DESKTOPS, ["Work", "Personal"]) == (
        "desktop 2 is named 'Personal' but the plan expects 'Admin'"
    )


def decision(outcome: AlignOutcome, reason: SkipReason | None = None) -> AlignDecision:
    desktop = TWO_DESKTOPS.desktops[0]
    return AlignDecision(1, desktop.name, desktop.launch[0], None, outcome, skip_reason=reason)


def test_summary_line_lists_only_non_zero_extras():
    report = AlignReport(
        decisions=[
            decision(AlignOutcome.MOVED),
            decision(AlignOutcome.MOVED),
            decision(AlignOutcome.IN_PLACE),
            decision(AlignOutcome.MISSING),
            decision(AlignOutcome.SKIPPED, SkipReason.MINIMIZED),
        ]
    )

    assert summary_line(report, dry_run=False) == "align: 2 moved, 1 in place, 1 missing, 1 skipped (minimized)"
    assert summary_line(AlignReport(decisions=[]), dry_run=False) == "align: 0 moved, 0 in place"


def test_summary_line_breaks_down_mixed_skips_and_dry_runs():
    report = AlignReport(
        decisions=[
            decision(AlignOutcome.MOVE),
            decision(AlignOutcome.SKIPPED, SkipReason.MINIMIZED),
            decision(AlignOutcome.SKIPPED, SkipReason.FOREGROUND),
            decision(AlignOutcome.FAILED),
        ]
    )

    assert summary_line(report, dry_run=True) == (
        "align (dry run): 1 to move, 0 in place, 2 skipped (1 foreground, 1 minimized), 1 failed"
    )
    assert summary_line(AlignReport(decisions=[], blocked="restore in progress"), dry_run=False) == (
        "align: skipped (restore in progress)"
    )


@dataclass(slots=True)
class FakePorts:
    windows: list[LiveWindow]
    names: list[str] = field(default_factory=lambda: ["Work", "Admin"])
    foreground: int | None = None
    show_states: dict[int, ShowState] = field(default_factory=dict[int, ShowState])
    closed: set[int] = field(default_factory=set[int])
    desktop_errors: dict[int, str] = field(default_factory=dict[int, str])
    foreground_after_inventory: int | None = None
    desktop_moves: list[tuple[int, int]] = field(default_factory=list[tuple[int, int]])
    placements: list[tuple[int, Rect]] = field(default_factory=list[tuple[int, Rect]])
    restore_calls: list[set[int]] = field(default_factory=list[set[int]])
    excluded: set[int] = field(default_factory=set[int])
    inventoried: bool = False

    def desktop_names(self) -> list[str]:
        return self.names

    def desktop_count(self) -> int:
        return len(self.names)

    def current_desktop(self) -> int | None:
        return 1

    def foreground_hwnd(self) -> int | None:
        if self.inventoried and self.foreground_after_inventory is not None:
            return self.foreground_after_inventory
        return self.foreground

    def console_hwnd(self) -> int | None:
        return 99

    def inventory(self, *, excluded_hwnds: set[int]) -> list[LiveWindow]:
        self.excluded = excluded_hwnds
        self.inventoried = True
        return [window for window in self.windows if window.hwnd not in excluded_hwnds]

    def show_state(self, hwnd: int) -> ShowState:
        return self.show_states.get(hwnd, ShowState.NORMAL)

    def target_rect(self, placement: PlacementSpec) -> Rect:
        return fake_target_rect(placement)

    def is_window(self, hwnd: int) -> bool:
        return hwnd not in self.closed

    def move_to_desktop(self, hwnd: int, desktop_index: int) -> str | None:
        if hwnd in self.desktop_errors:
            return self.desktop_errors[hwnd]
        self.desktop_moves.append((hwnd, desktop_index))
        return None

    def place(self, hwnd: int, rect: Rect) -> str | None:
        self.placements.append((hwnd, rect))
        return None

    def ensure_desktop_count(self, count: int, settle_seconds: float = 0.65) -> None:
        while len(self.names) < count:
            self.names.append("")

    def rename_desktop(self, desktop_index: int, name: str) -> bool:
        if 1 <= desktop_index <= len(self.names):
            self.names[desktop_index - 1] = name
            return True
        return False

    def retire_desktops(self, keep_count: int, retired_names: Sequence[str]) -> list[str]:
        retired_set = {n.casefold() for n in retired_names}
        removed = [n for n in self.names if n.casefold() in retired_set]
        self.names = [n for n in self.names if n.casefold() not in retired_set]
        return removed

    def restore_view(self, *, desktop: int | None, foreground: int | None, moved_hwnds: set[int]) -> bool:
        self.restore_calls.append(moved_hwnds)
        return False


def test_run_alignment_moves_out_of_place_windows_and_excludes_the_console():
    ports = FakePorts([fork_window(desktop=2, bounds=RIGHT_RECT), slack_window()])

    report = run_alignment(TWO_DESKTOPS, ports)

    assert ports.excluded == {99}
    assert ports.desktop_moves == [(10, 1)]
    assert ports.placements == [(10, LEFT_RECT)]
    assert ports.restore_calls == [{10}]
    assert summary_line(report, dry_run=False) == "align: 1 moved, 1 in place"


def test_dry_run_moves_nothing():
    ports = FakePorts([fork_window(desktop=2), slack_window()])

    report = run_alignment(TWO_DESKTOPS, ports, dry_run=True)

    assert (ports.desktop_moves, ports.placements, ports.restore_calls) == ([], [], [])
    assert report.count(AlignOutcome.MOVE) == 1


def test_nothing_out_of_place_touches_nothing():
    ports = FakePorts([fork_window(), slack_window()])

    run_alignment(TWO_DESKTOPS, ports)

    assert (ports.desktop_moves, ports.placements, ports.restore_calls) == ([], [], [])


def test_window_closed_or_focused_since_the_inventory_is_skipped_not_moved():
    closed = FakePorts([fork_window(desktop=2), slack_window()], closed={10})
    focused = FakePorts([fork_window(desktop=2), slack_window()], foreground_after_inventory=10)

    closed_report = run_alignment(TWO_DESKTOPS, closed)
    focused_report = run_alignment(TWO_DESKTOPS, focused)

    assert outcomes(closed_report.decisions)[0] == ("Fork", AlignOutcome.SKIPPED, SkipReason.CLOSED)
    assert outcomes(focused_report.decisions)[0] == ("Fork", AlignOutcome.SKIPPED, SkipReason.FOREGROUND)
    assert (closed.desktop_moves, focused.desktop_moves, closed.restore_calls) == ([], [], [])


def test_failed_move_is_reported_and_logged():
    ports = FakePorts([fork_window(desktop=2), slack_window()], desktop_errors={10: "OSError: access denied"})
    logged: list[str] = []

    report = run_alignment(TWO_DESKTOPS, ports, log=logged.append)

    assert outcomes(report.decisions)[0] == ("Fork", AlignOutcome.FAILED, None)
    assert logged == ["FAILED desktop move Work/Fork [10]: OSError: access denied"]
    assert ports.restore_calls == []


def test_renamed_desktops_block_the_run():
    ports = FakePorts([fork_window(desktop=2)], names=["Personal", "Work"])

    report = run_alignment(TWO_DESKTOPS, ports)

    assert report.blocked == "desktop 1 is named 'Personal' but the plan expects 'Work'; run workspace restore"
    assert not ports.inventoried


def test_reconcile_desktops_aligns_misnamed_desktops_and_moves_windows():
    ports = FakePorts([fork_window(desktop=2), slack_window()], names=["Personal", "Admin"])

    report = run_alignment(TWO_DESKTOPS, ports, reconcile_desktops=True)

    assert report.blocked is None
    assert ports.names == ["Work", "Admin"]
    assert ports.desktop_moves == [(10, 1)]
    assert summary_line(report, dry_run=False) == "align: desktops: 1 renamed, 1 moved, 1 in place"


def test_reconcile_desktops_dry_run_reports_desktop_drift():
    ports = FakePorts([fork_window(desktop=2), slack_window(desktop=2)], names=["Personal", "Admin"])

    report = run_alignment(TWO_DESKTOPS, ports, reconcile_desktops=True, dry_run=True)

    assert report.blocked is None
    assert ports.names == ["Personal", "Admin"]
    assert ports.desktop_moves == []
    assert summary_line(report, dry_run=True) == "align (dry run): desktops: 1 to rename, 1 to move, 1 in place"


def test_unknown_desktop_name_raises():
    with pytest.raises(ValueError, match="not found in plan"):
        run_alignment(TWO_DESKTOPS, FakePorts([]), target_desktop="Nope")


@pytest.fixture
def align_cli(tmp_path: Path, monkeypatch: pytest.MonkeyPatch, as_windows: None) -> Path:
    config = tmp_path / "plan.json"
    config.write_text("{}", encoding="utf-8")
    monkeypatch.delenv("DESKTOP_MANAGER_LOG_DIR", raising=False)
    monkeypatch.setattr("desktop_manager.plan.load_plan", lambda _path, **_kwargs: TWO_DESKTOPS)
    monkeypatch.setattr("desktop_manager.plan.load_plan_context", lambda _path, **_kwargs: TWO_DESKTOPS)
    monkeypatch.setattr("desktop_manager.placement.monitor_shortfall", lambda _plan: None)
    monkeypatch.setattr("desktop_manager.display_guard.lock_dir", lambda: tmp_path / "locks")
    return config


def invoke_align(config: Path, *args: str):
    from desktop_manager.cli import app

    return CliRunner().invoke(app, ["workspace", "align", "--config", str(config), *args])


def test_cli_prints_one_summary_line_and_logs_it(align_cli: Path, monkeypatch: pytest.MonkeyPatch):
    ports = FakePorts([fork_window(desktop=2), slack_window(), LiveWindow(hwnd=99, title="console")])
    monkeypatch.setattr("desktop_manager.win32.align_ports.Win32AlignPorts", lambda: ports)

    result = invoke_align(align_cli)

    assert result.exit_code == 0, result.output
    assert result.output.strip() == "align: 1 moved, 1 in place"
    assert "align: 1 moved, 1 in place" in (align_cli.parent / "logs" / "align.log").read_text(encoding="utf-8")


def test_cli_exits_zero_when_restore_holds_the_lock(align_cli: Path, monkeypatch: pytest.MonkeyPatch):
    @contextlib.contextmanager
    def held(*_args: object, **_kwargs: object) -> Generator[bool]:
        yield False

    monkeypatch.setattr("desktop_manager.display_guard.hold_lock", held)

    result = invoke_align(align_cli, "--dry-run")

    assert result.exit_code == 0, result.output
    assert result.output.strip() == "align (dry run): skipped (restore in progress)"


def test_cli_exits_three_when_a_move_fails(align_cli: Path, monkeypatch: pytest.MonkeyPatch):
    ports = FakePorts([fork_window(desktop=2), slack_window()], desktop_errors={10: "OSError: denied"})
    monkeypatch.setattr("desktop_manager.win32.align_ports.Win32AlignPorts", lambda: ports)

    result = invoke_align(align_cli, "--verbose")

    assert result.exit_code == 3, result.output
    assert "failed: Work/Fork" in result.output
    assert result.output.strip().endswith("align: 0 moved, 1 in place, 1 failed")


def test_cli_rejects_an_unknown_desktop(align_cli: Path, monkeypatch: pytest.MonkeyPatch):
    monkeypatch.setattr("desktop_manager.win32.align_ports.Win32AlignPorts", lambda: FakePorts([]))

    result = invoke_align(align_cli, "--desktop", "Nope")

    assert result.exit_code == 1
    assert "not found in plan" in result.output
