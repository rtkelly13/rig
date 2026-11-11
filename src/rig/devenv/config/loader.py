"""The devenv user config: ``~/.config/devenv/config.json``. ``rig devenv tools`` reads its ``custom_catalogs``."""

from __future__ import annotations

import json
from pathlib import Path

from rig.typed_json import load_json

from .models import DevenvConfig

CONFIG_PATH = Path.home() / ".config" / "devenv" / "config.json"


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
