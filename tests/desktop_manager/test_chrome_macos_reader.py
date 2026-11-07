"""Portable tests for native Chrome snapshots and conservative AX matching."""

from __future__ import annotations

import json
import subprocess
from dataclasses import replace
from itertools import cycle
from pathlib import Path
from unittest.mock import Mock

import pytest

from desktop_manager.chrome.drivers import macos, macos_jxa
from desktop_manager.chrome.drivers.macos_ax import AccessibilityUnavailableError, AXNode
from desktop_manager.chrome.drivers.macos_jxa import ProfileInfo
from desktop_manager.chrome.drivers.macos_read import TabSlot, pinned_labels_consistent, tab_slots
from desktop_manager.chrome.tabs import WindowRect

pytestmark = pytest.mark.ci

#: The real bridge, taken before the conftest guard replaces it; these tests mock `subprocess` beneath it.
_RUN_JXA = macos_jxa.run_jxa


def snapshot(title: str = 'Notes, "雪"\nline') -> dict[str, object]:
    return {
        "pid": 123,
        "frontmost": True,
        "windows": [
            {
                "id": "42",
                "index": 1,
                "title": "Chrome work",
                "active": 1,
                "minimized": True,
                "mode": "normal",
                "bounds": {"x": 10, "y": 20, "width": 800, "height": 600},
                "tabs": [{"id": "701", "title": title, "url": 'https://example.com/雪?q=a,b&x="c"', "loading": False}],
            }
        ],
    }


def ax_window(title: str = 'Notes, "雪"\nline', *, name: str = "Work", close: bool = True) -> AXNode:
    tab = AXNode(
        role="AXRadioButton",
        subrole="AXTabButton",
        title=title + " - Memory usage - 128 MB",
        selected=True,
        children=(AXNode(role="AXButton", title="Close"),) if close else (),
    )
    return AXNode(
        role="AXWindow",
        title="Chrome work",
        children=(
            AXNode(role="AXButton", identifier="AvatarToolbarButton", title=name),
            AXNode(role="AXTabGroup", children=(tab,)),
        ),
    )


def driver(monkeypatch: pytest.MonkeyPatch, data: dict[str, object], nodes: tuple[AXNode, ...] = ()) -> macos.ChromeMac:
    monkeypatch.setattr(macos_jxa, "run_jxa", lambda script: data)
    monkeypatch.setattr(macos.macos_ax, "read_windows", lambda pid: nodes)
    monkeypatch.setattr(macos.ChromeMac, "profiles", lambda self: {"Profile 4": ProfileInfo(name="Work")})
    return macos.ChromeMac()


def test_constructor_does_not_contact_os(monkeypatch: pytest.MonkeyPatch) -> None:
    run = Mock(side_effect=AssertionError("OS called during construction"))
    monkeypatch.setattr(macos_jxa, "run_jxa", run)
    macos.ChromeMac()
    run.assert_not_called()


def test_json_unicode_url_and_native_metadata(monkeypatch: pytest.MonkeyPatch) -> None:
    window = driver(monkeypatch, snapshot(), (ax_window(),)).list_windows()[0]
    tab = window.tabs[0]
    assert window.hwnd == 42 and window.minimized and window.rect == WindowRect(10, 20, 810, 620)
    assert window.profile_dir == "Profile 4" and window.profile_key == "Profile 4"
    assert tab.title == 'Notes, "雪"\nline' and tab.runtime_id == (701,)
    assert tab.url == 'https://example.com/雪?q=a,b&x="c"'
    assert tab.loaded and tab.memory_mb == 128 and not tab.audible and not tab.pinned


def test_elided_script_window_title_matches_full_native_title(monkeypatch: pytest.MonkeyPatch) -> None:
    data = snapshot("A long pull request title")
    data["windows"][0]["title"] = "A long…title"
    node = replace(ax_window("A long pull request title"), title="A long pull request title - Google Chrome – Work")
    result = driver(monkeypatch, data, (node,)).list_windows()[0]
    assert result.profile_dir == "Profile 4"
    assert not result.tabs[0].pinned


def test_untitled_native_window_matches_by_tab_strip(monkeypatch: pytest.MonkeyPatch) -> None:
    node = replace(ax_window(), title="")
    result = driver(monkeypatch, snapshot(), (node,)).list_windows()[0]
    assert result.profile_dir == "Profile 4"
    assert not result.tabs[0].pinned


