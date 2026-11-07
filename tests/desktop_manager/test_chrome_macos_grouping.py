"""Modeled native menu actions, conservative refusal and context restoration."""

from __future__ import annotations

from contextlib import contextmanager
from dataclasses import replace
from unittest.mock import Mock

import pytest

from desktop_manager.chrome.driver import ChromeDriver
from desktop_manager.chrome.drivers import macos, macos_actions, macos_jxa, macos_ui
from desktop_manager.chrome.drivers.macos_ax import AccessibilityUnavailableError, AXNode
from desktop_manager.chrome.drivers.macos_jxa import ProfileInfo, Snapshot
from desktop_manager.chrome.drivers.macos_read import walk
from desktop_manager.chrome.drivers.macos_ui import Focus, ScreenPos
from desktop_manager.chrome.group_run import GroupRun, run_group_plan
from desktop_manager.chrome.grouping import plan_groups
from desktop_manager.chrome.tabs import ChromeTab, ChromeWindow, TabGroup
from rig.typed_json import decode

pytestmark = pytest.mark.ci


def test_the_mac_driver_has_no_addressable_desktops():
    """Spaces cannot be addressed, so the shared run loop never reaches the Windows desktop API."""
    assert macos.ChromeMac().desktops.current() is None


def tab(identity=701, hwnd=42, title="Source", **changes):
    return replace(
        ChromeTab((identity,), hwnd, 0, title, title, True, False, True, False, url=f"https://example.com/{identity}"),
        **changes,
    )


def window(hwnd, tabs, **changes):
    tabs = tuple(replace(item, index=index, window_hwnd=hwnd) for index, item in enumerate(tabs))
    return replace(
        ChromeWindow(
            hwnd, next(item.title for item in tabs if item.selected), "Profile 4", "Work", hwnd, False, None, tabs
        ),
        **changes,
    )


