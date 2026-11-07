import json
from pathlib import Path

import pytest

from desktop_manager.models import DesktopPlan, LaunchEntry, WorkspacePlan
from desktop_manager.plan import (
    UnknownFeatureError,
    apply_feature_flags,
    load_plan_context,
    resolve_feature_flags,
)

pytestmark = pytest.mark.ci


def _plan() -> WorkspacePlan:
    return WorkspacePlan(
        features={"wt-terminals": True, "warp-terminals": False},
        desktops=[
            DesktopPlan(
                name="Work",
                launch=[
                    LaunchEntry(name="WT 1", feature="wt-terminals"),
                    LaunchEntry(name="Warp 1", feature="warp-terminals"),
                    LaunchEntry(name="Fork"),
                    LaunchEntry(name="Retired", feature="wt-terminals", disabled=True),
                ],
            )
        ],
    )


def _disabled_names(plan: WorkspacePlan) -> set[str]:
    return {entry.name for desktop in plan.desktops for entry in desktop.launch if entry.disabled}


def test_defaults_disable_entries_behind_an_off_feature():
    applied = apply_feature_flags(_plan())

    assert _disabled_names(applied) == {"Warp 1", "Retired"}


def test_enable_flag_revives_a_paused_feature():
    applied = apply_feature_flags(_plan(), enable=["warp-terminals"], disable=["wt-terminals"])

    assert _disabled_names(applied) == {"WT 1", "Retired"}
    assert applied.features == {"wt-terminals": False, "warp-terminals": True}


def test_entries_without_a_feature_are_never_touched():
    applied = apply_feature_flags(_plan(), disable=["wt-terminals", "warp-terminals"])

    assert "Fork" not in _disabled_names(applied)


def test_explicitly_disabled_entry_stays_disabled_when_its_feature_is_on():
    applied = apply_feature_flags(_plan(), enable=["wt-terminals"])

    assert "Retired" in _disabled_names(applied)


def test_applying_flags_leaves_the_source_plan_unchanged():
    plan = _plan()

    apply_feature_flags(plan, disable=["wt-terminals"])

    assert _disabled_names(plan) == {"Retired"}


def test_unknown_feature_name_is_rejected():
    with pytest.raises(UnknownFeatureError, match="wrap-terminals"):
        resolve_feature_flags(_plan(), enable=["wrap-terminals"])


def test_feature_enabled_and_disabled_at_once_is_rejected():
    with pytest.raises(UnknownFeatureError, match="both enabled and disabled"):
        resolve_feature_flags(_plan(), enable=["wt-terminals"], disable=["wt-terminals"])


def test_plan_without_features_is_returned_untouched():
    plan = WorkspacePlan(desktops=[DesktopPlan(name="Work", launch=[LaunchEntry(name="Fork")])])

    assert apply_feature_flags(plan) is plan


def test_features_round_trip_through_plan_json(tmp_path: Path):
    config = tmp_path / "plan.json"
    config.write_text(
        json.dumps(
            {
                "features": {"wt-terminals": True, "warp-terminals": "off"},
                "desktops": [
                    {
                        "name": "Work",
                        "launch": [
                            {"name": "WT 1", "type": "app", "path": "wt.exe", "feature": "wt-terminals"},
                            {"name": "Warp 1", "type": "app", "path": "warp.exe", "feature": "warp-terminals"},
                        ],
                    }
                ],
            }
        ),
        encoding="utf-8",
    )

    plan = load_plan_context(config)

    assert (plan.features["wt-terminals"], plan.features["warp-terminals"]) == (True, False)
    assert _disabled_names(plan) == {"Warp 1"}


def test_entry_feature_survives_serialization():
    entry = LaunchEntry(name="WT 1", feature="wt-terminals")

    assert LaunchEntry.from_dict(entry.to_dict()).feature == "wt-terminals"