def test_identical_untitled_strips_are_ambiguous(monkeypatch: pytest.MonkeyPatch) -> None:
    node = replace(ax_window(), title="")
    result = driver(monkeypatch, snapshot(), (node, replace(node, identity=2))).list_windows()[0]
    assert result.profile_dir is None
    assert result.tabs[0].pinned


def test_repeated_native_graph_references_do_not_duplicate_tabs() -> None:
    tab = replace(ax_window().children[1].children[0], identity=701)
    node = AXNode(role="AXWindow", children=(AXNode(children=(tab,)), AXNode(children=(tab,))))
    assert tab_slots(node, 42) == [TabSlot(tab, None)]


def test_conflicting_repeated_native_identity_refuses_metadata() -> None:
    tab = replace(ax_window().children[1].children[0], identity=701)
    node = AXNode(role="AXWindow", children=(tab, replace(tab, selected=False)))
    assert tab_slots(node, 42) == []


def test_serialization_uses_real_json(monkeypatch: pytest.MonkeyPatch) -> None:
    raw = snapshot()
    runner = Mock(return_value=subprocess.CompletedProcess([], 0, json.dumps(raw, ensure_ascii=False), ""))
    monkeypatch.setattr(macos_jxa.subprocess, "run", runner)
    assert _RUN_JXA("query") == raw
    assert runner.call_args.kwargs["input"] == "query"
    assert runner.call_args.args[0] == ["/usr/bin/osascript", "-l", "JavaScript", "-"]


@pytest.mark.parametrize("error", [subprocess.TimeoutExpired("osascript", 30), OSError("missing")])
def test_automation_errors_are_actionable(monkeypatch: pytest.MonkeyPatch, error: Exception) -> None:
    monkeypatch.setattr(macos_jxa.subprocess, "run", Mock(side_effect=error))
    with pytest.raises(macos.ChromeMacError, match="Automation permission"):
        _RUN_JXA("query")


def test_permission_failure_protects_tabs(monkeypatch: pytest.MonkeyPatch) -> None:
    reader = driver(monkeypatch, snapshot())
    monkeypatch.setattr(macos.macos_ax, "read_windows", Mock(side_effect=AccessibilityUnavailableError("denied")))
    tab = reader.list_windows()[0].tabs[0]
    assert tab.pinned and tab.audible
    assert tab.alerts == ("Accessibility metadata unavailable",)
    assert not tab.loaded and tab.memory_mb is None


def test_page_title_does_not_supply_safety_metadata(monkeypatch: pytest.MonkeyPatch) -> None:
    title = "Page - Audio playing - Memory usage - 500 MB"
    tab = driver(monkeypatch, snapshot(title)).list_windows()[0].tabs[0]
    assert tab.title == title and tab.name == title
    assert not tab.loaded and tab.memory_mb is None


def test_audio_and_group_come_from_ax(monkeypatch: pytest.MonkeyPatch) -> None:
    tab = AXNode(
        role="AXRadioButton",
        subrole="AXTabButton",
        selected=True,
        title="Notes - Audio playing - Part of group Old - Memory usage - 2 GB",
        children=(AXNode(role="AXButton", title="Close tab"),),
    )
    group = AXNode(
        role="AXTabGroup",
        children=(
            AXNode(role="AXTabGroup", identity=901, title=" group Current – 1 tab – Expanded"),
            tab,
        ),
    )
    node = replace(ax_window("Notes"), children=(group,))
    result = driver(monkeypatch, snapshot("Notes"), (node,)).list_windows()[0].tabs[0]
    assert result.audible and result.memory_mb == 2048
    assert result.group is not None and result.group.title == "Current"


def test_hidden_close_control_does_not_mean_pinned(monkeypatch: pytest.MonkeyPatch) -> None:
    tab = driver(monkeypatch, snapshot(), (ax_window(close=False),)).list_windows()[0].tabs[0]
    assert not tab.pinned and not tab.audible


def test_pinned_label_marks_tab_pinned(monkeypatch: pytest.MonkeyPatch) -> None:
    node = ax_window(close=False)
    tab = replace(node.children[1].children[0], title='Notes, "雪"\nline \u2013 Pinned - Memory usage - 128 MB')
    node = replace(node, children=(node.children[0], replace(node.children[1], children=(tab,))))
    result = driver(monkeypatch, snapshot(), (node,)).list_windows()[0].tabs[0]
    assert result.pinned and not result.audible and result.alerts == ()


def test_pinned_label_with_close_control_is_inconsistent() -> None:
    tab = replace(ax_window().children[1].children[0], title="A \u2013 Pinned")
    assert not pinned_labels_consistent([tab])


