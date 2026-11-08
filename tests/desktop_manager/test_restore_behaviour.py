"""What restore does to a desktop, run against ``FakeDesktop`` instead of Windows.

Each scenario sets a machine up, runs the real restore handler against it, and
asserts on where the windows ended up and what was launched. None of them patch
a module, so they run the same on every platform.
"""

from __future__ import annotations

from pathlib import Path

from desktop_manager.handlers.restore_handler import restore_workspace_with_options
from desktop_manager.models import DesktopPlan, LaunchEntry, PlacementLayout, PlacementSpec, WorkspacePlan
from desktop_manager.plan import create_restore_run_logger
from tests.fakes.desktop import FakeDesktop

LEFT = PlacementSpec(screen=1, layout=PlacementLayout(mode="halves", slot=1, orientation="vertical"))
RIGHT = PlacementSpec(screen=1, layout=PlacementLayout(mode="halves", slot=2, orientation="vertical"))
FULL_2 = PlacementSpec(screen=2, layout=PlacementLayout(mode="full", slot=1))


def _app(name: str, exe: str, placement: PlacementSpec | None = LEFT) -> LaunchEntry:
    return LaunchEntry(
        name=name,
        type="app",
        path=rf"C:\Apps\{exe}",
        window_process_name=exe,
        window_title=name,
        placement=placement,
        delay_ms=0,
    )


def _plan(*desktops: DesktopPlan, retired: list[str] | None = None) -> WorkspacePlan:
    return WorkspacePlan(desktops=list(desktops), switch_delay_ms=0, retired_desktop_names=retired or [])


def _restore(
    plan: WorkspacePlan,
    machine: FakeDesktop,
    *,
    dry_run: bool = False,
    close_existing: bool = False,
    diff_existing: bool = False,
) -> list[str]:
    return restore_workspace_with_options(
        plan, dry_run=dry_run, close_existing=close_existing, diff_existing=diff_existing, port=machine
    )


# ------------------------------------------------------------------ dry run


def test_dry_run_plans_reuse_launch_and_orphans_without_touching_the_machine() -> None:
    editor = _app("Editor", "Code.exe")
    notes = _app("Notes", "Obsidian.exe", RIGHT)
    machine = FakeDesktop()
    machine.add_window("Editor", "Code.exe", placement=RIGHT)
    machine.add_window("Unrelated chat", "Slack.exe", placement=FULL_2)
    before = [(window.hwnd, window.desktop, window.placement) for window in machine.windows]

    actions = _restore(
        _plan(DesktopPlan(name="Work", launch=[editor, notes])), machine, dry_run=True, diff_existing=True
    )

    assert actions[0] == "Desktop 1: Work"
    assert actions[1] == "  - reuse window `Editor` from desktop 1"
    assert actions[2].startswith("  - app `Notes`")
    assert actions[3] == "  - orphan open window `Unrelated chat [Slack.exe]` on desktop 1 screen 2"
    assert machine.launches == []
    assert [(window.hwnd, window.desktop, window.placement) for window in machine.windows] == before


def test_dry_run_with_close_reports_the_close() -> None:
    actions = _restore(
        _plan(DesktopPlan(name="Work", launch=[_app("Editor", "Code.exe")])),
        FakeDesktop(),
        dry_run=True,
        close_existing=True,
    )

    assert actions[:2] == ["Desktop 1: Work", "  - close existing windows on this desktop"]


def test_dry_run_lists_the_desktops_a_switched_off_feature_retires() -> None:
    actions = _restore(_plan(DesktopPlan(name="Work"), retired=["Personal"]), FakeDesktop(), dry_run=True)

    assert actions[0] == "Remove switched-off desktop(s), if present: Personal"


# ---------------------------------------------------------------- execution


def test_launches_a_missing_entry_into_its_planned_slot() -> None:
    machine = FakeDesktop(startup_seconds=2.0)

    _restore(_plan(DesktopPlan(name="Work", launch=[_app("Editor", "Code.exe")])), machine, diff_existing=True)

    assert machine.launches == ["Editor"]
    (editor,) = machine.windows
    assert (editor.desktop, editor.placement) == (1, LEFT)


def test_reuses_an_open_window_instead_of_launching_another() -> None:
    machine = FakeDesktop()
    open_editor = machine.add_window("Editor", "Code.exe", placement=RIGHT)

    _restore(_plan(DesktopPlan(name="Work", launch=[_app("Editor", "Code.exe")])), machine, diff_existing=True)

    assert machine.launches == []
    assert machine.windows == [open_editor]
    assert open_editor.placement == LEFT


def test_leaves_an_entry_already_in_its_slot_where_it_is() -> None:
    machine = FakeDesktop()
    machine.add_window("Editor", "Code.exe", placement=LEFT)

    actions = _restore(
        _plan(DesktopPlan(name="Work", launch=[_app("Editor", "Code.exe")])), machine, diff_existing=True
    )

    assert machine.launches == []
    assert len(machine.windows) == 1
    assert "  - reuse window `Editor` from desktop 1" in actions


