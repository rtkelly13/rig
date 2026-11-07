"""Install my personal gh token scope overlay for the workspace devenv to enforce.

The team baseline and the scope checks live in the workspace repo
(``devenv doctor git auth``). This step only places my personal extras at
``~/.config/devenv/gh-scopes.json``, where the workspace merges them in.
"""

from __future__ import annotations

import json
import os
from enum import Enum
from pathlib import Path

from pydantic import BaseModel, Field
from rich.console import Console

from rig.typed_json import load_json

from ..engine.context import StepContext

console = Console()

OVERLAY_SOURCE = Path(__file__).parent.parent / "config" / "gh_scopes.json"


class ScopeEntry(BaseModel):
    """One scope with the reason it is (or is not) held."""

    scope: str
    rationale: str = Field(min_length=1)


class GhScopeOverlay(BaseModel):
    """Personal scope extras, in the workspace overlay schema."""

    required: list[ScopeEntry] = Field(default_factory=list[ScopeEntry])
    optional: list[ScopeEntry] = Field(default_factory=list[ScopeEntry])
    disallowed: list[ScopeEntry] = Field(default_factory=list[ScopeEntry])


class OverlayState(Enum):
    IN_SYNC = "in sync"
    OUT_OF_DATE = "out of date"
    MISSING = "not installed"


def overlay_target() -> Path:
    """Where the workspace devenv reads the per-user overlay."""
    config_home = Path(os.environ.get("XDG_CONFIG_HOME") or Path.home() / ".config")
    return config_home / "devenv" / "gh-scopes.json"


def load_overlay(path: Path = OVERLAY_SOURCE) -> GhScopeOverlay:
    """Load and validate the overlay."""
    return GhScopeOverlay.model_validate_json(path.read_text(encoding="utf-8"))


def overlay_state(source: Path = OVERLAY_SOURCE, target: Path | None = None) -> OverlayState:
    """Compare the installed overlay with the one declared in this repo."""
    target = target or overlay_target()
    if not target.is_file():
        return OverlayState.MISSING
    try:
        installed = load_json(target.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return OverlayState.OUT_OF_DATE
    declared = load_json(source.read_text(encoding="utf-8"))
    return OverlayState.IN_SYNC if installed == declared else OverlayState.OUT_OF_DATE


def run_github_cli_setup(ctx: StepContext) -> None:
    """Copy the overlay to ~/.config/devenv/gh-scopes.json when it differs."""
    overlay = load_overlay()
    target = overlay_target()
    state = overlay_state(target=target)
    scopes = ", ".join(e.scope for e in overlay.required)

    if state is OverlayState.IN_SYNC:
        console.print(f"[green]✓ gh scope overlay is installed ({scopes}).[/green]")
    elif ctx.dry_run:
        console.print(f"[yellow]Would write gh scope overlay ({scopes}) to {target}[/yellow]")
        return
    else:
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(OVERLAY_SOURCE.read_text(encoding="utf-8"), encoding="utf-8")
        console.print(f"[green]✓ Wrote gh scope overlay ({scopes}) to {target}[/green]")

    console.print("  Check and reconcile the token with: [bold]devenv doctor git auth[/bold] (workspace)")
