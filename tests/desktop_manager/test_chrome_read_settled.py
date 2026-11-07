"""Reading Chrome on Windows when its UI changes mid-read: `read_settled`, and group-move baselines.

Runs on any platform: off Windows `WIN32_ERRORS` is `(OSError,)`, and every UIA call is a stand-in.
"""

from __future__ import annotations

import pytest

from desktop_manager.chrome.driver import ChromeReadError
from desktop_manager.chrome.tabs import ChromeTab, ChromeWindow, TabGroup
from desktop_manager.chrome.uia import ChromeUia
from desktop_manager.chrome.uia_core import read_settled

pytestmark = pytest.mark.ci


class _Flaky:
    """Fails `failures` times with a stale-element error, then returns "window"."""

    def __init__(self, failures: int) -> None:
        self.failures = failures
        self.calls = 0
        self.retries = 0

    def __call__(self) -> str:
        self.calls += 1
        if self.calls <= self.failures:
            raise OSError("An event was unable to invoke any of the subscribers")
        return "window"

    def forget(self) -> None:
        self.retries += 1


def _read(flaky: _Flaky, *, alive: bool = True, attempts: int = 3) -> str | None:
    pauses: list[float] = []
    result = read_settled(
        flaky, alive=lambda: alive, on_retry=flaky.forget, attempts=attempts, pause_seconds=0.5, sleep=pauses.append
    )
    assert pauses == [0.5] * (flaky.calls - 1)
    return result


def test_a_clean_read_is_returned_at_once() -> None:
    flaky = _Flaky(0)
    assert _read(flaky) == "window"
    assert (flaky.calls, flaky.retries) == (1, 0)


def test_a_stale_element_is_retried_after_forgetting_the_cached_strip() -> None:
    flaky = _Flaky(2)
    assert _read(flaky) == "window"
    assert (flaky.calls, flaky.retries) == (3, 2)


def test_a_window_that_closed_mid_read_is_gone_not_an_error() -> None:
    flaky = _Flaky(5)
    assert _read(flaky, alive=False) is None
    assert flaky.calls == 1


def test_a_window_that_never_reads_cleanly_raises_instead_of_guessing() -> None:
    flaky = _Flaky(5)
    with pytest.raises(ChromeReadError, match="3 attempts") as raised:
        _ = _read(flaky)
    assert flaky.calls == 3
    assert isinstance(raised.value.__cause__, OSError)


def test_other_errors_are_not_swallowed() -> None:
    def broken() -> str:
        raise ValueError("a bug, not a stale element")

    with pytest.raises(ValueError, match="a bug"):
        _ = read_settled(broken, alive=lambda: True, on_retry=lambda: None, sleep=lambda _: None)


class TestGroupBaseline:
    """A move into the home window is confirmed by its group's matching-title count rising,
    so an unreadable window must count as unknown, never as 0."""

    @staticmethod
    def _chrome(window: ChromeWindow | None) -> ChromeUia:
        chrome = ChromeUia.__new__(ChromeUia)  # no UIA session: every read goes through read_window
        chrome.read_window = lambda hwnd: window  # type: ignore[method-assign]
        return chrome

    @staticmethod
    def _window(*grouped_titles: str) -> ChromeWindow:
        group = TabGroup(runtime_id=(1,), title="Dev")
        tabs = tuple(
            ChromeTab(
                runtime_id=(2, index),
                window_hwnd=7,
                index=index,
                name=title,
                title=title,
                selected=False,
                pinned=False,
                loaded=True,
                audible=False,
                group=group,
            )
            for index, title in enumerate(grouped_titles)
        )
        return ChromeWindow(
            hwnd=7,
            title="w",
            profile_dir="Default",
            profile_name="Me",
            age_rank=0,
            minimized=False,
            desktop_number=1,
            tabs=tabs,
        )

    def test_an_unreadable_window_has_no_count(self) -> None:
        assert self._chrome(None)._titled_in_group(7, "Dev", "PR") is None

    def test_a_readable_window_counts_matching_titles_in_the_group(self) -> None:
        assert self._chrome(self._window("PR", "PR", "Other"))._titled_in_group(7, "Dev", "PR") == 2

    def test_an_unreadable_window_never_confirms_arrival(self) -> None:
        assert self._chrome(None)._arrived_in_group(7, "Dev", "PR", before=0) is None

    def test_arrival_needs_the_count_to_rise(self) -> None:
        chrome = self._chrome(self._window("PR"))
        assert chrome._arrived_in_group(7, "Dev", "PR", before=1) is None
        assert chrome._arrived_in_group(7, "Dev", "PR", before=0) is True