def test_relaunches_when_the_window_to_reuse_vanishes() -> None:
    machine = FakeDesktop()
    doomed = machine.add_window("Editor", "Code.exe", placement=RIGHT)
    machine.vanishing.add(doomed.hwnd)

    _restore(_plan(DesktopPlan(name="Work", launch=[_app("Editor", "Code.exe")])), machine, diff_existing=True)

    assert machine.launches == ["Editor"]
    (editor,) = machine.windows
    assert editor.hwnd != doomed.hwnd
    assert (editor.desktop, editor.placement) == (1, LEFT)


def test_a_failed_reuse_is_not_retried_against_the_same_window() -> None:
    first = _app("Editor", "Code.exe", LEFT)
    second = _app("Docs", "Code.exe", RIGHT)
    machine = FakeDesktop()
    doomed = machine.add_window("Editor", "Code.exe", placement=FULL_2)
    machine.vanishing.add(doomed.hwnd)

    actions = _restore(_plan(DesktopPlan(name="Work", launch=[first, second])), machine, diff_existing=True)

    assert [action for action in actions if "reuse window" in action] == ["  - reuse window `Editor` from desktop 1"]
    assert machine.launches == ["Editor", "Docs"]
    placed = sorted((window.title, window.placement) for window in machine.windows if window.placement is not None)
    assert placed == [("Docs", RIGHT), ("Editor", LEFT)]


def test_retries_a_desktop_whose_entry_did_not_appear(tmp_path: Path) -> None:
    machine = FakeDesktop()
    machine.windowless_launches["Editor"] = 1
    logger = create_restore_run_logger(tmp_path / "workspace.json")

    restore_workspace_with_options(
        _plan(DesktopPlan(name="Work", launch=[_app("Editor", "Code.exe")])),
        port=machine,
        diff_existing=True,
        logger=logger,
    )

    assert machine.launches == ["Editor", "Editor"]
    (editor,) = [window for window in machine.windows if window.visible_at <= machine.now]
    assert (editor.desktop, editor.placement) == (1, LEFT)
    log_text = logger.run_log_path.read_text(encoding="utf-8")
    assert "Desktop 1 completion check failed on attempt 1" in log_text
    assert "Retrying desktop 1: attempt 2 of 3" in log_text
    assert "Desktop 1 completion check passed on attempt 2" in log_text


def test_a_retry_waits_for_the_instance_still_starting_instead_of_launching_again() -> None:
    """Three attempts against a slow-starting app once spawned three processes (2026-08-05)."""
    machine = FakeDesktop()
    machine.hung_launches.add("Editor")

    _restore(_plan(DesktopPlan(name="Work", launch=[_app("Editor", "Code.exe")])), machine, diff_existing=True)

    assert machine.launches == ["Editor"]


def test_close_existing_keeps_the_invoking_window() -> None:
    machine = FakeDesktop()
    invoking = machine.add_window("claude", "WindowsTerminal.exe", placement=RIGHT)
    machine.invoking_hwnds.add(invoking.hwnd)
    machine.add_window("Stray chat", "Slack.exe", placement=FULL_2)

    _restore(_plan(DesktopPlan(name="Work", launch=[_app("Editor", "Code.exe")])), machine, close_existing=True)

    assert sorted(window.process_name for window in machine.windows) == ["Code.exe", "WindowsTerminal.exe"]
    assert (invoking.desktop, invoking.placement) == (1, RIGHT)


def test_the_invoking_window_is_never_claimed_for_an_entry_that_looks_like_it() -> None:
    """The session driving the restore runs in a terminal that matches a terminal entry."""
    machine = FakeDesktop()
    invoking = machine.add_window("Shell", "WindowsTerminal.exe", placement=RIGHT)
    machine.invoking_hwnds.add(invoking.hwnd)
    shell = _app("Shell", "WindowsTerminal.exe", LEFT)

    actions = _restore(_plan(DesktopPlan(name="Work", launch=[shell])), machine, diff_existing=True)

    assert not any("reuse window" in action for action in actions)
    assert machine.launches == ["Shell"]
    assert (invoking.desktop, invoking.placement) == (1, RIGHT)


def test_returns_to_the_desktop_it_started_on() -> None:
    machine = FakeDesktop(desktop_names=["Work", "Admin", "Spare"], current=3)
    plan = _plan(
        DesktopPlan(name="Work", launch=[_app("Editor", "Code.exe")]),
        DesktopPlan(name="Admin", launch=[_app("Mail", "Outlook.exe")]),
    )

    actions = _restore(plan, machine, diff_existing=True)

    assert machine.current == 3
    assert actions[-1] == "Return to starting desktop 3"
    assert {(window.title, window.desktop) for window in machine.windows} == {("Editor", 1), ("Mail", 2)}


def test_names_the_planned_desktops_and_retires_switched_off_ones() -> None:
    machine = FakeDesktop(desktop_names=["Desktop 1", "Personal", "Desktop 3"])
    stranded = machine.add_window("Diary", "Notes.exe", desktop=2)
    plan = _plan(DesktopPlan(name="Work"), DesktopPlan(name="Admin"), retired=["Personal"])

    _restore(plan, machine, diff_existing=True)

    assert machine.desktop_names == ["Work", "Admin"]
    assert stranded.desktop == 2
