"""How a bundle plan's refs override the desktop files they point at."""

from __future__ import annotations

import json
from pathlib import Path

import pytest

from desktop_manager.plan import parse_plan_json

pytestmark = pytest.mark.ci


def _bundle(tmp_path: Path, root: dict[str, object], desktops: dict[str, object]) -> str:
    folder = tmp_path / "desktops"
    folder.mkdir(exist_ok=True)
    for name, content in desktops.items():
        (folder / name).write_text(json.dumps(content), encoding="utf-8")
    return json.dumps(root)


def test_a_ref_overrides_the_files_name_feature_and_disabled(tmp_path: Path) -> None:
    text = _bundle(
        tmp_path,
        {"bundle": {"desktops": [{"path": "desktops/a.json", "name": 7, "feature": 3, "disabled": "no"}]}},
        {"a.json": {"name": "File", "feature": "file-feature", "disabled": False}},
    )

    desktop = parse_plan_json(text, base_dir=tmp_path).desktops[0]

    assert (desktop.name, desktop.feature, desktop.disabled) == ("7", "3", True)


def test_without_overrides_the_file_keeps_its_own_values(tmp_path: Path) -> None:
    text = _bundle(
        tmp_path,
        {"bundle": {"desktops": [{"path": "desktops/a.json"}]}},
        {"a.json": {"name": "File", "feature": "f", "disabled": True}},
    )

    desktop = parse_plan_json(text, base_dir=tmp_path).desktops[0]

    assert (desktop.name, desktop.feature, desktop.disabled) == ("File", "f", True)


def test_a_ref_name_stands_in_for_a_file_without_one(tmp_path: Path) -> None:
    text = _bundle(
        tmp_path,
        {"bundle": {"desktops": [{"path": "desktops/a.json", "name": "From Ref"}]}},
        {"a.json": {"launch": []}},
    )

    assert parse_plan_json(text, base_dir=tmp_path).desktops[0].name == "From Ref"


def test_a_desktop_named_nowhere_is_an_error(tmp_path: Path) -> None:
    text = _bundle(tmp_path, {"bundle": {"desktops": [{"path": "desktops/a.json"}]}}, {"a.json": {}})

    with pytest.raises(ValueError):
        parse_plan_json(text, base_dir=tmp_path)


def test_bundle_aliases_win_over_root_aliases_and_every_value_reads_as_text(tmp_path: Path) -> None:
    text = _bundle(
        tmp_path,
        {
            "aliases": {"a": "root", "b": None},
            "bundle": {"aliases": {"a": "bundle", "c": 3}, "desktops": [{"path": "desktops/a.json"}]},
        },
        {"a.json": {"name": "A"}},
    )

    assert parse_plan_json(text, base_dir=tmp_path).aliases == {"a": "bundle", "b": "None", "c": "3"}


def test_root_settings_are_kept(tmp_path: Path) -> None:
    text = _bundle(
        tmp_path,
        {
            "version": 1,
            "workspace": "C:/ws",
            "personal-workspace": "C:/personal",
            "switch_delay_ms": 10,
            "notes": ["n"],
            "features": {"lab": "off"},
            "bundle": {"desktops": [{"path": "desktops/a.json"}]},
        },
        {"a.json": {"name": "A"}},
    )

    plan = parse_plan_json(text, base_dir=tmp_path)

    assert (plan.workspace, plan.personal_workspace, plan.switch_delay_ms) == ("C:/ws", "C:/personal", 10)
    assert (plan.notes, plan.features) == (["n"], {"lab": False})


def test_root_desktops_are_used_when_the_bundle_lists_none(tmp_path: Path) -> None:
    text = json.dumps({"desktops": [{"name": "Inline"}], "bundle": {"desktops": []}})

    assert [desktop.name for desktop in parse_plan_json(text).desktops] == ["Inline"]


def test_root_desktops_are_ignored_when_the_bundle_lists_some(tmp_path: Path) -> None:
    text = _bundle(
        tmp_path,
        {"desktops": "not a list", "bundle": {"desktops": [{"path": "desktops/a.json"}]}},
        {"a.json": {"name": "A"}},
    )

    assert [desktop.name for desktop in parse_plan_json(text, base_dir=tmp_path).desktops] == ["A"]


@pytest.mark.parametrize(
    ("root", "message"),
    [
        ({"bundle": "nope"}, "JSON objects"),
        ({"bundle": {"aliases": []}}, "JSON objects"),
        ({"bundle": {"desktops": "nope"}}, "must be a list"),
        ({"bundle": {"desktops": ["nope"]}}, "JSON objects"),
        ({"bundle": {"desktops": [{}]}}, "relative path"),
        ({"bundle": {"desktops": [{"path": "  "}]}}, "relative path"),
    ],
)
def test_malformed_bundles_say_what_is_wrong(root: dict[str, object], message: str) -> None:
    with pytest.raises(ValueError, match=message):
        parse_plan_json(json.dumps(root), base_dir=Path())


def test_a_desktop_file_that_is_not_an_object_is_an_error(tmp_path: Path) -> None:
    text = _bundle(tmp_path, {"bundle": {"desktops": [{"path": "desktops/a.json"}]}}, {"a.json": [1]})

    with pytest.raises(ValueError, match="JSON objects"):
        parse_plan_json(text, base_dir=tmp_path)


def test_a_plan_that_is_not_an_object_is_an_error() -> None:
    with pytest.raises(ValueError, match="JSON objects"):
        parse_plan_json("[]")
