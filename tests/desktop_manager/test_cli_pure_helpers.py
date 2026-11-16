"""Unit tests for pure helper functions in desktop_manager.cli.

These tests cover console_safe_text, which is platform-agnostic and can run on
Linux in CI without Win32 dependencies. The plan scoping helpers now live in C#.
"""

from __future__ import annotations

from desktop_manager.cli_common import console_safe_text

# ---------------------------------------------------------------------------
# console_safe_text
# ---------------------------------------------------------------------------


class TestConsoleSafeText:
    def test_ascii_passes_through_unchanged(self) -> None:
        assert console_safe_text("Hello World") == "Hello World"

    def test_empty_string_returns_empty(self) -> None:
        assert console_safe_text("") == ""

    def test_cp1252_chars_pass_through(self) -> None:
        # Smart quotes (U+2018/U+2019) are valid cp1252 (0x91/0x92)
        result = console_safe_text("\u2018smart\u2019")
        assert result == "\u2018smart\u2019"

    def test_non_cp1252_chars_replaced(self) -> None:
        # Verify snowman character is replaced in cp1252 encoding
        result = console_safe_text("Hello \u2603")
        assert result == "Hello ?"

    def test_mixed_cp1252_and_non_encodable(self) -> None:
        result = console_safe_text("caf\xe9 \u2603")
        assert result == "caf\xe9 ?"
