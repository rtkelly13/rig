"""Unit tests for desktop_manager.models pure helpers and dataclasses."""

from __future__ import annotations

import pytest
from pydantic import ValidationError

from desktop_manager.models import (
    DesktopPlan,
    LaunchEntry,
    PlacementLayout,
    PlacementSpec,
    Rect,
    WindowSnapshot,
    WorkspacePlan,
    decode_plan_entry,
)

# ---------------------------------------------------------------------------
# decode_plan_entry
# ---------------------------------------------------------------------------


class TestDecodePlanEntry:
    def test_decodes_an_object_into_the_record(self) -> None:
        assert decode_plan_entry(DesktopPlan, {"name": "Work"}) == DesktopPlan(name="Work")

    def test_raises_for_list(self) -> None:
        with pytest.raises(ValueError, match="JSON objects"):
            decode_plan_entry(DesktopPlan, [1, 2, 3])

    @pytest.mark.parametrize("value", ["not a dict", None, 123])
    def test_raises_for_scalars(self, value: object) -> None:
        with pytest.raises(ValueError, match="JSON objects"):
            decode_plan_entry(DesktopPlan, value)

    def test_an_object_that_does_not_fit_raises_a_validation_error(self) -> None:
        with pytest.raises(ValidationError):
            decode_plan_entry(DesktopPlan, {})


# ---------------------------------------------------------------------------
# Rect
# ---------------------------------------------------------------------------


class TestRect:
    def test_right_is_left_plus_width(self) -> None:
        r = Rect(left=10, top=20, width=100, height=50)
        assert r.right == 110

    def test_bottom_is_top_plus_height(self) -> None:
        r = Rect(left=10, top=20, width=100, height=50)
        assert r.bottom == 70

    def test_center_x_even_width(self) -> None:
        r = Rect(left=0, top=0, width=200, height=100)
        assert r.center_x == 100

    def test_center_x_odd_width_truncates(self) -> None:
        r = Rect(left=0, top=0, width=101, height=100)
        assert r.center_x == 50  # integer division truncates

    def test_center_y_even_height(self) -> None:
        r = Rect(left=0, top=0, width=100, height=200)
        assert r.center_y == 100

    def test_center_x_with_offset(self) -> None:
        r = Rect(left=50, top=0, width=100, height=100)
        assert r.center_x == 100

    def test_center_y_with_offset(self) -> None:
        r = Rect(left=0, top=30, width=100, height=100)
        assert r.center_y == 80

    def test_to_dict_keys_and_values(self) -> None:
        r = Rect(left=1, top=2, width=3, height=4)
        assert r.to_dict() == {"left": 1, "top": 2, "width": 3, "height": 4}

    def test_from_dict_round_trip(self) -> None:
        original = Rect(left=5, top=10, width=640, height=480)
        restored = Rect.from_dict(original.to_dict())  # type: ignore[arg-type]
        assert restored == original

    def test_from_dict_with_string_values(self) -> None:
        d = {"left": "0", "top": "0", "width": "1920", "height": "1080"}
        r = Rect.from_dict(d)  # type: ignore[arg-type]
        assert r == Rect(left=0, top=0, width=1920, height=1080)

    def test_negative_coords(self) -> None:
        r = Rect(left=-100, top=-50, width=200, height=100)
        assert r.right == 100
        assert r.bottom == 50


# ---------------------------------------------------------------------------
# PlacementLayout
# ---------------------------------------------------------------------------


class TestPlacementLayout:
    def test_to_dict_without_orientation(self) -> None:
        pl = PlacementLayout(mode="full", slot=1)
        d = pl.to_dict()
        assert d == {"mode": "full", "slot": 1}
        assert "orientation" not in d

    def test_to_dict_with_orientation(self) -> None:
        pl = PlacementLayout(mode="half", slot=2, orientation="left")
        d = pl.to_dict()
        assert d["orientation"] == "left"

    def test_from_dict_defaults_slot_to_1(self) -> None:
        pl = PlacementLayout.from_dict({"mode": "full"})
        assert pl.slot == 1
        assert pl.orientation is None

    def test_from_dict_round_trip(self) -> None:
        original = PlacementLayout(mode="quarter", slot=3, orientation="top-left")
        restored = PlacementLayout.from_dict(original.to_dict())
        assert restored == original

    def test_from_dict_none_orientation_not_stored(self) -> None:
        pl = PlacementLayout.from_dict({"mode": "full", "slot": 1, "orientation": None})
        assert pl.orientation is None