class Model:
    def __init__(self, monkeypatch):
        self.windows = [window(42, [tab()]), window(43, [tab(702, 43, "Home")])]
        self.front = 42
        self.phase = "idle"
        self.operation = "group"
        self.logs = []
        self.group_title = "Work"
        self.home = 42
        self.new = True
        self.duplicate_menu = False
        self.multiple_menus = False
        self.fail_point = False
        self.focus = True
        self.apply_result = True
        self.saved = False
        self.change_before_press = None
        self.source_id = 701
        self.ui = FakeUI(self)
        self.driver = macos.ChromeMac()
        monkeypatch.setattr(macos_actions, "NativeUI", lambda: self.ui)
        monkeypatch.setattr(self.driver, "list_windows", lambda: list(self.windows))
        monkeypatch.setattr(self.driver, "snapshot", self.snapshot)
        monkeypatch.setattr(self.driver, "profiles", lambda: {"Profile 4": ProfileInfo(name="Work")})
        monkeypatch.setattr(self.driver, "foreground_window", lambda: self.front)
        monkeypatch.setattr(self.driver, "activate", self.activate)

    def activate(self, hwnd, timeout):
        self.front = hwnd
        self.logs.append(("activate", hwnd))
        return True

    def snapshot(self):
        return decode(Snapshot, self.raw_snapshot())

    def raw_snapshot(self):
        return {
            "pid": 123,
            "frontmost": self.front > 0,
            "windows": [
                {
                    "id": str(win.hwnd),
                    "index": 1 if win.hwnd == self.front else 2,
                    "title": win.title,
                    "active": next(item.index + 1 for item in win.tabs if item.selected),
                    "minimized": win.minimized,
                    "mode": "normal",
                    "tabs": [
                        {"id": str(item.runtime_id[0]), "title": item.title, "url": item.url} for item in win.tabs
                    ],
                }
                for win in self.windows
            ],
        }

    def replace_tab(self, identity, **changes):
        self.windows = [
            replace(
                win,
                tabs=tuple(replace(item, **changes) if item.runtime_id == (identity,) else item for item in win.tabs),
            )
            for win in self.windows
        ]

    def move(self, group=None):
        source = next(item for win in self.windows for item in win.tabs if item.runtime_id == (self.source_id,))
        target = next(win for win in self.windows if win.hwnd == self.home)
        moved = replace(source, window_hwnd=self.home, index=len(target.tabs), group=group, selected=False)
        self.windows = [
            replace(win, tabs=(*win.tabs, moved))
            if win.hwnd == self.home
            else replace(win, tabs=tuple(item for item in win.tabs if item.runtime_id != source.runtime_id))
            for win in self.windows
        ]
        self.windows = [win for win in self.windows if win.tabs]
        self.front = self.home

    def tree(self):
        nodes = []
        for win in self.windows:
            slots = []
            groups = set()
            for item in win.tabs:
                if item.group and item.group.runtime_id not in groups:
                    groups.add(item.group.runtime_id)
                    count = sum(other.group == item.group for other in win.tabs)
                    name = f" group {item.group.title}" if item.group.title else " unnamed group"
                    slots.append(
                        AXNode(
                            identity=item.group.runtime_id[1],
                            role="AXTabGroup",
                            title=f"{name} – {count} tabs – Expanded",
                            expanded=True,
                        )
                    )
                suffix = f" – Part of group {item.group.title}" if item.group else ""
                slots.append(
                    AXNode(
                        identity=item.runtime_id[0],
                        role="AXRadioButton",
                        subrole="AXTabButton",
                        title=item.title + suffix + " - Memory usage - 12 MB",
                        selected=item.selected,
                        children=(AXNode(role="AXButton", title="Close"),),
                    )
                )
            children = [
                AXNode(role="AXButton", identifier="avatar", title="Work"),
                AXNode(role="AXTabGroup", children=tuple(slots)),
            ]
            if self.phase == "bubble" and win.hwnd == self.front:
                children.append(
                    AXNode(
                        role="AXGroup",
                        subrole="AXApplicationDialog",
                        children=(
                            AXNode(role="AXTextField", title="Tab-group title"),
                            AXNode(role="AXButton", title="Ungroup"),
                        ),
                    )
                )
            if self.phase == "confirm" and win.hwnd == self.front:
                children.append(
                    AXNode(
                        role="AXGroup",
                        subrole="AXApplicationDialog",
                        title="Ungroup Tab Group?",
                        children=(AXNode(role="AXButton", title="Ungroup"),),
                    )
                )
            nodes.append(AXNode(role="AXWindow", title=win.title, children=tuple(children)))
        if self.phase in ("tab", "submenu"):
            if self.operation == "move":
                parent_name = "Move Tab to Another Window"
                target = next(win for win in self.windows if win.hwnd == self.home)
                count = len(target.tabs) - 1
                suffix = f" and {count} other tab" + ("s" if count != 1 else "") if count else ""
                choices = [
                    AXNode(role="AXMenuItem", title="New Window"),
                    AXNode(role="AXMenuItem", title=target.selected_tab.title + suffix),
                ]
            elif self.new and not any(item.group for win in self.windows for item in win.tabs):
                parent_name = "Add Tab to New Group"
                choices = []
            else:
                parent_name = "Add Tab to Group"
                choices = [
                    AXNode(role="AXMenuItem", title="New Group"),
                    AXNode(role="AXMenuItem", title=self.group_title),
                ]
            if self.duplicate_menu:
                choices.append(choices[-1])
            children = (AXNode(role="AXMenu", children=tuple(choices)),) if self.phase == "submenu" else ()
            menu = AXNode(
                role="AXMenu",
                children=(
                    AXNode(role="AXMenuItem", title="Close"),
                    AXNode(role="AXMenuItem", title=parent_name, children=children),
                ),
            )
            nodes.append(menu)
            if self.multiple_menus:
                nodes.append(menu)
        return AXNode(role="AXApplication", children=tuple(nodes))

    def group(self, *, new=True, home=42, title="Work"):
        self.new, self.home, self.group_title = new, home, title
        return self.driver.group_tab(self.windows[0].tabs[0], title=title, new=new, home_hwnd=home, timeout_seconds=0)