def test_pinned_label_after_unpinned_tab_is_inconsistent() -> None:
    plain = AXNode(role="AXRadioButton", subrole="AXTabButton", title="A")
    pinned = replace(plain, title="B \u2013 Pinned")
    assert pinned_labels_consistent([pinned, plain])
    assert not pinned_labels_consistent([plain, pinned])


def test_no_default_profile_guess_and_unknown_windows_are_isolated(monkeypatch: pytest.MonkeyPatch) -> None:
    data = snapshot()
    windows = data["windows"]
    assert isinstance(windows, list)
    windows.append({**windows[0], "id": "43", "tabs": [{"id": "702", "title": "Other", "url": "about:blank"}]})
    result = driver(monkeypatch, data).list_windows()
    assert all(window.profile_dir is None for window in result)
    assert result[0].profile_key != result[1].profile_key


def test_profile_mapping_requires_corroborated_control(monkeypatch: pytest.MonkeyPatch) -> None:
    reader = driver(monkeypatch, snapshot(), (replace(ax_window(), children=(AXNode(role="AXButton", title="Work"),)),))
    assert reader.list_windows()[0].profile_dir is None


def test_multiple_profile_windows_map_from_ui(monkeypatch: pytest.MonkeyPatch) -> None:
    data = snapshot("First")
    windows = data["windows"]
    assert isinstance(windows, list)
    windows.append(
        {
            **windows[0],
            "id": "43",
            "title": "Home window",
            "tabs": [{"id": "702", "title": "Second", "url": "about:blank"}],
        }
    )
    nodes = (ax_window("First"), replace(ax_window("Second", name="Home"), title="Home window"))
    reader = driver(monkeypatch, data, nodes)
    monkeypatch.setattr(
        reader, "profiles", lambda: {"Profile 4": ProfileInfo(name="Work"), "Default": ProfileInfo(name="Home")}
    )
    assert [window.profile_key for window in reader.list_windows()] == ["Profile 4", "Default"]


def test_duplicate_profile_names_are_ambiguous(monkeypatch: pytest.MonkeyPatch) -> None:
    reader = driver(monkeypatch, snapshot(), (ax_window(),))
    monkeypatch.setattr(
        reader, "profiles", lambda: {"Default": ProfileInfo(name="Work"), "Profile 4": ProfileInfo(name="Work")}
    )
    assert reader.list_windows()[0].profile_dir is None


def test_private_windows_do_not_share_normal_profile_scope(monkeypatch: pytest.MonkeyPatch) -> None:
    data = snapshot()
    windows = data["windows"]
    assert isinstance(windows, list)
    windows[0]["mode"] = "incognito"
    result = driver(monkeypatch, data, (ax_window(),)).list_windows()[0]
    assert result.profile_key != "Profile 4"


def test_mismatched_selection_protects_tab(monkeypatch: pytest.MonkeyPatch) -> None:
    node = ax_window()
    strip = node.children[1]
    node = replace(node, children=(replace(strip, children=(replace(strip.children[0], selected=False),)),))
    assert driver(monkeypatch, snapshot(), (node,)).list_windows()[0].tabs[0].pinned


def test_web_content_cannot_impersonate_profile_or_tabs(monkeypatch: pytest.MonkeyPatch) -> None:
    node = AXNode(
        role="AXWindow", title="Chrome work", children=(AXNode(role="AXWebArea", children=ax_window().children),)
    )
    window = driver(monkeypatch, snapshot(), (node,)).list_windows()[0]
    assert window.profile_dir is None and window.tabs[0].pinned


def test_foreground_reports_zero_when_another_app_is_active(monkeypatch: pytest.MonkeyPatch) -> None:
    data = snapshot()
    data["frontmost"] = False
    assert driver(monkeypatch, data).foreground_window() == 0
    data["frontmost"] = True
    assert driver(monkeypatch, data).foreground_window() == 42


def test_foreground_failure_propagates_for_unattended_safety(monkeypatch: pytest.MonkeyPatch) -> None:
    reader = driver(monkeypatch, snapshot())
    monkeypatch.setattr(macos_jxa, "run_jxa", Mock(side_effect=macos.ChromeMacError("denied")))
    with pytest.raises(macos.ChromeMacError):
        reader.foreground_window()