# ---------------------------------------------------------------------------
# PlacementSpec
# ---------------------------------------------------------------------------


class TestPlacementSpec:
    def _make(self) -> PlacementSpec:
        return PlacementSpec(
            screen=1,
            layout=PlacementLayout(mode="full", slot=1),
        )

    def test_to_dict_without_subdivide(self) -> None:
        ps = self._make()
        d = ps.to_dict()
        assert d["screen"] == 1
        assert "layout" in d
        assert "subdivide" not in d

    def test_to_dict_with_subdivide(self) -> None:
        ps = PlacementSpec(
            screen=2,
            layout=PlacementLayout(mode="half", slot=1),
            subdivide=PlacementLayout(mode="third", slot=2),
        )
        d = ps.to_dict()
        assert "subdivide" in d

    def test_from_dict_round_trip_no_subdivide(self) -> None:
        original = self._make()
        restored = PlacementSpec.from_dict(original.to_dict())
        assert restored == original

    def test_from_dict_round_trip_with_subdivide(self) -> None:
        original = PlacementSpec(
            screen=3,
            layout=PlacementLayout(mode="half", slot=1, orientation="right"),
            subdivide=PlacementLayout(mode="quarter", slot=2),
        )
        restored = PlacementSpec.from_dict(original.to_dict())
        assert restored == original

    def test_from_dict_without_subdivide_key(self) -> None:
        ps = PlacementSpec.from_dict({"screen": 1, "layout": {"mode": "full"}})
        assert ps.subdivide is None


# ---------------------------------------------------------------------------
# LaunchEntry
# ---------------------------------------------------------------------------


class TestLaunchEntry:
    def _minimal_entry(self) -> LaunchEntry:
        return LaunchEntry(name="MyApp")

    def test_default_type_is_app(self) -> None:
        entry = self._minimal_entry()
        assert entry.type == "app"

    def test_default_delay_ms(self) -> None:
        assert self._minimal_entry().delay_ms == 900

    def test_to_dict_minimal(self) -> None:
        d = self._minimal_entry().to_dict()
        assert d["name"] == "MyApp"
        assert d["type"] == "app"
        assert d["delay_ms"] == 900
        assert "shell" not in d
        assert "path" not in d
        assert "args" not in d

    def test_to_dict_includes_args_when_nonempty(self) -> None:
        entry = LaunchEntry(name="MyApp", args=["--flag", "value"])
        d = entry.to_dict()
        assert d["args"] == ["--flag", "value"]

    def test_from_dict_minimal(self) -> None:
        entry = LaunchEntry.from_dict({"name": "MyApp"})
        assert entry.name == "MyApp"
        assert entry.type == "app"
        assert entry.delay_ms == 900
        assert entry.args == []
        assert entry.shell is None

    def test_terminal_type_sets_shell_to_wt_pwsh(self) -> None:
        entry = LaunchEntry.from_dict({"name": "Terminal", "type": "terminal"})
        assert entry.shell == "wt-pwsh"

    def test_explicit_shell_overrides_terminal_default(self) -> None:
        entry = LaunchEntry.from_dict({"name": "T", "type": "terminal", "shell": "pwsh"})
        assert entry.shell == "pwsh"

    def test_from_dict_round_trip(self) -> None:
        original = LaunchEntry(
            name="Code",
            type="app",
            path=r"C:\code.exe",
            args=["--flag"],
            command=None,
            cwd=r"C:\workspace",
            delay_ms=500,
        )
        restored = LaunchEntry.from_dict(original.to_dict())
        assert restored.name == original.name
        assert restored.path == original.path
        assert restored.args == original.args
        assert restored.cwd == original.cwd
        assert restored.delay_ms == original.delay_ms

    def test_from_dict_with_placement(self) -> None:
        data = {
            "name": "App",
            "placement": {"screen": 1, "layout": {"mode": "full"}},
        }
        entry = LaunchEntry.from_dict(data)
        assert entry.placement is not None
        assert entry.placement.screen == 1

    def test_from_dict_non_dict_placement_ignored(self) -> None:
        entry = LaunchEntry.from_dict({"name": "App", "placement": None})
        assert entry.placement is None


