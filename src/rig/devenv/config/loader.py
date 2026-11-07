"""Catalog and configuration loader helpers."""

from __future__ import annotations

import json
from dataclasses import dataclass
from pathlib import Path

from rig.typed_json import decode, load_json, try_decode

from .models import DevenvConfig, ToolDefinition

TOOLS_DIR = Path(__file__).parent / "tools"
USER_TOOLS_DIR = Path.home() / ".config" / "devenv" / "tools"
CONFIG_PATH = Path.home() / ".config" / "devenv" / "config.json"


@dataclass(slots=True, frozen=True)
class _ToolCatalog:
    tools: list[ToolDefinition]


def load_tools_from_file(path: Path) -> list[ToolDefinition]:
    """Parse tool definitions from a JSON file: a bare list, or an object with a ``tools`` list.

    A misshapen catalog raises ``pydantic.ValidationError`` rather than quietly contributing no tools.
    """
    if not path.is_file():
        return []
    with path.open(encoding="utf-8") as f:
        data = load_json(f.read())
    if try_decode(list[object], data) is not None:
        return decode(list[ToolDefinition], data)
    return decode(_ToolCatalog, data).tools


def load_all_builtin_tools() -> list[ToolDefinition]:
    """Load all tool definitions from builtin catalogs, user drop directories, and custom paths."""
    tools: list[ToolDefinition] = []
    seen_ids: set[str] = set()

    candidate_files: list[Path] = []

    # 1. Built-in bundled catalogs
    if TOOLS_DIR.is_dir():
        candidate_files.extend(sorted(TOOLS_DIR.glob("*.json")))

    # 2. User drop directory (~/.config/devenv/tools/*.json)
    if USER_TOOLS_DIR.is_dir():
        candidate_files.extend(sorted(USER_TOOLS_DIR.glob("*.json")))

    # 3. Local workspace drop directory (./.devenv/tools/*.json)
    workspace_tools = Path.cwd() / ".devenv" / "tools"
    if workspace_tools.is_dir():
        candidate_files.extend(sorted(workspace_tools.glob("*.json")))

    # 4. Explicit custom catalogs from config.json
    cfg = load_user_config()
    for custom_path_str in cfg.custom_catalogs:
        custom_path = Path(custom_path_str).expanduser()
        if custom_path.is_file():
            candidate_files.append(custom_path)

    for file in candidate_files:
        for tool in load_tools_from_file(file):
            if tool.id not in seen_ids:
                tools.append(tool)
                seen_ids.add(tool.id)

    return tools


def load_user_config() -> DevenvConfig:
    """Load user configuration from ~/.config/devenv/config.json."""
    if not CONFIG_PATH.is_file():
        return DevenvConfig()
    try:
        with CONFIG_PATH.open(encoding="utf-8") as f:
            return DevenvConfig.model_validate(load_json(f.read()))
    except (OSError, ValueError):
        return DevenvConfig()


def save_user_config(config: DevenvConfig) -> None:
    """Save user configuration to ~/.config/devenv/config.json."""
    CONFIG_PATH.parent.mkdir(parents=True, exist_ok=True)
    with CONFIG_PATH.open("w", encoding="utf-8") as f:
        json.dump(config.model_dump(mode="json"), f, indent=2)
        f.write("\n")