class FakeUI:
    def __init__(self, model):
        self.model = model

    def __enter__(self):
        self.model.logs.append("enter")
        return self

    def __exit__(self, *args):
        self.model.logs.append("exit")

    def cursor(self):
        return (3, 4)

    def restore_cursor(self, point):
        self.model.logs.append(("cursor", point))

    def application_tree(self, pid):
        if self.model.phase == "submenu" and self.model.change_before_press:
            action, self.model.change_before_press = self.model.change_before_press, None
            action()
        return self.model.tree()

    def frontmost(self, pid):
        return self.model.front > 0

    def browser_windows(self, pid):
        return tuple(node for node in walk(self.application_tree(pid)) if node.role == "AXWindow")

    def show_menu(self, node):
        if self.model.fail_point:
            raise AccessibilityUnavailableError("AX action AXShowMenu failed (-25205)")
        self.model.logs.append(("menu", node.role))
        self.model.logs.append("show_menu")
        self.model.phase = "bubble" if self.model.operation == "ungroup" else "tab"

    def press(self, node):
        model = self.model
        model.logs.append(("press", node.name))
        if node.name in (*macos_actions._GROUP, *macos_actions._MOVE):
            model.phase = "submenu"
        elif node.name in (*macos_actions._FLAT, *macos_actions._NEW):
            if model.apply_result:
                model.replace_tab(model.source_id, group=TabGroup((model.home, 900, -1), ""))
            model.phase = "bubble"
        elif node.name == "Ungroup":
            if model.saved and model.phase != "confirm":
                model.phase = "confirm"
            else:
                if model.apply_result:
                    source = next(win for win in model.windows if win.hwnd == model.front)
                    group = next(item.group for item in source.tabs if item.runtime_id == (model.source_id,))
                    for member in source.tabs:
                        if member.group == group:
                            model.replace_tab(member.runtime_id[0], group=None)
                model.phase = "idle"
        elif model.operation == "move":
            if model.apply_result:
                model.move()
            model.phase = "idle"
        else:
            if model.apply_result:
                target = next(
                    item.group
                    for win in model.windows
                    for item in win.tabs
                    if item.group and item.group.title == model.group_title
                )
                source = next(
                    item for win in model.windows for item in win.tabs if item.runtime_id == (model.source_id,)
                )
                if model.home == source.window_hwnd:
                    model.replace_tab(model.source_id, group=target)
                else:
                    model.move(target)
            model.phase = "idle"

    def focused(self, node):
        return self.model.focus

    def set_text(self, node, text, guard):
        guard()
        self.model.logs.append(("text", text))
        group = next(
            item.group for win in self.model.windows for item in win.tabs if item.runtime_id == (self.model.source_id,)
        )
        self.model.replace_tab(self.model.source_id, group=replace(group, title=text))

    def key(self, code):
        self.model.logs.append(("key", code))
        self.model.phase = "idle"

    def escape(self, pid):
        self.model.logs.append(("escape", pid))
        self.model.phase = "idle"


def test_driver_conforms_and_constructor_is_safe(monkeypatch):
    monkeypatch.setattr(macos, "NativeUI", Mock(side_effect=AssertionError("constructed native UI")))
    monkeypatch.setattr(macos_jxa, "run_jxa", Mock(side_effect=AssertionError("contacted OS")))
    driver: ChromeDriver = macos.ChromeMac()  # conformance is checked statically, by this annotation
    assert driver.saved_groups(42, "Profile 4") == []


def test_new_group_flat_menu_and_unicode_title(monkeypatch):
    model = Model(monkeypatch)
    result = model.group(title='owner/雪 "quote"')
    assert result.grouped and result.detail == "created"
    assert model.windows[0].tabs[0].group.title == 'owner/雪 "quote"'
    assert ("text", 'owner/雪 "quote"') in model.logs
    assert model.logs[-2:] == [("cursor", (3, 4)), "exit"]


def test_new_group_submenu(monkeypatch):
    model = Model(monkeypatch)
    model.replace_tab(702, group=TabGroup((43, 901, -1), "Other"))
    assert model.group().grouped
    assert ("press", "New Group") in model.logs


@pytest.mark.parametrize("home", [42, 43])
def test_join_confirms_exact_group_and_stable_id(monkeypatch, home):
    model = Model(monkeypatch)
    grouped = tab(703, home, "Member", selected=False, group=TabGroup((home, 901, -1), "Work"))
    model.windows = [
        replace(win, tabs=(*win.tabs, replace(grouped, index=len(win.tabs)))) if win.hwnd == home else win
        for win in model.windows
    ]
    original_id = model.windows[0].tabs[0].runtime_id
    assert model.group(new=False, home=home).grouped
    found = next(item for win in model.windows for item in win.tabs if item.runtime_id == original_id)
    assert found.window_hwnd == home and found.group == grouped.group


def test_move_returns_outcome_and_same_stable_tab_id(monkeypatch):
    model = Model(monkeypatch)
    model.operation, model.home = "move", 43
    original = model.windows[0].tabs[0]
    carried = model.driver.move_to_window(original, 43, timeout_seconds=0)
    outcome, moved = carried.outcome, carried.tab
    assert outcome.grouped and moved.runtime_id == original.runtime_id and moved.window_hwnd == 43
    assert moved.group is None and moved.url == original.url