# ---------------------------------------------------------------------------
# WindowSnapshot
# ---------------------------------------------------------------------------


class TestWindowSnapshot:
    def test_to_dict_minimal(self) -> None:
        ws = WindowSnapshot(title="My Window")
        d = ws.to_dict()
        assert d == {"title": "My Window"}

    def test_to_dict_with_all_fields(self) -> None:
        ws = WindowSnapshot(
            title="Window",
            pid=1234,
            process_name="app.exe",
            exe_path=r"C:\app.exe",
            class_name="AppClass",
            screen_number=0,
        )
        d = ws.to_dict()
        assert d["pid"] == 1234
        assert d["process_name"] == "app.exe"
        assert d["screen_number"] == 0

    def test_from_dict_minimal(self) -> None:
        ws = WindowSnapshot.from_dict({"title": "Win"})
        assert ws.title == "Win"
        assert ws.pid is None
        assert ws.bounds is None

    def test_from_dict_round_trip(self) -> None:
        original = WindowSnapshot(
            title="Test",
            pid=42,
            process_name="test.exe",
            screen_number=1,
        )
        restored = WindowSnapshot.from_dict(original.to_dict())
        assert restored.title == original.title
        assert restored.pid == original.pid
        assert restored.screen_number == original.screen_number

    def test_from_dict_with_bounds(self) -> None:
        data = {
            "title": "Win",
            "bounds": {"left": 0, "top": 0, "width": 800, "height": 600},
        }
        ws = WindowSnapshot.from_dict(data)
        assert ws.bounds is not None
        assert ws.bounds.width == 800


# ---------------------------------------------------------------------------
# DesktopPlan
# ---------------------------------------------------------------------------


class TestDesktopPlan:
    def test_to_dict_empty_plan(self) -> None:
        dp = DesktopPlan(name="Desktop 1")
        d = dp.to_dict()
        assert d["name"] == "Desktop 1"
        assert d["launch"] == []
        assert "captured_windows" not in d

    def test_to_dict_with_windows(self) -> None:
        dp = DesktopPlan(
            name="Work",
            captured_windows=[WindowSnapshot(title="Win")],
        )
        d = dp.to_dict()
        assert "captured_windows" in d
        assert len(d["captured_windows"]) == 1

    def test_from_dict_minimal(self) -> None:
        dp = DesktopPlan.from_dict({"name": "Dev"})
        assert dp.name == "Dev"
        assert dp.launch == []
        assert dp.captured_windows == []

    def test_from_dict_with_launch_entries(self) -> None:
        data = {
            "name": "Dev",
            "launch": [{"name": "Editor"}, {"name": "Terminal", "type": "terminal"}],
        }
        dp = DesktopPlan.from_dict(data)
        assert len(dp.launch) == 2
        assert dp.launch[1].shell == "wt-pwsh"

    def test_from_dict_ignores_non_list_launch(self) -> None:
        dp = DesktopPlan.from_dict({"name": "Test", "launch": "not a list"})
        assert dp.launch == []

    def test_round_trip(self) -> None:
        original = DesktopPlan(
            name="Work",
            launch=[LaunchEntry(name="Code", type="app")],
        )
        restored = DesktopPlan.from_dict(original.to_dict())
        assert restored.name == original.name
        assert len(restored.launch) == 1
        assert restored.launch[0].name == "Code"


# ---------------------------------------------------------------------------
# WorkspacePlan
# ---------------------------------------------------------------------------


