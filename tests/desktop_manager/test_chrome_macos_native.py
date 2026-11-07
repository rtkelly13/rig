"""Read-only native bridge checks, excluded from portable CI."""

import sys

import pytest

from desktop_manager.chrome.drivers.macos_jxa import SNAPSHOT_SCRIPT, run_jxa

pytestmark = [
    pytest.mark.macos,
    pytest.mark.integration,
    pytest.mark.skipif(sys.platform != "darwin", reason="macOS only"),
]


def test_jxa_objc_numbers_and_no_running_application() -> None:
    # A nonexistent bundle exercises NSRunningApplication and the empty NSArray
    # bridge without contacting, launching, or changing the user's browser.
    script = SNAPSHOT_SCRIPT.replace("com.google.Chrome", "invalid.chrome-tabs-test.no-such-app")
    assert run_jxa(script) == {"pid": 0, "frontmost": False, "windows": []}


def test_native_json_preserves_strings() -> None:
    assert run_jxa('JSON.stringify({title: "雪, \\"quoted\\"\\nline", url: "https://example.test/?a=1,2"})') == {
        "title": '雪, "quoted"\nline',
        "url": "https://example.test/?a=1,2",
    }