@pytest.mark.parametrize("saved", [False, True])
def test_ungroup_header_and_saved_confirmation(monkeypatch, saved):
    model = Model(monkeypatch)
    model.operation, model.saved = "ungroup", saved
    model.replace_tab(701, group=TabGroup((42, 900, -1), "Work"))
    result = model.driver.ungroup(model.windows[0].tabs[0], title="Work", timeout_seconds=0.01)
    assert result.grouped and model.windows[0].tabs[0].group is None
    assert ("menu", "AXTabGroup") in model.logs
    assert model.logs.count(("press", "Ungroup")) == (2 if saved else 1)


@pytest.mark.parametrize(
    "changes",
    [dict(pinned=True), dict(audible=True), dict(url=None), dict(alerts=("Accessibility metadata unavailable",))],
)
def test_protected_tabs_refuse_without_click(monkeypatch, changes):
    model = Model(monkeypatch)
    model.replace_tab(701, **changes)
    assert not model.group().grouped
    assert "show_menu" not in model.logs


@pytest.mark.parametrize("changes", [dict(profile_dir=None), dict(profile_scope="private"), dict(title="Home")])
def test_ambiguous_or_unknown_window_refuses_without_click(monkeypatch, changes):
    model = Model(monkeypatch)
    model.windows[0] = replace(model.windows[0], **changes)
    assert not model.group().grouped
    assert "show_menu" not in model.logs


@pytest.mark.parametrize(
    "changes",
    [dict(title="Changed"), dict(url="https://changed.invalid"), dict(group=TabGroup((42, 900, -1), "Other"))],
)
def test_snapshot_tab_revalidated_before_click(monkeypatch, changes):
    model = Model(monkeypatch)
    before = model.windows[0].tabs[0]
    model.replace_tab(701, **changes)
    result = model.driver.group_tab(before, title="Work", new=True, timeout_seconds=0)
    assert not result.grouped and "show_menu" not in model.logs


def test_duplicate_group_titles_refuse_before_menu(monkeypatch):
    model = Model(monkeypatch)
    model.replace_tab(702, group=TabGroup((43, 901, -1), "Work"))
    other = tab(703, 42, "Other", selected=False, group=TabGroup((42, 902, -1), "Work"))
    model.windows[0] = replace(model.windows[0], tabs=(*model.windows[0].tabs, replace(other, index=1)))
    assert not model.group(new=False).grouped
    assert "show_menu" not in model.logs


def test_duplicate_menu_entry_escapes_and_restores_cursor(monkeypatch):
    model = Model(monkeypatch)
    model.replace_tab(702, group=TabGroup((43, 901, -1), "Work"))
    model.duplicate_menu = True
    result = model.group(new=False, home=43)
    assert not result.grouped and "ambiguous" in result.detail
    assert ("escape", 123) in model.logs and ("press", "Work") not in model.logs
    assert model.logs[-2:] == [("cursor", (3, 4)), "exit"]


def test_multiple_menus_refuse_and_escape(monkeypatch):
    model = Model(monkeypatch)
    model.multiple_menus = True
    assert not model.group().grouped
    assert ("escape", 123) in model.logs


def test_menu_that_will_not_open_refuses(monkeypatch):
    model = Model(monkeypatch)
    model.fail_point = True
    assert not model.group().grouped
    assert "show_menu" not in model.logs


def test_editor_focus_loss_never_types(monkeypatch):
    model = Model(monkeypatch)
    model.focus = False
    assert not model.group().grouped
    assert not any(isinstance(log, tuple) and log[0] == "text" for log in model.logs)
    assert ("escape", 123) in model.logs


@pytest.mark.parametrize("operation", ["group", "move", "ungroup"])
def test_invoked_action_needs_postcondition(monkeypatch, operation):
    model = Model(monkeypatch)
    model.apply_result = False
    model.operation = operation
    if operation == "move":
        model.home = 43
        carried = model.driver.move_to_window(model.windows[0].tabs[0], 43, timeout_seconds=0)
        outcome, moved = carried.outcome, carried.tab
        assert moved is None
    elif operation == "ungroup":
        model.replace_tab(701, group=TabGroup((42, 900, -1), "Work"))
        outcome = model.driver.ungroup(model.windows[0].tabs[0], title="Work", timeout_seconds=0)
    else:
        outcome = model.group()
    assert not outcome.grouped and ("escape", 123) in model.logs


