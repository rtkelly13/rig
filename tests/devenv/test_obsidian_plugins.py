"""Obsidian plugin installation — registry resolution and enablement.

Network calls are not exercised; these cover the pure logic that decides what to
install and the file that records it.
"""

from __future__ import annotations

import json
from pathlib import Path
from unittest.mock import patch

import pytest

from rig.devenv.steps.ai_tools.obsidian_plugins import (
    PluginSpec,
    enable_core_plugins,
    enable_plugin,
    install_plugin,
    installed_version,
    parse_registry,
    resolve_plugin,
)

REGISTRY = parse_registry(
    [
        {
            "id": "obsidian-local-rest-api",
            "repo": "coddingtonbear/obsidian-local-rest-api",
            "name": "Local REST API with MCP",
        },
        {"id": "dataview", "repo": "blacksmithgu/obsidian-dataview", "name": "Dataview"},
    ]
)


def test_resolve_plugin_maps_id_to_repo():
    spec = resolve_plugin("obsidian-local-rest-api", REGISTRY)

    assert spec == PluginSpec(
        id="obsidian-local-rest-api",
        repo="coddingtonbear/obsidian-local-rest-api",
        name="Local REST API with MCP",
    )


def test_resolve_plugin_returns_none_when_unknown():
    assert resolve_plugin("not-a-real-plugin", REGISTRY) is None


def test_installed_version_reads_the_manifest(tmp_path: Path):
    d = tmp_path / ".obsidian" / "plugins" / "dataview"
    d.mkdir(parents=True)
    (d / "manifest.json").write_text(json.dumps({"version": "0.5.67"}))

    assert installed_version(tmp_path, "dataview") == "0.5.67"
    assert installed_version(tmp_path, "absent") is None


def test_enable_plugin_preserves_plugins_already_enabled(tmp_path: Path):
    """The file is a user's list; enabling one plugin must not drop the others."""
    cfg = tmp_path / ".obsidian" / "community-plugins.json"
    cfg.parent.mkdir(parents=True)
    cfg.write_text(json.dumps(["dataview", "templater-obsidian"]))

    changed = enable_plugin(tmp_path, "obsidian-local-rest-api")

    assert changed is True
    assert json.loads(cfg.read_text()) == ["dataview", "templater-obsidian", "obsidian-local-rest-api"]


def test_enable_plugin_is_idempotent(tmp_path: Path):
    cfg = tmp_path / ".obsidian" / "community-plugins.json"
    cfg.parent.mkdir(parents=True)
    cfg.write_text(json.dumps(["dataview"]))

    assert enable_plugin(tmp_path, "dataview") is False
    assert json.loads(cfg.read_text()) == ["dataview"]


def test_enable_plugin_survives_a_corrupt_config(tmp_path: Path):
    """A malformed file must not take the whole setup step down."""
    cfg = tmp_path / ".obsidian" / "community-plugins.json"
    cfg.parent.mkdir(parents=True)
    cfg.write_text("{not json")

    assert enable_plugin(tmp_path, "dataview") is True
    assert json.loads(cfg.read_text()) == ["dataview"]


def test_enable_core_plugins_flips_the_mapping(tmp_path: Path):
    cfg = tmp_path / ".obsidian" / "core-plugins.json"
    cfg.parent.mkdir(parents=True)
    cfg.write_text(json.dumps({"properties": False, "graph": True}))

    turned_on = enable_core_plugins(tmp_path, ("properties",))

    assert turned_on == ["properties"]
    data = json.loads(cfg.read_text())
    assert data["properties"] is True
    assert data["graph"] is True  # untouched


def test_enable_core_plugins_handles_the_legacy_list_shape(tmp_path: Path):
    """Older vaults store a list of enabled ids; the shape must be preserved."""
    cfg = tmp_path / ".obsidian" / "core-plugins.json"
    cfg.parent.mkdir(parents=True)
    cfg.write_text(json.dumps(["graph", "backlink"]))

    turned_on = enable_core_plugins(tmp_path, ("properties",))

    assert turned_on == ["properties"]
    data = json.loads(cfg.read_text())
    assert isinstance(data, list)
    assert data == ["graph", "backlink", "properties"]


def test_enable_core_plugins_is_idempotent(tmp_path: Path):
    cfg = tmp_path / ".obsidian" / "core-plugins.json"
    cfg.parent.mkdir(parents=True)
    cfg.write_text(json.dumps({"properties": True}))

    assert enable_core_plugins(tmp_path, ("properties",)) == []


def test_enable_core_plugins_noop_without_the_file(tmp_path: Path):
    (tmp_path / ".obsidian").mkdir(parents=True)

    assert enable_core_plugins(tmp_path, ("properties",)) == []


def test_plugin_urls_must_be_https_from_a_known_host():
    """Registry and release URLs are remote data; `urlopen` honours file:// too.

    A hostile or corrupted registry entry must not be able to make the installer
    read a local path or fetch from an arbitrary host.
    """
    from rig.devenv.steps.ai_tools.obsidian_plugins import _check_url

    ok = "https://raw.githubusercontent.com/obsidianmd/obsidian-releases/master/community-plugins.json"
    assert _check_url(ok) == ok

    for bad in (
        "file:///etc/passwd",
        "http://github.com/x/y",
        "https://evil.example.com/main.js",
    ):
        with pytest.raises(ValueError):
            _check_url(bad)


def test_registry_skips_misshapen_entries_without_hiding_the_rest():
    registry = parse_registry(
        [
            {"id": "no-repo", "name": "No Repo"},
            "not-an-object",
            {"id": "dataview", "repo": "blacksmithgu/obsidian-dataview", "name": "Dataview"},
        ]
    )

    assert [spec.id for spec in registry] == ["dataview"]
    assert resolve_plugin("no-repo", registry) is None


def test_registry_that_is_not_a_list_is_an_error():
    with pytest.raises(ValueError):
        parse_registry({"plugins": []})


def test_installed_version_is_none_for_a_manifest_without_a_version(tmp_path: Path):
    d = tmp_path / ".obsidian" / "plugins" / "dataview"
    d.mkdir(parents=True)
    (d / "manifest.json").write_text(json.dumps({"id": "dataview"}))

    assert installed_version(tmp_path, "dataview") is None


@pytest.mark.parametrize(
    "release",
    [
        pytest.param({"assets": []}, id="no-tag"),
        pytest.param({"tag_name": "1.0.0"}, id="no-assets"),
        pytest.param({"tag_name": "1.0.0", "assets": [{"name": "main.js"}]}, id="asset-without-url"),
        pytest.param([], id="not-an-object"),
    ],
)
def test_install_plugin_skips_a_misshapen_release(tmp_path: Path, release: object):
    """A release the API answers in an unexpected shape is reported and skipped, not half-installed."""
    spec = PluginSpec(id="dataview", repo="blacksmithgu/obsidian-dataview", name="Dataview")
    with patch(
        "rig.devenv.steps.ai_tools.obsidian_plugins._get",
        return_value=json.dumps(release).encode(),
    ):
        assert install_plugin(tmp_path, spec) is None
    assert not (tmp_path / ".obsidian" / "plugins" / "dataview").exists()
