"""Claude Code settings baseline configuration (union/never-clobber).

Also installs the session-ledger ``SessionEnd`` hook (see :mod:`.session_ledger`).
Command permissions are not baselined here: they come from the estate policy
(``config/cli-allowlists.json``) through the same projection ``workspace policy sync`` uses.
"""

from __future__ import annotations

import copy
import json
import os
from dataclasses import dataclass
from pathlib import Path
from typing import Annotated

from pydantic import Field
from rich.console import Console

from rig.typed_json import Lenient, decode, load_json, try_decode, try_decode_json

from .policy_permissions import project_policy_permissions
from .session_ledger import (
    hook_script_current,
    install_hook_script,
    merge_session_ledger_hook,
    session_ledger_command,
    session_ledger_hook_path,
)

console = Console()

BASELINE_PREFERENCES: dict[str, object] = {
    "model": "opus-5.5",
    "effortLevel": "medium",
    "autoCompactEnabled": True,
    "autoCompactWindow": 200000,
    "skipWorkflowUsageWarning": True,
}

BASELINE_PERMISSION_PREFERENCES: dict[str, object] = {
    "defaultMode": "acceptEdits",
}

BASELINE_ATTRIBUTION: dict[str, object] = {
    "commit": "",
    "pr": "",
    "sessionUrl": False,
}

# MCP servers that need no credentials and no per-machine paths. Servers requiring
# an API key or a vault path are registered per-machine via `claude mcp add`, not
# baselined here — a baseline is only for what is safe on every machine.
BASELINE_MCP_SERVERS: dict[str, object] = {
    "chrome-devtools": {
        "command": "npx",
        "args": ["-y", "chrome-devtools-mcp@latest"],
    },
}


def get_claude_settings_path(home: Path | None = None) -> Path:
    """Return ~/.claude/settings.json path."""
    return (home or Path.home()) / ".claude" / "settings.json"


def load_settings(path: Path) -> dict[str, object]:
    """Load settings from JSON file or return empty dict."""
    if not path.is_file():
        return {}
    try:
        with path.open(encoding="utf-8") as f:
            data = load_json(f.read())
            return try_decode(dict[str, object], data) or {}
    except (OSError, ValueError):
        return {}


@dataclass(slots=True, frozen=True)
class SettingsMerge:
    """Settings with the baseline merged in, and a line per addition."""

    settings: dict[str, object]
    changes: list[str]


def settings_unreadable(path: Path) -> bool:
    """True when the file exists but is not a JSON object, so writing it would lose content."""
    if not path.is_file():
        return False
    try:
        return try_decode_json(dict[str, object], path.read_bytes()) is None
    except OSError:
        return True


@dataclass(slots=True, frozen=True)
class _SettingsView:
    """The parts of a Claude settings file the merge reads; a wrong-typed value reads as absent."""

    permissions: Lenient[dict[str, object]] = None
    mcp_servers: Annotated[Lenient[dict[str, object]], Field(alias="mcpServers")] = None
    attribution: Lenient[dict[str, object]] = None


def merge_claude_settings(existing: dict[str, object], session_end_command: str | None = None) -> SettingsMerge:
    """Union-merge baseline into existing settings without clobbering.

    ``session_end_command`` is the session-ledger hook command; it defaults to the
    script at :func:`session_ledger_hook_path`.
    """
    changes: list[str] = []
    merged = copy.deepcopy(existing)

    # permissions.allow/deny belong to the policy projection; only the mode is baselined here.
    view = decode(_SettingsView, merged)
    permissions = view.permissions or {}
    merged["permissions"] = permissions

    for key, val in BASELINE_PERMISSION_PREFERENCES.items():
        if key not in permissions:
            permissions[key] = val
            changes.append(f"+ permissions.{key}: {val!r}")

    for key, val in BASELINE_PREFERENCES.items():
        if key not in merged:
            merged[key] = val
            changes.append(f"+ {key}: {val!r}")

    # attribution — clear automated footers/trailers while preserving custom text
    attribution = view.attribution or {}
    if BASELINE_ATTRIBUTION or attribution:
        merged["attribution"] = attribution
    for key, val in BASELINE_ATTRIBUTION.items():
        if key not in attribution:
            attribution[key] = val
            changes.append(f"+ attribution.{key}: {val!r}")

    # mcpServers — union by server name. An existing server is never modified, even
    # if its definition differs from the baseline: ADR 0015 exists because clobbering
    # a hand-configured server destroys credentials that cannot be regenerated here.
    servers = view.mcp_servers or {}
    if BASELINE_MCP_SERVERS or servers:
        merged["mcpServers"] = servers
    for name, config in BASELINE_MCP_SERVERS.items():
        if name not in servers:
            servers[name] = copy.deepcopy(config)
            changes.append(f"+ mcpServers.{name}")

    command = session_end_command or session_ledger_command(session_ledger_hook_path())
    changes.extend(merge_session_ledger_hook(merged, command))

    return SettingsMerge(merged, changes)


def save_claude_settings(path: Path, data: dict[str, object]) -> bool:
    """Save merged settings to disk, replacing the file in one step so a failed write never truncates it."""
    staging = path.with_name(f"{path.name}.{os.getpid()}.tmp")
    try:
        path.parent.mkdir(parents=True, exist_ok=True)
        with staging.open("w", encoding="utf-8") as f:
            json.dump(data, f, indent=2, ensure_ascii=False)
            f.write("\n")
        staging.replace(path)
        return True
    except (OSError, TypeError, ValueError) as e:
        staging.unlink(missing_ok=True)
        console.print(f"[red]Failed to write Claude settings: {e}[/red]")
        return False


def setup_claude_code(dry_run: bool = False, home: Path | None = None) -> None:
    """Merge the Claude Code settings baseline, install the session-ledger hook, then project the policy."""
    if _apply_baseline(dry_run, home):
        project_policy_permissions("claude", home=home, dry_run=dry_run)


def _apply_baseline(dry_run: bool, home: Path | None) -> bool:
    """Merge the baseline and hook; False when the settings file is unreadable and was left alone."""
    path = get_claude_settings_path(home)
    hook_path = session_ledger_hook_path(home)

    if settings_unreadable(path):
        console.print(f"[yellow]! {path} is not a readable JSON object; fix it by hand, then re-run.[/yellow]")
        return False

    merge = merge_claude_settings(load_settings(path), session_end_command=session_ledger_command(hook_path))
    changes = merge.changes
    script_stale = not hook_script_current(hook_path)

    if not changes and not script_stale:
        console.print(f"[green]✓ Claude Code settings up to date:[/green] {path}")
        return True

    if changes:
        console.print(f"[cyan]Applying {len(changes)} baseline updates to {path}:[/cyan]")
        for change in changes:
            console.print(f"  [dim green]{change}[/dim green]")
    if script_stale:
        console.print(f"[cyan]Installing the session-ledger hook script at {hook_path}[/cyan]")

    if dry_run:
        console.print("[dim]Dry run: no changes written.[/dim]")
        return True

    # Install the script before wiring it, so the hook never points at nothing.
    if script_stale:
        try:
            install_hook_script(hook_path)
        except OSError as e:
            console.print(f"[red]Failed to install {hook_path}: {e}; Claude Code settings left unchanged.[/red]")
            return False
        console.print(f"[green]✓ Session-ledger hook script installed at {hook_path}[/green]")

    if not changes:
        return True
    if not save_claude_settings(path, merge.settings):
        return False
    console.print(f"[bold green]✓ Successfully updated Claude Code settings at {path}[/bold green]")
    return True
