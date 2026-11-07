"""Chrome tab accessible-name parsing and window profile attribution, without a browser."""

from __future__ import annotations

import pytest

from desktop_manager.chrome.tabs import parse_group_header, parse_tab_name
from desktop_manager.chrome.uia import names_window
from desktop_manager.chrome.window_info import (
    profile_dir_from_relaunch_command,
    profile_name_from_relaunch_display_name,
)

pytestmark = pytest.mark.ci


@pytest.mark.parametrize(
    ("name", "title", "loaded"),
    [
        ("Inbox - Memory usage - 346 MB", "Inbox", True),
        ("Inbox - High memory usage - 1,204 MB", "Inbox", True),
        ("Docs - Memory usage - 67.1 MB", "Docs", True),
        ("Discarded tab", "Discarded tab", False),
        ("A - B - C", "A - B - C", False),
    ],
)
def test_memory_suffix_is_stripped_and_marks_the_tab_loaded(name: str, title: str, loaded: bool):
    parsed = parse_tab_name(name)
    assert (parsed.title, parsed.loaded, parsed.audible) == (title, loaded, False)


def test_alert_suffixes_are_stripped_and_live_media_counts_as_audible():
    parsed = parse_tab_name("Google Meet - Camera or microphone recording - Audio playing - Memory usage - 340 MB")
    assert parsed.title == "Google Meet"
    assert parsed.alerts == ("Camera or microphone recording", "Audio playing")
    assert parsed.audible


def test_en_dash_joined_call_is_audible():
    # A live Meet call as this Chrome build names it: en dashes, and "and" rather than "or".
    name = "Meet – Data & Platform Standup - Daily – Camera and microphone recording - Memory usage - 765 MB"
    parsed = parse_tab_name(name)
    assert parsed.title == "Meet – Data & Platform Standup - Daily"
    assert parsed.alerts == ("Camera and microphone recording",)
    assert (parsed.loaded, parsed.audible) == (True, True)


def test_non_media_alerts_are_not_audible():
    parsed = parse_tab_name("Page - Network error")
    assert (parsed.title, parsed.alerts, parsed.audible) == ("Page", ("Network error",), False)


def test_profile_directory_comes_from_the_relaunch_command():
    exe = r'"C:\Program Files\Google\Chrome\Application\chrome.exe"'
    assert profile_dir_from_relaunch_command(f'{exe} --profile-directory="Profile 4"') == "Profile 4"
    assert profile_dir_from_relaunch_command(f"{exe} --profile-directory=Default") == "Default"
    assert profile_dir_from_relaunch_command(exe) == "Default"
    assert profile_dir_from_relaunch_command(None) is None


def test_profile_name_drops_the_chrome_suffix():
    assert profile_name_from_relaunch_display_name("Ryan (work) - Chrome") == "Ryan (work)"
    assert profile_name_from_relaunch_display_name(None) is None


@pytest.mark.parametrize(
    "name",
    [
        "Example Domain – Part of unnamed group - Memory usage - 30.0 MB",
        "Example Domain – Part of group typed-title - Memory usage - 30.0 MB",
        "Example Domain - Part of group a - b - Memory usage - 30.0 MB",
    ],
)
def test_group_suffix_is_stripped(name: str):
    assert parse_tab_name(name).title == "Example Domain"


def test_group_suffix_before_an_alert_keeps_the_alert():
    parsed = parse_tab_name("Meet – Part of group calls - Audio playing - Memory usage - 30.0 MB")
    assert (parsed.title, parsed.alerts) == ("Meet", ("Audio playing",))


@pytest.mark.parametrize(
    ("name", "title"),
    [
        (" group typed-title – 1 tab, •  Example Domain – Expanded", "typed-title"),
        (" group data-platform – 12 tabs, •  A, •  B – Collapsed", "data-platform"),
        (" unnamed group – 2 tabs, •  Example Domain – Expanded", ""),
        ("Example Domain", None),
    ],
)
def test_group_header_names_the_group(name: str, title: str | None):
    assert parse_group_header(name) == title


@pytest.mark.parametrize(
    ("entry", "active", "count", "names"),
    [
        ("linking triage and 3 other tabs", "Linking Triage", 4, True),
        ("linking triage and 1 other tab", "Linking Triage", 2, True),
        ("linking triage", "Linking Triage", 1, True),
        ("linking triage and 3 other tabs", "Linking Triage", 3, False),
        ("linking triage and 3 other tabs", "Linking by Entity", 4, False),
        ("some very long pull request ti… and 9 other tabs", "Some very long pull request title here", 10, True),
        ("… and 9 other tabs", "Anything", 10, False),
        ("new window", "New window", 3, False),
    ],
)
def test_move_menu_entries_name_a_window_by_active_tab_and_count(entry: str, active: str, count: int, names: bool):
    assert names_window(entry, active, count) is names
