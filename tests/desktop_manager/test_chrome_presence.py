from __future__ import annotations

import sys

import pytest

from desktop_manager.chrome import presence
from desktop_manager.chrome.presence import Block, wait_until_clear
from desktop_manager.chrome.tabs import ChromeTab, ChromeWindow, parse_tab_name
from desktop_manager.chrome.unattended import live_call, unattended_group_block


class _Clock:
    def __init__(self) -> None:
        self.now = 0.0
        self.pauses: list[float] = []

    def __call__(self) -> float:
        return self.now

    def sleep(self, seconds: float) -> None:
        self.pauses.append(seconds)
        self.now += seconds


def _blocks(clock: _Clock, clear_at: float, hint: bool = False):
    def blocked() -> Block | None:
        if clock.now >= clear_at:
            return None
        return Block("busy", clear_at - clock.now if hint else None)

    return blocked


@pytest.mark.ci
def test_clear_at_once_does_not_sleep():
    clock = _Clock()
    outcome = wait_until_clear(_blocks(clock, 0), budget_seconds=60, sleep=clock.sleep, clock=clock)
    assert (outcome.ready, outcome.checks, clock.pauses) == (True, 1, [])


@pytest.mark.ci
def test_pauses_double_up_to_the_cap():
    clock = _Clock()
    outcome = wait_until_clear(
        _blocks(clock, 1000), budget_seconds=400, first_pause=5, max_pause=60, sleep=clock.sleep, clock=clock
    )
    assert outcome.ready is False
    assert clock.pauses[:6] == [5, 10, 20, 40, 60, 60]
    assert sum(clock.pauses) == 400
    assert outcome.last_block == "busy"


@pytest.mark.ci
def test_a_hint_wakes_the_wait_exactly_when_the_block_may_clear():
    clock = _Clock()
    clock.now = 0
    outcome = wait_until_clear(
        _blocks(clock, 3, hint=True), budget_seconds=600, first_pause=60, sleep=clock.sleep, clock=clock
    )
    assert outcome.ready is True
    assert clock.pauses == [3]


@pytest.mark.ci
def test_capped_pauses_catch_an_idle_spell_well_before_a_five_minute_lock():
    # The user stops typing at t=500; grouping wants 60s idle; the machine locks at 300s idle.
    clock = _Clock()
    went_idle = 500.0

    def blocked() -> Block | None:
        idle = clock.now - went_idle if clock.now >= went_idle else 0.0
        return None if idle >= 60 else Block("busy", 60 - idle)

    outcome = wait_until_clear(blocked, budget_seconds=3600, max_pause=60, sleep=clock.sleep, clock=clock)
    assert outcome.ready is True
    assert clock.now - went_idle < 300


def _window(*names: str) -> ChromeWindow:
    tabs: list[ChromeTab] = []
    for index, name in enumerate(names):
        parsed = parse_tab_name(name)
        tabs.append(
            ChromeTab(
                runtime_id=(42, 1, index),
                window_hwnd=1,
                index=index,
                name=name,
                title=parsed.title,
                selected=index == 0,
                pinned=False,
                loaded=parsed.loaded,
                audible=parsed.audible,
                alerts=parsed.alerts,
            )
        )
    return ChromeWindow(
        hwnd=1,
        title="w",
        profile_dir="Profile 4",
        profile_name="Ryan",
        age_rank=1,
        minimized=False,
        desktop_number=1,
        tabs=tuple(tabs),
    )


@pytest.fixture
def no_devices(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setattr(presence, "devices_in_use", lambda: [])


@pytest.mark.ci
@pytest.mark.parametrize(
    ("name", "says"),
    [
        ("Meet – abc-defg-hij - Desktop content shared - Memory usage - 300 MB", "desktop content shared"),
        ("Meet – abc-defg-hij – Microphone recording", "microphone recording"),
        ("Quarterly review - Google Slides - Tab content shared", "tab content shared"),
        ("Meet – abc-defg-hij - Audio playing", "a call is live (Meet"),
    ],
)
def test_a_recording_sharing_or_audible_call_tab_is_live(no_devices: None, name: str, says: str):
    live = live_call([_window("Inbox - Gmail", name)])
    assert live is not None
    assert says in live


@pytest.mark.ci
def test_quiet_tabs_and_idle_devices_are_not_live(no_devices: None):
    assert live_call([_window("Meet – abc-defg-hij", "Lo-fi beats - YouTube - Audio playing")]) is None


@pytest.mark.ci
def test_chrome_holding_the_microphone_is_live_without_any_tab_alert(monkeypatch: pytest.MonkeyPatch):
    monkeypatch.setattr(presence, "devices_in_use", lambda: ["microphone", "camera"])
    assert live_call([]) == "a call is live: Chrome is using the microphone and camera"


@pytest.mark.ci
def test_a_device_check_that_fails_is_not_live(monkeypatch: pytest.MonkeyPatch):
    def broken() -> list[str]:
        raise OSError("no registry")

    monkeypatch.setattr(presence, "devices_in_use", broken)
    assert live_call([]) is None


@pytest.mark.ci
def test_block_reports_a_live_call_once_idle(monkeypatch: pytest.MonkeyPatch, no_devices: None):
    monkeypatch.setattr(presence, "session_locked", lambda: False)
    monkeypatch.setattr(presence, "idle_seconds", lambda: 120.0)
    windows = [_window("Meet – abc-defg-hij - Camera and microphone recording")]
    block = unattended_group_block(lambda: windows, idle_minutes=1)
    assert block is not None
    assert "camera and microphone recording" in block.reason
    assert block.clears_in is None


@pytest.mark.ci
def test_block_reports_a_locked_session_first(monkeypatch: pytest.MonkeyPatch):
    monkeypatch.setattr(presence, "session_locked", lambda: True)
    monkeypatch.setattr(presence, "idle_seconds", lambda: 9999.0)
    assert unattended_group_block(lambda: [], idle_minutes=1) == Block("the session is locked")


@pytest.mark.ci
def test_block_says_how_long_until_idle_is_enough(monkeypatch: pytest.MonkeyPatch):
    monkeypatch.setattr(presence, "session_locked", lambda: False)
    monkeypatch.setattr(presence, "idle_seconds", lambda: 20.0)
    block = unattended_group_block(lambda: [], idle_minutes=1)
    assert block is not None
    assert "20s ago" in block.reason
    assert block.clears_in == pytest.approx(40.0)


@pytest.mark.ci
def test_idle_unlocked_machine_without_a_call_is_clear(monkeypatch: pytest.MonkeyPatch, no_devices: None):
    monkeypatch.setattr(presence, "session_locked", lambda: False)
    monkeypatch.setattr(presence, "idle_seconds", lambda: 61.0)
    assert unattended_group_block(lambda: [], idle_minutes=1) is None


@pytest.mark.windows
@pytest.mark.skipif(sys.platform != "win32", reason="needs the Windows input desktop")
def test_session_is_not_locked_while_tests_run():
    assert presence.session_locked() is False


@pytest.mark.windows
@pytest.mark.skipif(sys.platform != "win32", reason="reads the Windows privacy settings")
def test_devices_in_use_reads_the_registry_without_failing():
    assert set(presence.devices_in_use()) <= {"microphone", "camera"}
