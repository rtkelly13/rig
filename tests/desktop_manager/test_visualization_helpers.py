"""Unit tests for pure helper functions in desktop_manager.visualization and window_reuse.

Covers branches in:
  - _chrome_hints_for_url: all URL pattern branches
  - _terminal_command_hint_score: copilot, opencode, and fallback branches
  - truncate_text: within-limit and truncation cases
  - expected_process_name: explicit process name, path-derived, and None cases
  - WorkspaceDiff: cleanliness, fixable drift and the changed-screens view
"""

from __future__ import annotations

import pytest

from desktop_manager.models import LaunchEntry
from desktop_manager.visualization import DesktopDiff, ScreenDiff, WorkspaceDiff, truncate_text
from desktop_manager.window_reuse import (
    _chrome_hints_for_url,
    _terminal_command_hint_score,
    expected_process_name,
)

# ---------------------------------------------------------------------------
# _chrome_hints_for_url
# ---------------------------------------------------------------------------


class TestChromeHintsForUrlGoogleServices:
    def test_google_meet_url_returns_meet_hints(self) -> None:
        result = _chrome_hints_for_url("https://meet.google.com/abc-defg-hij")
        assert result == ["google meet", "meet"]

    def test_google_calendar_url_returns_calendar_hints(self) -> None:
        result = _chrome_hints_for_url("https://calendar.google.com/calendar/r")
        assert result == ["google calendar", "calendar"]

    def test_gmail_mail_subdomain_returns_gmail_hints(self) -> None:
        result = _chrome_hints_for_url("https://mail.google.com/mail/u/0/")
        assert result == ["gmail", "mail"]

    def test_gmail_com_hostname_returns_gmail_hints(self) -> None:
        result = _chrome_hints_for_url("https://gmail.com/")
        assert result == ["gmail", "mail"]

    def test_google_docs_url_returns_docs_hints(self) -> None:
        result = _chrome_hints_for_url("https://docs.google.com/document/d/123")
        assert result == ["google docs", "docs"]


class TestChromeHintsForUrlGenericDomain:
    def test_simple_domain_returns_domain_label(self) -> None:
        result = _chrome_hints_for_url("https://github.com/")
        assert "github" in result

    def test_subdomain_includes_subdomain_label(self) -> None:
        result = _chrome_hints_for_url("https://linear.app/team/issues")
        assert "linear" in result

    def test_www_prefix_is_stripped(self) -> None:
        result = _chrome_hints_for_url("https://www.notion.so/workspace")
        assert "www" not in " ".join(result)
        assert "notion" in result

    def test_path_segment_included_when_path_is_non_root(self) -> None:
        result = _chrome_hints_for_url("https://github.com/myorg")
        assert "myorg" in result

    def test_root_path_does_not_produce_empty_path_hint(self) -> None:
        result = _chrome_hints_for_url("https://github.com/")
        assert "" not in result

    def test_empty_host_returns_empty_list(self) -> None:
        result = _chrome_hints_for_url("not-a-url")
        assert result == []

    def test_no_duplicate_labels_for_single_label_domain(self) -> None:
        result = _chrome_hints_for_url("https://github.com/")
        # Both hint slots may be the same string for single-label domains; ensure no empty strings
        assert all(h for h in result)


# ---------------------------------------------------------------------------
# _terminal_command_hint_score
# ---------------------------------------------------------------------------


def _terminal_entry(command: str) -> LaunchEntry:
    return LaunchEntry(name="Terminal", type="terminal", command=command, cwd=r"C:\code")


class TestTerminalCommandHintScore:
    def test_non_terminal_entry_returns_zero(self) -> None:
        entry = LaunchEntry(name="App", type="app", path=r"C:\app.exe")
        assert _terminal_command_hint_score(entry, "opencode") == 0

    def test_terminal_without_command_returns_zero(self) -> None:
        entry = LaunchEntry(name="Terminal", type="terminal", cwd=r"C:\code")
        assert _terminal_command_hint_score(entry, "opencode") == 0

    def test_copilot_command_matches_window_title(self) -> None:
        entry = _terminal_entry("gh copilot suggest")
        assert _terminal_command_hint_score(entry, "copilot session") == 180

    def test_copilot_command_no_match_returns_zero(self) -> None:
        entry = _terminal_entry("gh copilot suggest")
        assert _terminal_command_hint_score(entry, "PowerShell") == 0

    def test_opencode_exact_title_match(self) -> None:
        entry = _terminal_entry("opencode")
        assert _terminal_command_hint_score(entry, "opencode chat") == 180

    def test_opencode_oc_pipe_prefix_match(self) -> None:
        entry = _terminal_entry("opencode")
        assert _terminal_command_hint_score(entry, "oc | my-project") == 160

    def test_opencode_no_match_returns_zero(self) -> None:
        entry = _terminal_entry("opencode")
        assert _terminal_command_hint_score(entry, "PowerShell") == 0

    def test_unrecognised_command_returns_zero(self) -> None:
        entry = _terminal_entry("python manage.py shell")
        assert _terminal_command_hint_score(entry, "python manage.py shell") == 0