class TestWorkspacePlan:
    def test_to_dict_defaults(self) -> None:
        wp = WorkspacePlan()
        d = wp.to_dict()
        assert d["version"] == 1
        assert d["switch_delay_ms"] == 650
        assert d["desktops"] == []
        assert "workspace" not in d
        assert "notes" not in d
        assert "aliases" not in d

    def test_to_dict_includes_workspace(self) -> None:
        wp = WorkspacePlan(workspace=r"C:\code")
        d = wp.to_dict()
        assert d["workspace"] == r"C:\code"

    def test_to_dict_personal_workspace_key(self) -> None:
        wp = WorkspacePlan(personal_workspace=r"C:\personal")
        d = wp.to_dict()
        assert "personal-workspace" in d
        assert d["personal-workspace"] == r"C:\personal"

    def test_from_dict_minimal(self) -> None:
        wp = WorkspacePlan.from_dict({})
        assert wp.version == 1
        assert wp.switch_delay_ms == 650
        assert wp.desktops == []

    def test_from_dict_with_desktops(self) -> None:
        data = {"desktops": [{"name": "Dev"}, {"name": "Comms"}]}
        wp = WorkspacePlan.from_dict(data)
        assert len(wp.desktops) == 2
        assert wp.desktops[0].name == "Dev"

    def test_round_trip(self) -> None:
        original = WorkspacePlan(
            workspace=r"C:\projects",
            switch_delay_ms=400,
            desktops=[DesktopPlan(name="Work")],
            notes=["note1"],
            aliases={"proj": r"C:\projects"},
        )
        restored = WorkspacePlan.from_dict(original.to_dict())
        assert restored.workspace == original.workspace
        assert restored.switch_delay_ms == original.switch_delay_ms
        assert len(restored.desktops) == 1
        assert restored.notes == ["note1"]
        assert restored.aliases == {"proj": r"C:\projects"}

    def test_substitutions_workspace_key(self) -> None:
        wp = WorkspacePlan(workspace=r"C:\code")
        subs = wp.substitutions()
        assert subs["workspace"] == r"C:\code"

    def test_substitutions_aliases_override(self) -> None:
        wp = WorkspacePlan(
            workspace=r"C:\code",
            aliases={"myalias": r"C:\other"},
        )
        subs = wp.substitutions()
        assert subs["myalias"] == r"C:\other"
        assert subs["workspace"] == r"C:\code"

    def test_substitutions_placeholder_resolution(self) -> None:
        wp = WorkspacePlan(
            workspace=r"C:\code",
            aliases={"vscode_ws": r"{workspace}\project.code-workspace"},
        )
        subs = wp.substitutions()
        assert subs["vscode_ws"] == r"C:\code\project.code-workspace"

    def test_substitutions_empty_plan(self, monkeypatch: pytest.MonkeyPatch) -> None:
        for variable in ("LOCALAPPDATA", "APPDATA", "USERPROFILE", "ProgramFiles"):
            monkeypatch.delenv(variable, raising=False)
        wp = WorkspacePlan()
        assert wp.substitutions() == {}

    def test_substitutions_personal_workspace_key(self) -> None:
        wp = WorkspacePlan(personal_workspace=r"C:\personal")
        subs = wp.substitutions()
        assert subs["personal-workspace"] == r"C:\personal"

    def test_substitutions_circular_placeholder_does_not_hang(self) -> None:
        wp = WorkspacePlan(aliases={"a": "{b}", "b": "{a}"})
        subs = wp.substitutions()
        # Should not raise or hang; just return partially resolved values
        assert "a" in subs
        assert "b" in subs


class TestHandEditedPlanTolerance:
    def test_numbers_where_text_belongs_read_as_text(self) -> None:
        plan = WorkspacePlan.from_dict(
            {
                "notes": [1],
                "aliases": {"n": 2},
                "desktops": [{"name": 3, "launch": [{"name": "x", "args": [123], "cwd": 4}]}],
            }
        )
        entry = plan.desktops[0].launch[0]
        assert (plan.notes, plan.aliases, plan.desktops[0].name) == (["1"], {"n": "2"}, "3")
        assert (entry.args, entry.cwd) == (["123"], "4")

    def test_unreadable_optional_values_are_absent(self) -> None:
        window = WindowSnapshot.from_dict({"title": "t", "pid": "unknown", "inferred_placement": "left"})
        assert (window.pid, window.inferred_placement) == (None, None)
        entry = LaunchEntry.from_dict({"name": "x", "window_wait_timeout_ms": [], "placement": 5})
        assert (entry.window_wait_timeout_ms, entry.placement) == (None, None)

    def test_non_object_maps_read_as_empty(self) -> None:
        plan = WorkspacePlan.from_dict({"aliases": "nope", "features": ["a"]})
        assert (plan.aliases, plan.features) == ({}, {})

    def test_a_plan_file_cannot_set_runtime_only_retired_names(self) -> None:
        assert WorkspacePlan.from_dict({"retired_desktop_names": ["Personal"]}).retired_desktop_names == []
