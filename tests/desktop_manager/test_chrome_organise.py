"""The `organise` command end to end: prune, then group, traced, against an in-memory Chrome.

Nothing here touches Chrome: the driver is a `FakeChrome` passed in through the
command's context, and the presence and history seams are replaced, so the flow
runs on any platform.
"""

from __future__ import annotations

from collections.abc import Iterator
from dataclasses import replace
from functools import partial
from itertools import count
from pathlib import Path

import pytest
import typer
from click.testing import Result
from typer.testing import CliRunner

from desktop_manager.chrome.backend import ChromeSession
from desktop_manager.chrome.driver import ChromeReadError, CloseOutcome, GroupOutcome, SavedDeleteOutcome
from desktop_manager.chrome.saved_groups import SavedGroup
from desktop_manager.chrome.tabs import ChromeTab, ChromeWindow
from rig import tracing
from tests.desktop_manager.fake_chrome import FakeChrome
from tests.trace_observations import ObservedRun, observe_runs

pytestmark = pytest.mark.ci

SEP = "·"
SCHEDULED = ("--unattended", "--trigger", "scheduled")
_ids = count(500)


def _pr_window(hwnd: int, *numbers: int) -> ChromeWindow:
    tabs = tuple(
        ChromeTab(
            runtime_id=(42, hwnd, next(_ids)),
            window_hwnd=hwnd,
            index=index,
            name=f"Change {number}",
            title=f"Change {number} by rtkelly13 {SEP} Pull Request #{number} {SEP} acme-corp/data-platform",
            selected=index == 0,
            pinned=False,
            loaded=True,
            audible=False,
        )
        for index, number in enumerate(numbers)
    )
    return ChromeWindow(
        hwnd=hwnd,
        title="w",
        profile_dir="Profile 4",
        profile_name="Ryan",
        age_rank=hwnd,
        minimized=False,
        desktop_number=1,
        tabs=tabs,
    )


class _NoInput:
    def mark(self) -> None:
        pass

    def user_moved(self) -> bool:
        return False


class _InputFor:
    """An InputWatch whose first `attempts` grouping attempts each see input at once."""

    attempts = 0

    def mark(self) -> None:
        pass

    def user_moved(self) -> bool:
        if type(self).attempts > 0:
            type(self).attempts -= 1
            return True
        return False


@pytest.fixture
def chrome(monkeypatch: pytest.MonkeyPatch, tmp_path: Path, as_windows: None) -> Iterator[FakeChrome]:
    """A Chrome with one window of three PR tabs, with presence and history replaced."""
    from desktop_manager.chrome import history, presence

    del as_windows
    monkeypatch.setenv("LOCALAPPDATA", str(tmp_path))
    monkeypatch.setattr(history, "hosts_by_title", lambda profile_dir, titles: {})
    monkeypatch.setattr(presence, "session_locked", lambda: False)
    monkeypatch.setattr(presence, "devices_in_use", lambda: [])
    monkeypatch.setattr(presence, "InputWatch", _NoInput)
    yield FakeChrome(windows=[_pr_window(1001, 11, 12, 13)])
    tracing.shutdown_tracing()
    tracing.set_trace_dir(None)


def _organise(chrome: FakeChrome, *args: str, scheduled: bool = True) -> Result:
    """Run `organise` on `chrome` without GitHub, as the hourly task does unless `scheduled` is False.

    A failed run raises as the command did: `typer.Exit`, or the exception itself."""
    from desktop_manager.chrome.cli import chrome_tabs_app

    command = ["organise", "--no-github", *(SCHEDULED if scheduled else ()), *args]
    result = CliRunner().invoke(chrome_tabs_app, command, obj=ChromeSession(lambda: chrome))
    if isinstance(result.exception, SystemExit):
        if result.exit_code:
            raise typer.Exit(result.exit_code)
    elif result.exception is not None:
        raise result.exception
    return result


def _run(isolate_trace_output: Path) -> ObservedRun:
    tracing.shutdown_tracing()
    [run] = observe_runs(isolate_trace_output, "chrome_tabs.organise")
    return run


def _blocked_reasons(run: ObservedRun) -> list[str]:
    return [
        event.attributes["reason"]
        for span in run.spans
        if span.name == "chrome_tabs.wait_for_idle"
        for event in span.events
        if event.name == "check" and event.attributes.get("blocked")
    ]