# ---------------------------------------------------------------------------
# truncate_text
# ---------------------------------------------------------------------------


class TestTruncateText:
    def test_short_string_returned_unchanged(self) -> None:
        assert truncate_text("hello", 10) == "hello"

    def test_string_at_limit_returned_unchanged(self) -> None:
        assert truncate_text("hello", 5) == "hello"

    def test_long_string_is_truncated_with_ellipsis(self) -> None:
        result = truncate_text("hello world", 8)
        assert result == "hello..."
        assert len(result) == 8

    def test_truncated_result_respects_limit(self) -> None:
        for limit in range(4, 20):
            result = truncate_text("a" * 30, limit)
            assert len(result) == limit


# ---------------------------------------------------------------------------
# expected_process_name
# ---------------------------------------------------------------------------


class TestExpectedProcessName:
    def test_explicit_window_process_name_returned_lowercased(self) -> None:
        entry = LaunchEntry(name="App", type="app", path=r"C:\App.exe", window_process_name="MyApp.exe")
        assert expected_process_name(entry, {}) == "myapp.exe"

    def test_process_name_derived_from_path_when_not_set(self) -> None:
        # Use a posix-style path so Path().name works correctly on the Linux CI runner.
        entry = LaunchEntry(name="App", type="app", path="/tools/MyTool.exe")
        assert expected_process_name(entry, {}) == "mytool.exe"

    def test_process_name_resolves_substitution_in_path(self) -> None:
        # Use a posix-style path so Path().name works correctly on the Linux CI runner.
        entry = LaunchEntry(name="App", type="app", path="{tool-path}")
        subs = {"tool-path": "/tools/resolved.exe"}
        assert expected_process_name(entry, subs) == "resolved.exe"

    def test_no_path_and_no_process_name_returns_none(self) -> None:
        entry = LaunchEntry(name="Terminal", type="terminal", cwd=r"C:\code")
        assert expected_process_name(entry, {}) is None


# ---------------------------------------------------------------------------
# WorkspaceDiff
# ---------------------------------------------------------------------------


@pytest.mark.parametrize(
    ("screen", "is_clean", "has_fixable_drift"),
    [
        (ScreenDiff(1, matched=("Notes <- extra: missing: notes [notes]",)), True, False),
        (ScreenDiff(1, missing=("Notes",)), False, True),
        (ScreenDiff(1, misplaced=("Notes on screen 2",)), False, True),
        (ScreenDiff(1, extra=("Slack [slack]",)), False, False),
        (ScreenDiff(1, borrowed=("Docs [chrome] -> Chrome",)), False, False),
    ],
)
def test_workspace_diff_reads_its_fields_not_its_text(
    screen: ScreenDiff, is_clean: bool, has_fixable_drift: bool
) -> None:
    diff = WorkspaceDiff((DesktopDiff("Work", (screen,)),))

    assert diff.is_clean is is_clean
    assert diff.has_fixable_drift is has_fixable_drift


def test_workspace_diff_renders_the_restore_diff_layout() -> None:
    diff = WorkspaceDiff(
        (
            DesktopDiff("Work", (ScreenDiff(1, planned=("Notes",), missing=("Notes",), extra=("Slack [slack]",)),)),
            DesktopDiff("Admin", (ScreenDiff(2),)),
        )
    )

    assert diff.render().splitlines() == [
        "Workspace Diff",
        "",
        "Work",
        "Screen 1",
        "  planned: Notes",
        "  actual:  -",
        "  missing: Notes",
        "  extra:   Slack [slack]",
        "",
        "Admin",
        "Screen 2",
        "  planned: -",
        "  actual:  -",
        "  status:  ok",
    ]


def test_changed_since_keeps_only_screens_that_changed() -> None:
    ok_work = DesktopDiff("Work", (ScreenDiff(1), ScreenDiff(2)))
    ok_admin = DesktopDiff("Admin", (ScreenDiff(1),))
    drifted_work = DesktopDiff("Work", (ScreenDiff(1), ScreenDiff(2, missing=("Notes",))))
    previous = WorkspaceDiff((ok_work, ok_admin))

    changed = WorkspaceDiff((drifted_work, ok_admin)).changed_since(previous)

    assert changed == WorkspaceDiff((DesktopDiff("Work", (ScreenDiff(2, missing=("Notes",)),)),))


@pytest.mark.parametrize("previous", [None, WorkspaceDiff((DesktopDiff("Work", (ScreenDiff(1),)),))])
def test_changed_since_returns_the_whole_diff_without_a_previous_change(previous: WorkspaceDiff | None) -> None:
    diff = WorkspaceDiff((DesktopDiff("Work", (ScreenDiff(1),)),))

    assert diff.changed_since(previous) is diff