@pytest.mark.parametrize("value", ["x", "1; do shell script", True, -4, "４２", str(2**53)])
def test_invalid_ids_fail_closed(monkeypatch: pytest.MonkeyPatch, value: object) -> None:
    data = snapshot()
    windows = data["windows"]
    assert isinstance(windows, list)
    windows[0]["id"] = value
    with pytest.raises(macos.ChromeMacError):
        driver(monkeypatch, data).list_windows()


def test_no_running_chrome_returns_empty(monkeypatch: pytest.MonkeyPatch) -> None:
    assert driver(monkeypatch, {"pid": 0, "frontmost": False, "windows": []}).list_windows() == []


def test_toolbar_profile_without_identifier(monkeypatch: pytest.MonkeyPatch) -> None:
    toolbar = AXNode(
        role="AXToolbar",
        children=(
            AXNode(role="AXButton", title="Work"),
            AXNode(role="AXPopUpButton", title="Customize and control Google Chrome"),
        ),
    )
    node = replace(ax_window(), children=(toolbar, ax_window().children[1]))
    assert driver(monkeypatch, snapshot(), (node,)).list_windows()[0].profile_dir == "Profile 4"


_SHARED_ACCOUNT = {
    "Default": ProfileInfo(name="Personal", gaia_given_name="Ryan"),
    "Profile 3": ProfileInfo(name="Main", gaia_given_name="Ryan"),
}


def test_signed_in_avatar_label_names_profile(monkeypatch: pytest.MonkeyPatch) -> None:
    toolbar = AXNode(
        role="AXToolbar",
        children=(AXNode(role="AXButton", title="Ryan (Main)"), AXNode(role="AXPopUpButton", title="Chrome")),
    )
    node = replace(ax_window(), children=(toolbar, ax_window().children[1]))
    reader = driver(monkeypatch, snapshot(), (node,))
    monkeypatch.setattr(reader, "profiles", lambda: _SHARED_ACCOUNT)
    assert reader.list_windows()[0].profile_dir == "Profile 3"


def test_shared_account_name_alone_is_ambiguous(monkeypatch: pytest.MonkeyPatch) -> None:
    node = replace(ax_window(), children=(replace(ax_window().children[0], title="Ryan"), ax_window().children[1]))
    reader = driver(monkeypatch, snapshot(), (node,))
    monkeypatch.setattr(reader, "profiles", lambda: _SHARED_ACCOUNT)
    assert reader.list_windows()[0].profile_dir is None


def test_actual_local_state_parsing(monkeypatch: pytest.MonkeyPatch, tmp_path: Path) -> None:
    local_state = tmp_path / "Library/Application Support/Google/Chrome/Local State"
    local_state.parent.mkdir(parents=True)
    local_state.write_text(json.dumps({"profile": {"info_cache": {"Profile 2": {"name": "仕事"}}}}), encoding="utf-8")
    monkeypatch.setattr(macos.Path, "home", lambda: tmp_path)
    assert macos.ChromeMac().profiles() == {"Profile 2": ProfileInfo(name="仕事")}
    local_state.write_text("corrupted", encoding="utf-8")
    assert macos.ChromeMac().profiles() == {}


def test_stable_duplicate_tab_titles_use_verified_order(monkeypatch: pytest.MonkeyPatch) -> None:
    data = snapshot("Duplicate")
    windows = data["windows"]
    assert isinstance(windows, list)
    windows[0]["tabs"].append({"id": "702", "title": "Duplicate", "url": "https://other.example/"})
    node = ax_window("Duplicate")
    tab = node.children[1].children[0]
    node = replace(
        node, children=(node.children[0], replace(node.children[1], children=(tab, replace(tab, selected=False))))
    )
    result = driver(monkeypatch, data, (node,)).list_windows()[0].tabs
    assert not any(tab.pinned for tab in result)
    assert [tab.runtime_id for tab in result] == [(701,), (702,)]


def test_native_state_that_keeps_changing_protects_tabs(monkeypatch: pytest.MonkeyPatch) -> None:
    first, after = snapshot("Before"), snapshot("After")
    reader = driver(monkeypatch, first, (ax_window("Before"),))
    monkeypatch.setattr(macos_jxa, "run_jxa", Mock(side_effect=cycle([first, after])))
    monkeypatch.setattr(macos.time, "sleep", lambda seconds: None)
    assert reader.list_windows()[0].tabs[0].pinned


def test_a_read_that_settles_on_retry_keeps_its_metadata(monkeypatch: pytest.MonkeyPatch) -> None:
    first, after = snapshot("Before"), snapshot("After")
    reader = driver(monkeypatch, first, (ax_window("After"),))
    monkeypatch.setattr(macos_jxa, "run_jxa", Mock(side_effect=[first, after, after]))
    monkeypatch.setattr(macos.time, "sleep", lambda seconds: None)
    tab = reader.list_windows()[0].tabs[0]
    assert tab.title == "After" and not tab.pinned


