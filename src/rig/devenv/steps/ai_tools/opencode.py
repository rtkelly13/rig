"""OpenCode default user config setup."""

from __future__ import annotations

import json
from copy import deepcopy
from pathlib import Path

from rich.console import Console

from rig.typed_json import JsonObject, JsonValue, load_json, try_decode

from .policy_permissions import project_policy_permissions

console = Console()

OPENCODE_DEFAULT_CONFIG: JsonObject = {
    "$schema": "https://opencode.ai/config.json",
    "agent": {
        "explore": {"model": "github-copilot/gemini-3-flash-preview"},
        "general": {"model": "github-copilot/claude-sonnet-4.6"},
    },
}


def get_opencode_config_path() -> Path:
    """Return OpenCode user configuration path."""
    return Path.home() / ".config" / "opencode" / "config.json"


def merge_dicts_non_destructive(target: JsonObject, defaults: JsonObject) -> None:
    """Recursively merge defaults without overwriting existing user keys."""
    for key, val in defaults.items():
        if key not in target:
            target[key] = deepcopy(val)
            continue
        current = try_decode(dict[str, JsonValue], target[key])
        nested_defaults = try_decode(dict[str, JsonValue], val)
        if current is not None and nested_defaults is not None:
            merge_dicts_non_destructive(current, nested_defaults)
            target[key] = current


def setup_opencode(dry_run: bool = False) -> None:
    """Set up OpenCode user configuration, then project the estate command policy into it."""
    _merge_default_config(dry_run)
    project_policy_permissions("opencode", dry_run=dry_run)


def _merge_default_config(dry_run: bool) -> None:
    path = get_opencode_config_path()
    existing: JsonObject = {}
    if path.is_file():
        try:
            with path.open(encoding="utf-8") as f:
                existing = try_decode(dict[str, JsonValue], load_json(f.read())) or {}
        except (OSError, ValueError):
            existing = {}

    merged = deepcopy(existing)
    merge_dicts_non_destructive(merged, OPENCODE_DEFAULT_CONFIG)

    if merged == existing:
        console.print(f"[green]✓ OpenCode config already configured:[/green] {path}")
        return

    if dry_run:
        console.print(f"[dim]Dry run: would merge defaults into {path}[/dim]")
        return

    try:
        path.parent.mkdir(parents=True, exist_ok=True)
        with path.open("w", encoding="utf-8") as f:
            json.dump(merged, f, indent=2)
            f.write("\n")
        console.print(f"[bold green]✓ Configured OpenCode default settings at {path}[/bold green]")
    except (OSError, TypeError, ValueError) as e:
        console.print(f"[yellow]! Failed to write OpenCode config: {e}[/yellow]")