def test_target_changes_after_menu_open_refuses(monkeypatch):
    model = Model(monkeypatch)
    model.replace_tab(702, group=TabGroup((43, 901, -1), "Work"))
    model.change_before_press = lambda: model.replace_tab(702, group=TabGroup((43, 902, -1), "Work"))
    result = model.group(new=False, home=43)
    assert not result.grouped and ("press", "Work") not in model.logs
    assert ("escape", 123) in model.logs


@pytest.mark.parametrize("home", [42, 43])
@pytest.mark.parametrize("change", ["id", "title", "url", "leave", "join"])
def test_target_members_revalidated_before_join_press(monkeypatch, home, change):
    model = Model(monkeypatch)
    group = TabGroup((home, 901, -1), "Work")
    members = [tab(identity, home, f"Member {identity}", selected=False, group=group) for identity in (703, 704)]
    model.windows = [
        replace(
            win,
            tabs=(*win.tabs, *(replace(member, index=len(win.tabs) + index) for index, member in enumerate(members))),
        )
        if win.hwnd == home
        else win
        for win in model.windows
    ]
    source = model.windows[0].tabs[0]

    def alter_target():
        if change == "join":
            model.windows = [
                replace(
                    win,
                    tabs=(*win.tabs, tab(705, home, "New member", index=len(win.tabs), selected=False, group=group)),
                )
                if win.hwnd == home
                else win
                for win in model.windows
            ]
        else:
            changes = {
                "id": {"runtime_id": (999,)},
                "title": {"title": "Changed member"},
                "url": {"url": "https://changed.invalid"},
                "leave": {"group": None},
            }
            model.replace_tab(703, **changes[change])

    model.change_before_press = alter_target
    result = model.group(new=False, home=home)
    if change in ("id", "url"):
        # Invisible to accessibility while the menu holds Chrome; the arrival check refuses after the press.
        assert not result.grouped and "Resulting group membership differs" in result.detail
        return
    assert not result.grouped and "Tab strip changed" in result.detail
    assert ("press", "Work") not in model.logs
    assert ("escape", 123) in model.logs
    current = next(item for win in model.windows for item in win.tabs if item.runtime_id == source.runtime_id)
    assert current == source and current.window_hwnd == 42 and current.group is None


def test_foreground_loss_before_press_refuses(monkeypatch):
    model = Model(monkeypatch)
    model.replace_tab(702, group=TabGroup((43, 901, -1), "Work"))
    model.change_before_press = lambda: setattr(model, "front", 0)
    assert not model.group(new=False, home=43).grouped
    assert ("press", "Work") not in model.logs and ("escape", 123) in model.logs


def test_cross_profile_move_refuses(monkeypatch):
    model = Model(monkeypatch)
    model.windows[1] = replace(model.windows[1], profile_dir="Profile 5")
    carried = model.driver.move_to_window(model.windows[0].tabs[0], 43, timeout_seconds=0)
    result, moved = carried.outcome, carried.tab
    assert not result.grouped and moved is None and "show_menu" not in model.logs


def test_window_label_collision_refuses(monkeypatch):
    model = Model(monkeypatch)
    model.operation, model.home = "move", 43
    model.windows.append(window(44, [tab(703, 44, "Home")]))
    carried = model.driver.move_to_window(model.windows[0].tabs[0], 43, timeout_seconds=0)
    result, moved = carried.outcome, carried.tab
    assert not result.grouped and moved is None and ("press", "Home") not in model.logs


@pytest.mark.parametrize(
    "label,title,count,match",
    [
        ("Page", "Page", 1, True),
        ("Page and 1 other tab", "Page", 2, True),
        ("Page and 2 Other Tabs", "Page", 3, True),
        ("Pag… and 2 other tabs", "Page long", 3, False),
        ("Page … and 2 other tabs", "Page long", 3, True),
        ("Wrong", "Page", 1, False),
        ("Page and 3 other tabs", "Page", 3, False),
    ],
)
def test_window_menu_labels(label, title, count, match):
    tabs = [tab(title=title)] + [tab(702 + index, title="Other", selected=False) for index in range(count - 1)]
    assert macos_actions.names_window(label, window(42, tabs)) is match


