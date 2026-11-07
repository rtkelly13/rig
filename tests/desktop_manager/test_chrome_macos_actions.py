"""Portable close and selection tests. No test mutates a running browser."""

from __future__ import annotations

import json
from dataclasses import replace
from unittest.mock import Mock

import pytest

from desktop_manager.chrome.drivers import macos, macos_jxa
from desktop_manager.chrome.drivers.macos_jxa import JxaTab, JxaWindow, Snapshot
from desktop_manager.chrome.tabs import ChromeTab, ChromeWindow

pytestmark = pytest.mark.ci


def tab(identity: int = 701, *, selected: bool = False) -> ChromeTab:
    return ChromeTab(
        (identity,),
        42,
        4,
        'Notes, "雪"\nline',
        'Notes, "雪"\nline',
        selected,
        False,
        True,
        False,
        url='https://example.test/雪?quoted="yes"',
    )


def window(*tabs: ChromeTab) -> ChromeWindow:
    return ChromeWindow(42, "Chrome", None, None, 42, False, None, tabs)


def native(*tab_ids: int) -> Snapshot:
    """A scripting snapshot holding window 42 with these tabs, or no windows at all."""
    if not tab_ids:
        return Snapshot(pid=1, frontmost=True, windows=())
    tabs = tuple(JxaTab(id=tab_id, title="t", url="u") for tab_id in tab_ids)
    return Snapshot(
        1, True, (JxaWindow(id=42, index=1, title="w", active=1, minimized=False, mode="normal", tabs=tabs),)
    )


def setup_close(monkeypatch: pytest.MonkeyPatch, current: ChromeTab | None = None) -> tuple[macos.ChromeMac, Mock]:
    driver = macos.ChromeMac()
    monkeypatch.setattr(driver, "list_windows", Mock(return_value=[window(current or tab())]))
    monkeypatch.setattr(driver, "snapshot", Mock(return_value=native()))
    runner = Mock(return_value={"status": "invoked"})
    monkeypatch.setattr(macos_jxa, "run_jxa", runner)
    return driver, runner


def test_close_uses_stable_ids_and_escaped_strings(monkeypatch: pytest.MonkeyPatch) -> None:
    driver, runner = setup_close(monkeypatch, replace(tab(), index=0))
    ticks = iter([4.0, 4.25])
    monkeypatch.setattr(macos.time, "perf_counter", lambda: next(ticks))
    result = driver.close_tab(tab())
    assert result.closed and result.detail == "closed" and result.seconds == 0.25
    script = runner.call_args.args[0]
    assert "windowID = 42" in script and "tabID = 701" in script
    assert json.dumps(tab().title, ensure_ascii=True) in script
    assert json.dumps(tab().url, ensure_ascii=True) in script
    assert "target.close()" in script and "chrome.activate" not in script
    assert "chrome.windows.byId(windowID)" in script and "window.tabs.byId(tabID)" in script
    assert "matches.length !== 1" in script and "windows.length !== 1" in script


@pytest.mark.parametrize(
    "field,value,detail",
    [
        ("title", "Changed", "title changed"),
        ("url", "https://other.test", "URL changed"),
        ("pinned", True, "pinned"),
        ("audible", True, "audible"),
        ("alerts", ("Accessibility metadata unavailable",), "metadata unavailable"),
    ],
)
def test_close_revalidates_current_content_and_safety(
    monkeypatch: pytest.MonkeyPatch, field: str, value: object, detail: str
) -> None:
    current = replace(tab(), **{field: value})  # type: ignore[arg-type]
    driver, runner = setup_close(monkeypatch, current)
    result = driver.close_tab(tab())
    assert not result.closed and detail in result.detail
    runner.assert_not_called()


@pytest.mark.parametrize(
    "planned",
    [
        replace(tab(), runtime_id=()),
        replace(tab(), runtime_id=(701, 2)),
        replace(tab(), runtime_id=(0,)),
        replace(tab(), runtime_id=(-1,)),
        replace(tab(), runtime_id=(True,)),
        replace(tab(), runtime_id=(2**53,)),
        replace(tab(), window_hwnd=0),
        replace(tab(), window_hwnd=True),
        replace(tab(), url=None),
    ],
)
def test_invalid_target_never_sends_action(monkeypatch: pytest.MonkeyPatch, planned: ChromeTab) -> None:
    driver, runner = setup_close(monkeypatch)
    assert not driver.close_tab(planned).closed
    runner.assert_not_called()


def test_missing_or_moved_tab_is_not_closed(monkeypatch: pytest.MonkeyPatch) -> None:
    driver, runner = setup_close(monkeypatch, replace(tab(), runtime_id=(702,)))
    assert not driver.close_tab(tab()).closed
    runner.assert_not_called()


@pytest.mark.parametrize("reply", [{"status": "missing"}, {"status": "stale"}, {}, None, True])
def test_native_empty_or_stale_target_is_not_success(monkeypatch: pytest.MonkeyPatch, reply: object) -> None:
    driver, runner = setup_close(monkeypatch)
    runner.return_value = reply
    assert not driver.close_tab(tab()).closed


