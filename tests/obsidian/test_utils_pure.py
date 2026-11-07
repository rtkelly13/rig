"""Pure utility-function tests for ``tvs.obsidian.utils``."""

from __future__ import annotations

from rig.obsidian.utils import (
    clean_url,
    clean_ws,
    is_http_url,
    normalize_url,
    sanitize_filename,
)


def test_clean_ws_normalizes_whitespace() -> None:
    assert clean_ws("  foo   bar\t\tbaz\n") == "foo bar baz"
    assert clean_ws("") == ""


def test_normalize_url_strips_tracking_fragment_and_www() -> None:
    assert normalize_url("https://www.Example.com/page/?utm_source=x#section") == "https://example.com/page"
    assert normalize_url("https://x.com/someuser/status/12345") == "https://x.com/i/status/12345"


def test_is_http_url_accepts_http_and_https_only() -> None:
    assert is_http_url("https://example.com") is True
    assert is_http_url("http://example.com") is True
    assert is_http_url("ftp://example.com") is False
    assert is_http_url("not-a-url") is False


def test_clean_url_removes_tracking_params_only() -> None:
    result = clean_url("https://example.com/page?id=42&utm_source=newsletter&fbclid=abc")
    assert result == "https://example.com/page?id=42"


def test_sanitize_filename_removes_forbidden_chars() -> None:
    result = sanitize_filename('foo:bar/baz*qux?"<>|')
    assert result == "foo-bar-baz-qux"
    assert sanitize_filename("   ") == "Untitled"
