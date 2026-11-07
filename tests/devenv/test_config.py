"""Tests for configuration models and catalog loaders."""

import json
from pathlib import Path

import pytest

from rig.devenv.config.loader import (
    load_all_builtin_tools,
    load_tools_from_file,
    load_user_config,
    save_user_config,
)
from rig.devenv.config.models import (
    DevenvConfig,
    GitIdentity,
    ToolDefinition,
    ToolInstallSpec,
)


@pytest.mark.ci
def test_tool_definition_model():
    spec = ToolInstallSpec(winget="Test.Tool", brew="test-tool", check_cmd="test")
    tool = ToolDefinition(
        id="test",
        name="Test Tool",
        description="A test tool",
        category="core",
        install=spec,
        required=True,
    )
    assert tool.id == "test"
    assert tool.install.winget == "Test.Tool"
    assert tool.install.brew == "test-tool"
    assert tool.required


@pytest.mark.ci
def test_load_builtin_tools():
    tools = load_all_builtin_tools()
    assert len(tools) > 0
    ids = {t.id for t in tools}
    assert "git" in ids
    assert "uv" in ids
    assert "antigravity" in ids
    assert "claude-code" in ids


@pytest.mark.ci
def test_load_tools_from_file(tmp_path: Path):
    catalog_file = tmp_path / "custom.json"
    catalog_file.write_text(
        json.dumps(
            [
                {
                    "id": "my-tool",
                    "name": "My Tool",
                    "description": "Custom utility",
                    "category": "utilities",
                    "install": {"check_cmd": "my-tool"},
                }
            ]
        ),
        encoding="utf-8",
    )

    tools = load_tools_from_file(catalog_file)
    assert len(tools) == 1
    assert tools[0].id == "my-tool"
    assert tools[0].name == "My Tool"


_TOOL = {"id": "my-tool", "name": "My Tool", "description": "Custom utility", "category": "utilities"}


@pytest.mark.ci
def test_load_tools_from_a_catalog_object(tmp_path: Path):
    catalog_file = tmp_path / "custom.json"
    catalog_file.write_text(json.dumps({"tools": [_TOOL]}), encoding="utf-8")

    assert [tool.id for tool in load_tools_from_file(catalog_file)] == ["my-tool"]


@pytest.mark.ci
@pytest.mark.parametrize(
    "catalog",
    [
        pytest.param([_TOOL, "not-a-tool"], id="entry-not-an-object"),
        pytest.param({"catalog": [_TOOL]}, id="object-without-tools"),
        pytest.param("my-tool", id="neither-list-nor-object"),
    ],
)
def test_load_tools_rejects_a_misshapen_catalog(tmp_path: Path, catalog: object):
    """A malformed catalog fails loudly rather than quietly contributing fewer tools."""
    catalog_file = tmp_path / "custom.json"
    catalog_file.write_text(json.dumps(catalog), encoding="utf-8")

    with pytest.raises(ValueError):
        load_tools_from_file(catalog_file)


@pytest.mark.ci
def test_load_and_save_user_config(tmp_path: Path, monkeypatch: pytest.MonkeyPatch):
    fake_config_path = tmp_path / "devenv" / "config.json"
    monkeypatch.setattr("rig.devenv.config.loader.CONFIG_PATH", fake_config_path)

    # Missing file returns default config
    cfg = load_user_config()
    assert isinstance(cfg, DevenvConfig)
    assert len(cfg.identities) == 0

    # Save and reload
    cfg.custom_catalogs = ["~/catalogs/extra.json"]
    cfg.identities.append(
        GitIdentity(
            id="personal",
            name="Developer",
            email="dev@example.com",
            is_default=True,
        )
    )
    save_user_config(cfg)

    reloaded = load_user_config()
    assert reloaded.custom_catalogs == ["~/catalogs/extra.json"]
    assert len(reloaded.identities) == 1
    assert reloaded.identities[0].email == "dev@example.com"