def test_group_suffix_without_header_does_not_allow_mutation(monkeypatch: pytest.MonkeyPatch) -> None:
    node = ax_window("Notes")
    strip = node.children[1]
    tab = replace(strip.children[0], title="Notes - Part of group Stale - Memory usage - 128 MB")
    node = replace(node, children=(node.children[0], replace(strip, children=(tab,))))
    result = driver(monkeypatch, snapshot("Notes"), (node,)).list_windows()[0].tabs[0]
    assert result.group is None and result.pinned


def slot(title: str, *, selected: bool = False) -> AXNode:
    return AXNode(
        role="AXRadioButton",
        subrole="AXTabButton",
        title=title,
        selected=selected,
        children=(AXNode(role="AXButton", title="Close tab"),),
    )


def header(title: str, count: int, identity: int = 901, *, expanded: bool | None = True) -> AXNode:
    # TabGroupHeader kTabList -> AXTabGroup, matching Chromium's macOS bridge.
    label = f"group {title}" if title else "unnamed group"
    return AXNode(role="AXTabGroup", identity=identity, title=f" {label} – {count} tabs", expanded=expanded)


def test_macos_sibling_group_count_does_not_capture_loose_tabs() -> None:
    container = AXNode(
        role="AXTabGroup",
        children=(
            header("Work", 1),
            slot("Grouped - Part of group Old"),
            slot("Ungrouped"),
        ),
    )
    result = tab_slots(container, 42)
    assert [slot.node.title for slot in result] == ["Grouped - Part of group Old", "Ungrouped"]
    first = result[0].group
    assert first is not None and first.title == "Work"
    assert first.runtime_id == (42, 901, -1)
    assert result[1].group is None


def test_adjacent_sibling_groups_and_loose_tabs() -> None:
    container = AXNode(
        role="AXTabGroup",
        children=(
            slot("Before"),
            header("Work", 2),
            slot("One"),
            slot("Two"),
            header("", 1, 902),
            slot("Three"),
            slot("After"),
        ),
    )
    result = tab_slots(container, 42)
    assert [slot.group.title if slot.group is not None else None for slot in result] == [None, "Work", "Work", "", None]
    assert result[1].group == result[2].group
    assert result[2].group != result[3].group


@pytest.mark.parametrize(
    "children",
    [
        (header("Work", 1, expanded=False), slot("Loose after hidden group")),
        (header("Work", 1, expanded=None), slot("Unknown expansion")),
        (header("Work", 2), slot("Only visible member")),
        (header("Work", 2), slot("One"), header("Next", 1, 902), slot("Two")),
        (header("Work", 0), slot("Loose")),
        (replace(header("Work", 1), title=" group Work – 1 tab – Collapsed"), slot("One")),
    ],
)
def test_ambiguous_or_collapsed_sibling_groups_refuse_membership(children: tuple[AXNode, ...]) -> None:
    assert tab_slots(AXNode(role="AXTabGroup", children=children), 42) == []


def test_collapsed_header_protects_even_visible_ungrouped_tab(monkeypatch: pytest.MonkeyPatch) -> None:
    node = AXNode(
        role="AXWindow",
        title="Chrome work",
        children=(
            AXNode(role="AXTabGroup", children=(header("Hidden", 1, expanded=False), slot("Visible", selected=True))),
        ),
    )
    result = driver(monkeypatch, snapshot("Visible"), (node,)).list_windows()[0].tabs[0]
    assert result.pinned and result.audible and result.group is None


def test_unproven_nested_wrapper_does_not_assign_group_membership() -> None:
    node = AXNode(role="AXGroup", children=(header("Work", 1), slot("One")))
    assert tab_slots(node, 42) == []


@pytest.mark.parametrize(
    "name",
    [
        " group Work – 2 tabs – 1 tab – Expanded",
        " group Work – 1 tab, • Preview – 2 tabs – Expanded",
    ],
)
def test_ambiguous_header_count_never_captures_following_loose_tab(name: str) -> None:
    container = AXNode(
        role="AXTabGroup",
        children=(
            AXNode(role="AXTabGroup", identity=901, title=name, expanded=True),
            slot("Grouped"),
            slot("Loose"),
        ),
    )
    assert tab_slots(container, 42) == []