@pytest.mark.parametrize("body_error,arrived", [(False, True), (True, True), (False, False)])
def test_foreground_restores_actual_previous_focus_and_cursor(monkeypatch, body_error, arrived):
    driver = macos.ChromeMac()
    ui = Mock()
    ui.__enter__ = Mock(return_value=ui)
    ui.__exit__ = Mock(return_value=False)
    ui.cursor.return_value = ScreenPos(9.5, 10.5)
    ui.capture_focus.return_value = Focus(12345, 67890)
    monkeypatch.setattr(macos, "NativeUI", lambda: ui)
    windows = [
        {"id": "42", "index": 2, "title": "a", "active": 1, "minimized": True, "mode": "normal", "tabs": []},
        {"id": "43", "index": 1, "title": "b", "active": 1, "minimized": False, "mode": "normal", "tabs": []},
    ]
    monkeypatch.setattr(driver, "snapshot", lambda: decode(Snapshot, {"pid": 1, "frontmost": True, "windows": windows}))
    monkeypatch.setattr(driver, "activate", lambda hwnd, timeout: arrived)
    scripts = []
    monkeypatch.setattr(macos_jxa, "run_jxa", lambda script: scripts.append(script))
    try:
        with driver.foreground(42, timeout_seconds=0) as result:
            assert result is arrived
            if body_error:
                raise ValueError("body error")
    except ValueError:
        assert body_error
    ui.restore_focus.assert_called_once_with(Focus(12345, 67890))
    ui.restore_cursor.assert_called_once_with(ScreenPos(9.5, 10.5))
    assert '"target": 42' in scripts[0] and '"front": 43' in scripts[0] and '"minimized": true' in scripts[0]


def test_foreground_restores_cursor_even_when_focus_restore_fails(monkeypatch):
    model = Model(monkeypatch)
    ui = Mock()
    ui.__enter__ = Mock(return_value=ui)
    ui.__exit__ = Mock(return_value=False)
    ui.cursor.return_value = ScreenPos(9, 10)
    ui.restore_focus.side_effect = AccessibilityUnavailableError("previous window vanished")
    monkeypatch.setattr(macos, "NativeUI", lambda: ui)
    monkeypatch.setattr(macos_jxa, "run_jxa", lambda script: None)
    with pytest.raises(AccessibilityUnavailableError), model.driver.foreground(42):
        pass
    ui.restore_cursor.assert_called_once_with(ScreenPos(9, 10))


def test_native_handle_refuses_hash_only_or_changed_control():
    ui = object.__new__(macos_ui.NativeUI)
    node = AXNode(identity=999, role="AXMenuItem", title="Work")
    ui._handles = {}
    with pytest.raises(AccessibilityUnavailableError, match="not owned"):
        ui._handle(node)
    ui._handles[id(node)] = 12345
    ui.text = Mock(side_effect=["AXMenuItem", "Wrong"])
    with pytest.raises(AccessibilityUnavailableError, match="changed name"):
        ui._handle(node)


def test_native_press_rechecks_enabled_on_retained_handle():
    ui = object.__new__(macos_ui.NativeUI)
    node = AXNode(role="AXMenuItem", title="Work")
    ui._handle = Mock(return_value=12345)
    ui.scalar = Mock(return_value=False)
    ui._action = Mock()
    with pytest.raises(AccessibilityUnavailableError, match="not enabled"):
        ui.press(node)
    ui._action.assert_not_called()
    ui.scalar.return_value = True
    ui.press(node)
    ui._action.assert_called_once_with(12345, "AXPress")


def test_group_result_rejects_same_title_with_wrong_stable_id(monkeypatch):
    model = Model(monkeypatch)
    original_press = model.ui.press

    def press(node):
        original_press(node)
        if node.name == "Add Tab to New Group":
            model.replace_tab(701, runtime_id=(999,))

    model.ui.press = press
    result = model.group()
    assert not result.grouped and ("escape", 123) in model.logs


def test_ungroup_checks_every_member_before_saved_confirmation(monkeypatch):
    model = Model(monkeypatch)
    model.operation, model.saved = "ungroup", True
    group = TabGroup((42, 900, -1), "Work")
    model.replace_tab(701, group=group)
    model.windows[0] = replace(
        model.windows[0], tabs=(*model.windows[0].tabs, tab(703, 42, "Other", index=1, selected=False, group=group))
    )
    original_press = model.ui.press

    def press(node):
        original_press(node)
        if model.phase == "confirm":
            model.replace_tab(703, url="https://changed.invalid")

    model.ui.press = press
    result = model.driver.ungroup(model.windows[0].tabs[0], title="Work", timeout_seconds=0)
    assert not result.grouped and "content changed" in result.detail
    assert model.logs.count(("press", "Ungroup")) == 1