class TestOrganiseFlow:
    def test_an_unattended_run_emits_the_whole_span_tree(
        self, chrome: FakeChrome, isolate_trace_output: Path, monkeypatch: pytest.MonkeyPatch
    ) -> None:
        from desktop_manager.chrome import presence

        idle = iter([5.0, 20.0])
        monkeypatch.setattr(presence, "idle_seconds", lambda: next(idle, 600.0))
        clock = [0.0]

        def sleep(seconds: float) -> None:
            clock[0] += seconds

        monkeypatch.setattr(
            presence,
            "wait_until_clear",
            partial(presence.wait_until_clear, sleep=sleep, clock=lambda: clock[0]),
        )

        _organise(chrome)

        run = _run(isolate_trace_output)
        names = [span.name for span in sorted(run.spans, key=lambda span: span.start_ns)]
        assert [name for name in names if name != "chrome_tabs.read_windows"] == [
            "chrome_tabs.plan_closes",
            "chrome_tabs.close_tabs",
            "chrome_tabs.plan_grouping",
            "chrome_tabs.wait_for_idle",
            "chrome_tabs.plan_grouping",
            "chrome_tabs.group",
            "chrome_tabs.group_window",
        ]
        reads = sorted(
            (span for span in run.spans if span.name == "chrome_tabs.read_windows"), key=lambda s: s.start_ns
        )
        phases = [span.attributes.get("read.phase") for span in reads]
        assert phases[:2] == ["initial", "regroup"]
        assert "wait_check" in phases
        assert phases[-1] == "replan"

        root = run.root
        assert root.attributes.get("chrome.trigger") == "scheduled"
        assert root.attributes.get("chrome.unattended") is True
        assert root.attributes.get("run.outcome") == "ok"
        assert root.attributes.get("group.outcome") == "grouped"
        assert (root.attributes.get("group.planned"), root.attributes.get("group.moved")) == (3, 3)
        assert root.attributes.get("wait.outcome") == "clear"
        assert root.attributes.get("wait.checks") == 3
        assert len(_blocked_reasons(run)) == 2

        [wait] = [span for span in run.spans if span.name == "chrome_tabs.wait_for_idle"]
        assert [event.attributes["blocked"] for event in wait.events] == [True, True, False]
        [window] = [span for span in run.spans if span.name == "chrome_tabs.group_window"]
        assert window.attributes.get("group.moved") == 3

    def test_input_pauses_grouping_with_a_note_and_it_goes_again_once_left_alone(
        self, chrome: FakeChrome, isolate_trace_output: Path, monkeypatch: pytest.MonkeyPatch
    ) -> None:
        from desktop_manager.chrome import presence, unattended

        monkeypatch.setattr(presence, "idle_seconds", lambda: 600.0)
        monkeypatch.setattr(presence, "InputWatch", _InputFor)
        monkeypatch.setattr(_InputFor, "attempts", 1)
        notes: list[object] = []
        monkeypatch.setattr(unattended, "show_paused_note", lambda **kwargs: notes.append(kwargs))

        _organise(chrome)

        run = _run(isolate_trace_output)
        assert len(notes) == 1
        assert run.root.attributes.get("run.outcome") == "ok"
        assert run.root.attributes.get("group.outcome") == "grouped"
        assert (run.root.attributes.get("group.moved"), run.root.attributes.get("group.retries")) == (3, 1)
        reads = sorted(
            (span for span in run.spans if span.name == "chrome_tabs.read_windows"), key=lambda s: s.start_ns
        )
        assert [span.attributes.get("read.phase") for span in reads][-2:] == ["wait_check", "retry"]

    def test_input_that_keeps_coming_leaves_grouping_to_the_next_run_without_failing(
        self, chrome: FakeChrome, isolate_trace_output: Path, monkeypatch: pytest.MonkeyPatch
    ) -> None:
        from desktop_manager.chrome import presence, unattended

        monkeypatch.setattr(presence, "idle_seconds", lambda: 600.0)
        monkeypatch.setattr(presence, "InputWatch", _InputFor)
        monkeypatch.setattr(_InputFor, "attempts", 99)
        notes: list[object] = []
        monkeypatch.setattr(unattended, "show_paused_note", lambda **kwargs: notes.append(kwargs))

        _organise(chrome)

        run = _run(isolate_trace_output)
        assert len(notes) == unattended.INPUT_RETRIES
        assert run.root.attributes.get("run.outcome") == "ok"
        assert run.root.attributes.get("group.outcome") == "interrupted"
        assert run.root.attributes.get("group.retries") == unattended.INPUT_RETRIES
        assert run.root.attributes.get("group.stopped") == "keyboard or mouse input again after 2 retries"

    def test_grouping_that_fails_by_itself_fails_an_unattended_run(
        self, chrome: FakeChrome, isolate_trace_output: Path, monkeypatch: pytest.MonkeyPatch
    ) -> None:
        from desktop_manager.chrome import presence, unattended

        monkeypatch.setattr(presence, "idle_seconds", lambda: 600.0)
        chrome.group_outcome = GroupOutcome(False, "menu never opened", 0.0)
        notes: list[object] = []
        monkeypatch.setattr(unattended, "show_paused_note", lambda **kwargs: notes.append(kwargs))

        with pytest.raises(typer.Exit):
            _organise(chrome)

        run = _run(isolate_trace_output)
        assert notes == []
        assert run.root.attributes.get("run.outcome") == "failed"
        assert run.root.attributes.get("group.outcome") == "stopped"
        assert run.root.attributes.get("group.retries") == 0

    def test_a_live_call_leaves_chrome_alone(
        self, chrome: FakeChrome, isolate_trace_output: Path, monkeypatch: pytest.MonkeyPatch
    ) -> None:
        from desktop_manager.chrome import presence

        monkeypatch.setattr(presence, "idle_seconds", lambda: 600.0)
        monkeypatch.setattr(presence, "devices_in_use", lambda: ["microphone"])

        _organise(chrome)

        run = _run(isolate_trace_output)
        assert run.root.attributes.get("run.outcome") == "skipped_live"
        assert "microphone" in str(run.root.attributes.get("run.skipped"))
        assert [span.name for span in run.spans if span.name != "chrome_tabs.read_windows"] == []

    def test_chrome_that_will_not_read_cleanly_is_skipped_when_scheduled(
        self, chrome: FakeChrome, isolate_trace_output: Path, monkeypatch: pytest.MonkeyPatch
    ) -> None:
        def unreadable() -> list[ChromeWindow]:
            raise ChromeReadError("Chrome's tab strip kept changing during the read")

        monkeypatch.setattr(chrome, "list_windows", unreadable)

        result = _organise(chrome)

        run = _run(isolate_trace_output)
        assert "Skipped" in result.output
        assert chrome.log == []
        assert run.root.attributes.get("run.outcome") == "skipped_unreadable"
        assert "kept changing" in str(run.root.attributes.get("run.skipped"))

    def test_chrome_that_will_not_read_cleanly_fails_an_attended_run(
        self, chrome: FakeChrome, isolate_trace_output: Path, monkeypatch: pytest.MonkeyPatch
    ) -> None:
        def unreadable() -> list[ChromeWindow]:
            raise ChromeReadError("Chrome's tab strip kept changing during the read")

        monkeypatch.setattr(chrome, "list_windows", unreadable)

        with pytest.raises(typer.Exit):
            _ = _organise(chrome, scheduled=False)

        assert _run(isolate_trace_output).root.attributes.get("run.outcome") == "failed"

    def test_a_tab_that_would_not_close_warns_without_failing_the_run(
        self,
        chrome: FakeChrome,
        isolate_trace_output: Path,
        monkeypatch: pytest.MonkeyPatch,
    ) -> None:
        from desktop_manager.chrome import presence

        monkeypatch.setattr(presence, "idle_seconds", lambda: 600.0)
        [window] = chrome.windows
        page = replace(
            window.tabs[-1],
            name="Merge Queue Handbook",
            title="Merge Queue Handbook",
            url="https://example.com/a",
            selected=False,
        )
        twice = (replace(page, runtime_id=(42, 1001, 1), index=3), replace(page, runtime_id=(42, 1001, 2), index=4))
        chrome.windows = [replace(window, tabs=(*window.tabs, *twice))]
        chrome.close_outcome = CloseOutcome(False, "tab is still present", 0.1)

        result = _organise(chrome)

        run = _run(isolate_trace_output)
        assert run.root.attributes.get("run.outcome") == "partial"
        assert (run.root.attributes.get("tabs.closed"), run.root.attributes.get("tabs.close_failed")) == (0, 1)
        assert result.output.splitlines()[-1] == "Warning: 1 tab(s) not closed; the next run tries again"

    def test_grouping_that_switched_desktops_switches_back(
        self, chrome: FakeChrome, isolate_trace_output: Path, monkeypatch: pytest.MonkeyPatch
    ) -> None:
        from desktop_manager.chrome import presence

        monkeypatch.setattr(presence, "idle_seconds", lambda: 600.0)

        group_tab = chrome.group_tab

        def switching(tab: ChromeTab, *, title: str, new: bool, home_hwnd: int | None = None) -> GroupOutcome:
            chrome.desktops.number = 3
            return group_tab(tab, title=title, new=new, home_hwnd=home_hwnd)

        monkeypatch.setattr(chrome, "group_tab", switching)

        _organise(chrome)

        assert (chrome.desktops.number, chrome.desktops.went) == (1, [1])
        run = _run(isolate_trace_output)
        [restored] = [span for span in run.spans if span.name == "chrome_tabs.restore_desktop"]
        assert (restored.attributes.get("desktop.from"), restored.attributes.get("desktop.to")) == (3, 1)

    def test_a_run_that_stays_on_its_desktop_does_not_switch(
        self, chrome: FakeChrome, isolate_trace_output: Path, monkeypatch: pytest.MonkeyPatch
    ) -> None:
        from desktop_manager.chrome import presence

        monkeypatch.setattr(presence, "idle_seconds", lambda: 600.0)

        _organise(chrome)

        assert chrome.desktops.went == []

    def test_a_run_that_created_a_group_consolidates_saved_groups(
        self, chrome: FakeChrome, isolate_trace_output: Path, monkeypatch: pytest.MonkeyPatch
    ) -> None:
        from desktop_manager.chrome import presence

        monkeypatch.setattr(presence, "idle_seconds", lambda: 600.0)

        _organise(chrome, "--consolidate-saved")

        run = _run(isolate_trace_output)
        assert run.root.attributes.get("saved.consolidate") == "ran"
        [consolidating] = [span for span in run.spans if span.name == "chrome_tabs.consolidate_saved"]
        assert (consolidating.attributes.get("saved.planned"), consolidating.attributes.get("saved.deleted")) == (0, 0)

    def test_a_run_that_created_no_group_leaves_saved_groups_alone(
        self, chrome: FakeChrome, isolate_trace_output: Path, monkeypatch: pytest.MonkeyPatch
    ) -> None:
        from desktop_manager.chrome import presence

        monkeypatch.setattr(presence, "session_locked", lambda: True)

        _organise(chrome, "--consolidate-saved", "--wait-minutes", "0")

        run = _run(isolate_trace_output)
        assert run.root.attributes.get("saved.consolidate") == "no_new_groups"
        assert "chrome_tabs.consolidate_saved" not in {span.name for span in run.spans}

    def test_an_incomplete_consolidation_is_retried_without_a_new_group(
        self, chrome: FakeChrome, isolate_trace_output: Path, monkeypatch: pytest.MonkeyPatch
    ) -> None:
        from desktop_manager.chrome import presence

        monkeypatch.setattr(presence, "idle_seconds", lambda: 600.0)
        profile = chrome.windows[0].profile_key
        saved = [SavedGroup(profile, "Google", True, 0), SavedGroup(profile, "Google", False, 1)]
        chrome.saved = saved
        failed = SavedDeleteOutcome(False, "chip still on the bar", (), 0.0)
        chrome.deletes = [failed, SavedDeleteOutcome(True, "deleted", (), 0.0)]

        with pytest.raises(typer.Exit):
            _organise(chrome, "--consolidate-saved")
        monkeypatch.setattr(presence, "session_locked", lambda: True)
        _organise(chrome, "--consolidate-saved", "--wait-minutes", "0")
        _organise(chrome, "--consolidate-saved", "--wait-minutes", "0")
        _organise(chrome, "--consolidate-saved", "--wait-minutes", "0")

        tracing.shutdown_tracing()
        runs = sorted(observe_runs(isolate_trace_output, "chrome_tabs.organise"), key=lambda run: run.root.start_ns)
        assert [run.root.attributes.get("saved.consolidate") for run in runs] == [
            "ran",
            "pending",
            "pending",
            "no_new_groups",
        ]
        assert [run.root.attributes.get("saved.deleted") for run in runs] == [0, 1, 0, None]

    def test_a_pending_profile_that_was_not_scanned_stays_pending(
        self, chrome: FakeChrome, isolate_trace_output: Path, monkeypatch: pytest.MonkeyPatch
    ) -> None:
        from desktop_manager.chrome import presence
        from desktop_manager.chrome.saved_consolidation import consolidation_marker

        monkeypatch.setattr(presence, "session_locked", lambda: True)
        consolidation_marker().parent.mkdir(parents=True, exist_ok=True)
        consolidation_marker().write_text("Closed Profile\n", encoding="utf-8")

        _organise(chrome, "--consolidate-saved", "--wait-minutes", "0")

        assert _run(isolate_trace_output).root.attributes.get("saved.consolidate") == "pending"
        assert consolidation_marker().read_text(encoding="utf-8") == "Closed Profile\n"

    def test_a_consolidation_that_raises_is_retried(
        self, chrome: FakeChrome, isolate_trace_output: Path, monkeypatch: pytest.MonkeyPatch
    ) -> None:
        from desktop_manager.chrome import presence
        from desktop_manager.chrome.saved_consolidation import consolidation_marker

        def broken(hwnd: int, profile: str) -> list[SavedGroup]:
            raise OSError("UIA is gone")

        monkeypatch.setattr(presence, "idle_seconds", lambda: 600.0)
        monkeypatch.setattr(chrome, "saved_groups", broken)
        with pytest.raises(OSError, match="UIA is gone"):
            _organise(chrome, "--consolidate-saved")

        assert consolidation_marker().read_text(encoding="utf-8") == f"{chrome.windows[0].profile_key}\n"

    def test_a_locked_machine_with_no_wait_defers_grouping(
        self, chrome: FakeChrome, isolate_trace_output: Path, monkeypatch: pytest.MonkeyPatch
    ) -> None:
        from desktop_manager.chrome import presence

        monkeypatch.setattr(presence, "session_locked", lambda: True)

        _organise(chrome, "--wait-minutes", "0")

        run = _run(isolate_trace_output)
        assert run.root.attributes["group.outcome"] == "deferred"
        assert run.root.attributes.get("wait.outcome") == "no_wait"
        assert _blocked_reasons(run) == ["the session is locked"]
        assert "chrome_tabs.group" not in {span.name for span in run.spans}

    def test_a_dry_run_is_traced_too(self, chrome: FakeChrome, isolate_trace_output: Path) -> None:
        _organise(chrome, "--dry-run", scheduled=False)

        run = _run(isolate_trace_output)
        assert run.root.attributes["run.outcome"] == "dry_run"
        assert run.root.attributes.get("group.planned") == 3
        assert {span.name for span in run.spans} == {
            "chrome_tabs.read_windows",
            "chrome_tabs.plan_closes",
            "chrome_tabs.plan_grouping",
        }

    def test_github_lookups_parent_under_resolve_across_the_thread_pool(
        self, chrome: FakeChrome, isolate_trace_output: Path, monkeypatch: pytest.MonkeyPatch
    ) -> None:
        from rig import proc

        monkeypatch.setattr(proc, "run_gh", lambda query: proc.GhResult(1, "", "HTTP 502"))

        _organise(chrome, "--dry-run", "--github", scheduled=False)

        run = _run(isolate_trace_output)
        [resolve] = [span for span in run.spans if span.name == "chrome_tabs.resolve_prs"]
        assert (resolve.attributes.get("github.prs"), resolve.attributes.get("github.repos")) == (3, 1)
        assert resolve.attributes.get("github.failed_repos") == ["acme-corp/data-platform"]
        queries = [span for span in run.spans if span.name == "github.graphql"]
        assert queries
        assert {span.parent_id for span in queries} == {resolve.span_id}
        assert all(span.attributes.get("github.repo") == "acme-corp/data-platform" for span in queries)
        assert all(span.error == "HTTP 502" for span in queries)

    def test_a_failing_run_records_the_exception(
        self, chrome: FakeChrome, isolate_trace_output: Path, monkeypatch: pytest.MonkeyPatch
    ) -> None:
        def broken() -> list[ChromeWindow]:
            raise OSError("UIA is gone")

        monkeypatch.setattr(chrome, "list_windows", broken)
        with pytest.raises(OSError, match="UIA is gone"):
            _organise(chrome)

        run = _run(isolate_trace_output)
        assert run.root.status_code == 2
        [read] = run.spans
        assert read.name == "chrome_tabs.read_windows"
        assert read.events[0].attributes["exception.type"] == "OSError"