def test_tab_still_present_or_prompt_is_failure(monkeypatch: pytest.MonkeyPatch) -> None:
    driver, runner = setup_close(monkeypatch)
    monkeypatch.setattr(driver, "snapshot", Mock(return_value=native(701)))
    result = driver.close_tab(tab(), timeout_seconds=0)
    assert not result.closed and "still present" in result.detail
    assert runner.call_count == 1  # A prompt never authorizes another close or a confirmation.


def test_close_waits_for_actual_disappearance(monkeypatch: pytest.MonkeyPatch) -> None:
    driver, _ = setup_close(monkeypatch)
    snapshots = Mock(side_effect=[native(701), native()])
    monkeypatch.setattr(driver, "snapshot", snapshots)
    monkeypatch.setattr(macos.time, "sleep", Mock())
    assert driver.close_tab(tab()).closed
    assert snapshots.call_count == 2


@pytest.mark.parametrize("stage", ["read", "action", "confirmation"])
def test_close_errors_return_timed_failure(monkeypatch: pytest.MonkeyPatch, stage: str) -> None:
    driver, runner = setup_close(monkeypatch)
    error = macos.ChromeMacError("denied")
    if stage == "read":
        monkeypatch.setattr(driver, "list_windows", Mock(side_effect=error))
    elif stage == "action":
        runner.side_effect = error
    else:
        monkeypatch.setattr(driver, "snapshot", Mock(side_effect=error))
    result = driver.close_tab(tab())
    assert not result.closed and result.seconds >= 0 and "failed" in result.detail


def test_missing_ax_evidence_prevents_close(monkeypatch: pytest.MonkeyPatch) -> None:
    driver, runner = setup_close(monkeypatch)
    from tests.desktop_manager.test_chrome_macos_reader import snapshot

    monkeypatch.delattr(driver, "list_windows")
    monkeypatch.setattr(macos_jxa, "run_jxa", lambda script: snapshot())
    monkeypatch.setattr(macos.macos_ax, "read_windows", lambda pid: ())
    result = driver.close_tab(replace(tab(), url='https://example.com/雪?q=a,b&x="c"'))
    assert not result.closed
    runner.assert_not_called()


def test_restore_maps_stable_id_to_current_index(monkeypatch: pytest.MonkeyPatch) -> None:
    original = tab(selected=True)
    runner = Mock(return_value={"status": "selected"})
    monkeypatch.setattr(macos_jxa, "run_jxa", runner)
    assert macos.ChromeMac().restore_selection(iter([window(original)])) == {42: original}
    script = runner.call_args.args[0]
    assert "tabID = 701" in script and "activeTabIndex = matches[0] + 1" in script
    assert "activeTab().id()" in script and "chrome.activate" not in script


def test_restore_uses_same_window_fallback_only_when_original_missing(monkeypatch: pytest.MonkeyPatch) -> None:
    original, fallback = tab(selected=True), tab(702)
    runner = Mock(side_effect=[{"status": "missing"}, {"status": "selected"}])
    monkeypatch.setattr(macos_jxa, "run_jxa", runner)
    assert macos.ChromeMac().restore_selection([window(original)], fallbacks={42: fallback}) == {42: fallback}
    assert "tabID = 702" in runner.call_args.args[0]


@pytest.mark.parametrize("reply", [{"status": "failed"}, {}, None, macos.ChromeMacError("denied")])
def test_restore_action_failure_does_not_select_fallback(monkeypatch: pytest.MonkeyPatch, reply: object) -> None:
    runner = Mock(side_effect=reply) if isinstance(reply, Exception) else Mock(return_value=reply)
    monkeypatch.setattr(macos_jxa, "run_jxa", runner)
    assert macos.ChromeMac().restore_selection([window(tab(selected=True))], fallbacks={42: tab(702)}) == {42: None}
    assert runner.call_count == 1


@pytest.mark.parametrize("fallback", [None, replace(tab(702), window_hwnd=43), replace(tab(702), runtime_id=())])
def test_restore_missing_original_rejects_unusable_fallback(
    monkeypatch: pytest.MonkeyPatch, fallback: ChromeTab | None
) -> None:
    runner = Mock(return_value={"status": "missing"})
    monkeypatch.setattr(macos_jxa, "run_jxa", runner)
    fallbacks = {42: fallback} if fallback else None
    assert macos.ChromeMac().restore_selection([window(tab(selected=True))], fallbacks=fallbacks) == {42: None}
    assert runner.call_count == 1


def test_restore_no_selected_tab_or_fallback_is_noop(monkeypatch: pytest.MonkeyPatch) -> None:
    runner = Mock()
    monkeypatch.setattr(macos_jxa, "run_jxa", runner)
    assert macos.ChromeMac().restore_selection([window(tab())]) == {42: None}
    runner.assert_not_called()