def test_native_nodes_retain_actual_reference_and_release_at_exit(monkeypatch):
    ui = object.__new__(macos_ui.NativeUI)
    node = AXNode(identity=999, role="AXMenuItem", title="Work")
    monkeypatch.setattr(macos_ui.NativeAX, "node", lambda self, element, depth: node)
    ui.retain, ui.release = Mock(), Mock()
    ui._owned, ui._handles = [], {}
    assert ui.node(12345, 0) is node
    ui.retain.assert_called_once_with(12345)
    assert ui._handles[id(node)] == 12345
    ui.__exit__(None, None, None)
    ui.release.assert_called_once_with(12345)
    assert not ui._owned and not ui._handles


def test_native_unicode_input_uses_utf16_and_focus_guard():
    ui = object.__new__(macos_ui.NativeUI)
    node = AXNode(role="AXTextField", title="Tab-group title")
    ui.key_event = Mock(side_effect=[101, 102])
    ui.set_unicode = Mock()
    ui._handle = Mock(return_value=12345)
    ui.text = Mock(return_value="")
    ui.focused = Mock(return_value=True)
    ui._post = Mock()
    guard = Mock()
    ui.set_text(node, "雪🚀", guard)
    guard.assert_called_once_with()
    calls = ui.set_unicode.call_args_list
    assert len(calls) == 2 and calls[0].args[1] == 3
    assert list(calls[0].args[2]) == [0x96EA, 0xD83D, 0xDE80]
    assert ui._post.call_count == 2


def configure_executor_model(monkeypatch, model):
    """Set menu expectations while retaining the real driver and action methods."""
    original_group = model.driver.group_tab
    original_ungroup = model.driver.ungroup
    original_move = model.driver.move_to_window

    def group(tab, *, title, new, home_hwnd):
        model.operation, model.source_id = "group", tab.runtime_id[0]
        model.group_title, model.new, model.home = title, new, home_hwnd
        return original_group(tab, title=title, new=new, home_hwnd=home_hwnd, timeout_seconds=0)

    def ungroup(tab, *, title):
        model.operation, model.source_id = "ungroup", tab.runtime_id[0]
        return original_ungroup(tab, title=title, timeout_seconds=0)

    def move(tab, target_hwnd):
        model.operation, model.source_id, model.home = "move", tab.runtime_id[0], target_hwnd
        return original_move(tab, target_hwnd, timeout_seconds=0)

    @contextmanager
    def foreground(hwnd):
        model.activate(hwnd, 0)
        yield True

    monkeypatch.setattr(model.driver, "group_tab", group)
    monkeypatch.setattr(model.driver, "ungroup", ungroup)
    monkeypatch.setattr(model.driver, "move_to_window", move)
    monkeypatch.setattr(model.driver, "foreground", foreground)


@pytest.mark.parametrize("seed", [False, True])
def test_planner_executor_consolidates_confirmed_ungroup_with_native_actions(monkeypatch, seed):
    model = Model(monkeypatch)
    title = "repo" if seed else "Google"
    source_title = "Issues · owner/repo" if seed else "Source - Google Docs"
    source = tab(group=TabGroup((42, 900, -1), title), title=source_title)
    home_group = None if seed else TabGroup((43, 901, -1), title)
    home_title = "Home" if seed else "Home - Google Docs"
    members = [tab(702, 43, home_title, group=home_group)]
    if seed:
        # Another repo sets the review window, but has no group to create.
        members += [
            tab(703, 43, "Issues · owner/other", selected=False),
            tab(704, 43, "Pull requests · owner/other", selected=False),
        ]
    else:
        members += [tab(703, 43, "Other - Google Sheets", selected=False, group=home_group)]
    model.windows = [window(42, [source]), window(43, members)]
    configure_executor_model(monkeypatch, model)
    plan = plan_groups(model.windows, current_desktop=None, min_size=3)
    assert len(plan.ungroups) == len(plan.moves) == 1
    assert plan.moves[0].new is seed and plan.moves[0].crosses
    before = plan.moves[0].tab
    assert run_group_plan(model.driver, plan) == GroupRun(1)
    moved = next(item for win in model.windows for item in win.tabs if item.runtime_id == before.runtime_id)
    assert moved.window_hwnd == 43 and moved.group.title == title
    assert (moved.runtime_id, moved.title, moved.url, moved.pinned, moved.audible, moved.alerts) == (
        before.runtime_id,
        before.title,
        before.url,
        before.pinned,
        before.audible,
        before.alerts,
    )
    assert plan.moves[0].tab == before  # The original plan is immutable.