def _desktop_plan() -> WorkspacePlan:
    return WorkspacePlan(
        features={"personal-desktop": False},
        desktops=[
            DesktopPlan(name="Work", launch=[LaunchEntry(name="Fork")]),
            DesktopPlan(
                name="Personal",
                feature="personal-desktop",
                launch=[LaunchEntry(name="Chrome Personal")],
            ),
            DesktopPlan(name="Admin", launch=[LaunchEntry(name="Slack")]),
        ],
    )


def test_desktop_behind_an_off_feature_is_dropped_not_emptied():
    applied = apply_feature_flags(_desktop_plan())

    # Dropped rather than emptied: the desktop count is len(desktops), so a husk
    # would still create the virtual desktop and push Admin into third place.
    assert [desktop.name for desktop in applied.desktops] == ["Work", "Admin"]


def test_enabling_a_desktop_feature_restores_it_in_place():
    applied = apply_feature_flags(_desktop_plan(), enable=["personal-desktop"])

    assert [desktop.name for desktop in applied.desktops] == ["Work", "Personal", "Admin"]
    assert applied.retired_desktop_names == []


def test_dropped_desktop_names_are_kept_for_restore_to_retire():
    applied = apply_feature_flags(_desktop_plan())

    # Restore removes a leftover virtual desktop of this name, so switching the
    # feature off retires the desktop rather than leaving it unmanaged.
    assert applied.retired_desktop_names == ["Personal"]
    assert "retired_desktop_names" not in applied.to_dict()


def test_desktop_disabled_in_the_plan_is_dropped_whatever_its_feature_says():
    plan = WorkspacePlan(
        features={"personal-desktop": True},
        desktops=[
            DesktopPlan(name="Work"),
            DesktopPlan(name="Personal", feature="personal-desktop", disabled=True),
        ],
    )

    applied = apply_feature_flags(plan, enable=["personal-desktop"])

    assert [desktop.name for desktop in applied.desktops] == ["Work"]


def test_dropping_a_desktop_leaves_the_source_plan_unchanged():
    plan = _desktop_plan()

    apply_feature_flags(plan)

    assert [desktop.name for desktop in plan.desktops] == ["Work", "Personal", "Admin"]


def test_desktop_feature_survives_serialization():
    desktop = DesktopPlan(name="Personal", feature="personal-desktop", disabled=True)

    restored = DesktopPlan.from_dict(desktop.to_dict())

    assert restored.feature == "personal-desktop"
    assert restored.disabled is True


def test_bundle_desktop_ref_carries_the_feature(tmp_path: Path):
    (tmp_path / "bundles").mkdir()
    (tmp_path / "bundles" / "personal.json").write_text(
        json.dumps({"name": "Personal", "launch": []}), encoding="utf-8"
    )
    (tmp_path / "bundles" / "work.json").write_text(json.dumps({"name": "Work", "launch": []}), encoding="utf-8")
    config = tmp_path / "plan.bundle.json"
    config.write_text(
        json.dumps(
            {
                "features": {"personal-desktop": False},
                "bundle": {
                    "desktops": [
                        {"path": "bundles/work.json"},
                        {"path": "bundles/personal.json", "feature": "personal-desktop"},
                    ]
                },
            }
        ),
        encoding="utf-8",
    )

    assert [desktop.name for desktop in load_plan_context(config).desktops] == ["Work"]
    assert [desktop.name for desktop in load_plan_context(config, enable_features=["personal-desktop"]).desktops] == [
        "Work",
        "Personal",
    ]


def test_bundle_round_trips_a_desktop_feature(tmp_path: Path):
    from desktop_manager.plan import save_plan_bundle

    config = tmp_path / "plan.bundle.json"
    save_plan_bundle(
        WorkspacePlan(
            features={"personal-desktop": True},
            desktops=[
                DesktopPlan(name="Work"),
                DesktopPlan(name="Personal", feature="personal-desktop"),
            ],
        ),
        config,
    )

    refs = json.loads(config.read_text(encoding="utf-8"))["bundle"]["desktops"]

    assert refs[1]["feature"] == "personal-desktop"
    assert [desktop.feature for desktop in load_plan_context(config).desktops] == [None, "personal-desktop"]