@pytest.mark.parametrize("error", [-25202, -25211, -25204])
def test_native_focus_restores_app_after_window_raise_failure(error):
    ui = object.__new__(macos_ui.NativeUI)
    ui.cf_string, ui.release = Mock(return_value=1), Mock()
    ui.perform = Mock(return_value=error)
    ui._restore_app = Mock()
    if error == -25202:
        ui.restore_focus(Focus(100, 200))
    else:
        with pytest.raises(AccessibilityUnavailableError, match=str(error)):
            ui.restore_focus(Focus(100, 200))
    ui._restore_app.assert_called_once_with(100)


def test_executor_last_tab_move_restores_app_when_saved_focused_window_vanishes(monkeypatch):
    model = Model(monkeypatch)
    source = tab(title="Source - Google Docs")
    target = tab(702, 43, "Home - Google Docs", group=TabGroup((43, 901, -1), "Google"))
    model.windows = [window(42, [source]), window(43, [target])]
    configure_executor_model(monkeypatch, model)
    # Exercise the actual foreground context and native AX restore error path.
    monkeypatch.setattr(model.driver, "foreground", macos.ChromeMac.foreground.__get__(model.driver))
    native = object.__new__(macos_ui.NativeUI)
    native.cf_string, native.release = Mock(return_value=1), Mock()
    native.perform = Mock()
    native.perform.side_effect = lambda *args: 0 if any(win.hwnd == 42 for win in model.windows) else -25202
    native._restore_app = Mock()
    ui = Mock()
    ui.__enter__, ui.__exit__ = Mock(return_value=ui), Mock(return_value=False)
    ui.capture_focus.return_value = Focus(100, 200)
    ui.cursor.return_value = ScreenPos(9, 10)
    ui.restore_focus.side_effect = native.restore_focus
    monkeypatch.setattr(macos, "NativeUI", lambda: ui)
    monkeypatch.setattr(macos_jxa, "run_jxa", Mock(return_value=None))
    plan = plan_groups(model.windows, current_desktop=None)
    assert run_group_plan(model.driver, plan) == GroupRun(1)
    assert [win.hwnd for win in model.windows] == [43]
    native._restore_app.assert_called_once_with(100)
    ui.restore_cursor.assert_called_once_with(ScreenPos(9, 10))


@pytest.mark.parametrize(
    "change", [dict(url="https://changed.invalid"), dict(pinned=True), dict(group=TabGroup((42, 903, -1), "Other"))]
)
def test_confirmed_ungroup_keeps_later_content_safety_and_membership_guards(monkeypatch, change):
    model = Model(monkeypatch)
    source = tab(title="Source - Google Docs", group=TabGroup((42, 900, -1), "Google"))
    target = tab(702, 43, "Home - Google Docs", group=TabGroup((43, 901, -1), "Google"))
    other = tab(703, 43, "Other - Google Sheets", selected=False, group=target.group)
    model.windows = [window(42, [source]), window(43, [target, other])]
    configure_executor_model(monkeypatch, model)
    original = model.driver.ungroup

    def ungroup(tab, *, title):
        result = original(tab, title=title)
        assert result.grouped
        model.replace_tab(701, **change)
        return result

    monkeypatch.setattr(model.driver, "ungroup", ungroup)
    ran = run_group_plan(model.driver, plan_groups(model.windows, current_desktop=None))
    assert ran.moved == 0 and ran.stopped is not None
    assert ("press", "Google") not in model.logs


def test_arrival_waits_out_an_unsettled_read_instead_of_refusing(monkeypatch):
    model = Model(monkeypatch)
    source = model.windows[0].tabs[0]
    unsettled = replace(model.windows[0], profile_scope="(unknown profile: window 42)")
    monkeypatch.setattr(model.driver, "list_windows", lambda: [unsettled, *model.windows[1:]])
    assert macos_actions._arrival(model.driver, source, 42, "Profile 4", None) is None
