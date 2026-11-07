"""Terminal & prompt customization step (Oh My Posh & Windows Terminal)."""

from __future__ import annotations

import copy
import json
import os
import shutil
from dataclasses import dataclass
from pathlib import Path
from typing import Annotated

from pydantic import Field
from rich.console import Console

from rig.typed_json import Lenient, decode, try_decode_json

from ..engine.context import StepContext

console = Console()


@dataclass(slots=True, frozen=True)
class WindowsTerminalStatus:
    """Status summary of Windows Terminal settings."""

    installed: bool
    path: Path | None
    snap_to_grid: bool | None
    font_face: str | None
    starting_directory: str | None


@dataclass(slots=True, frozen=True)
class _FontView:
    face: Lenient[str] = None
    size: Lenient[int] = None


@dataclass(slots=True, frozen=True)
class _DefaultsView:
    font: Lenient[dict[str, object]] = None
    starting_directory: Annotated[Lenient[str], Field(alias="startingDirectory")] = None


@dataclass(slots=True, frozen=True)
class _ProfilesView:
    defaults: Lenient[dict[str, object]] = None


@dataclass(slots=True, frozen=True)
class _SettingsView:
    snap_to_grid_on_resize: Annotated[Lenient[bool], Field(alias="snapToGridOnResize")] = None
    profiles: Lenient[dict[str, object]] = None


def _clean_json_text(text: str) -> str:
    """Strip single-line // comments from JSON text if present."""
    lines: list[str] = []
    for line in text.splitlines():
        stripped = line.strip()
        if stripped.startswith("//"):
            continue
        lines.append(line)
    return "\n".join(lines)


def _load_settings_dict(target: Path) -> dict[str, object]:
    if not target.is_file():
        return {}
    try:
        raw_text = target.read_text(encoding="utf-8")
        parsed = try_decode_json(dict[str, object], raw_text)
        if parsed is not None:
            return parsed
        return try_decode_json(dict[str, object], _clean_json_text(raw_text)) or {}
    except OSError:
        return {}


def get_windows_terminal_settings_path(local_app_data: Path | None = None) -> Path | None:
    """Find the settings.json file for Windows Terminal (stable, preview, or unpackaged)."""
    base = local_app_data or (
        Path(os.environ["LOCALAPPDATA"]) if "LOCALAPPDATA" in os.environ else Path.home() / "AppData" / "Local"
    )
    candidates = (
        base / "Packages" / "Microsoft.WindowsTerminal_8wekyb3d8bbwe" / "LocalState" / "settings.json",
        base / "Packages" / "Microsoft.WindowsTerminalPreview_8wekyb3d8bbwe" / "LocalState" / "settings.json",
        base / "Microsoft" / "Windows Terminal" / "settings.json",
    )
    for p in candidates:
        if p.is_file():
            return p
    for p in candidates:
        if p.parent.is_dir():
            return p
    return None


def get_windows_terminal_status(path: Path | None = None) -> WindowsTerminalStatus:
    """Inspect Windows Terminal settings status."""
    target_path = path or get_windows_terminal_settings_path()
    if not target_path or not target_path.is_file():
        return WindowsTerminalStatus(
            installed=False,
            path=target_path,
            snap_to_grid=None,
            font_face=None,
            starting_directory=None,
        )

    try:
        data = _load_settings_dict(target_path)
        if not data:
            return WindowsTerminalStatus(
                installed=True,
                path=target_path,
                snap_to_grid=None,
                font_face=None,
                starting_directory=None,
            )

        view = decode(_SettingsView, data)
        profiles_view = decode(_ProfilesView, view.profiles or {})
        defaults_view = decode(_DefaultsView, profiles_view.defaults or {})
        font_view = decode(_FontView, defaults_view.font or {})

        return WindowsTerminalStatus(
            installed=True,
            path=target_path,
            snap_to_grid=view.snap_to_grid_on_resize,
            font_face=font_view.face,
            starting_directory=defaults_view.starting_directory,
        )
    except (OSError, ValueError):
        return WindowsTerminalStatus(
            installed=True,
            path=target_path,
            snap_to_grid=None,
            font_face=None,
            starting_directory=None,
        )


def configure_windows_terminal(
    ctx: StepContext,
    *,
    font_name: str | None = None,
    starting_directory: str | None = None,
    disable_snap_to_grid: bool = True,
    settings_path: Path | None = None,
) -> bool:
    """Configure Windows Terminal settings (disabling snapToGridOnResize and setting defaults)."""
    target = settings_path or get_windows_terminal_settings_path()
    if not target or not target.parent.exists():
        console.print("[dim]Windows Terminal not detected. Skipping terminal settings.[/dim]")
        return False

    try:
        existing = _load_settings_dict(target)
        changes: list[str] = []
        data = copy.deepcopy(existing)

        # 1. snapToGridOnResize: False (prevents window unsnapping/jittering when CLI tools render)
        view = decode(_SettingsView, data)
        if disable_snap_to_grid and view.snap_to_grid_on_resize is not False:
            data["snapToGridOnResize"] = False
            changes.append("Disabled snapToGridOnResize (prevents snap break and margin jitter)")

        # 2. Profiles defaults
        profiles = view.profiles or {}
        data["profiles"] = profiles

        profiles_view = decode(_ProfilesView, profiles)
        defaults = profiles_view.defaults or {}
        profiles["defaults"] = defaults

        defaults_view = decode(_DefaultsView, defaults)

        # Font configuration
        if font_name:
            font_view = decode(_FontView, defaults_view.font or {})
            font = defaults_view.font or {}
            defaults["font"] = font

            if font_view.face != font_name:
                font["face"] = font_name
                changes.append(f"Set font face to {font_name}")
            if font_view.size is None:
                font["size"] = 10
                changes.append("Set font size to 10")

        # Starting directory
        if starting_directory and defaults_view.starting_directory != starting_directory:
            defaults["startingDirectory"] = starting_directory
            changes.append(f"Set starting directory to {starting_directory}")

        if not changes:
            console.print("[green]✓ Windows Terminal settings are already configured.[/green]")
            return True

        if ctx.dry_run:
            console.print(f"[dim]Dry run: would update {target}:[/dim]")
            for ch in changes:
                console.print(f"[dim]  • {ch}[/dim]")
            return True

        target.parent.mkdir(parents=True, exist_ok=True)
        staging = target.with_name(f"{target.name}.{os.getpid()}.tmp")
        with staging.open("w", encoding="utf-8") as f:
            json.dump(data, f, indent=2, ensure_ascii=False)
            f.write("\n")
        staging.replace(target)
        console.print(f"[bold green]✓ Updated Windows Terminal settings ({target}):[/bold green]")
        for ch in changes:
            console.print(f"  [green]•[/green] {ch}")
        return True

    except (OSError, ValueError) as e:
        console.print(f"[yellow]! Failed to configure Windows Terminal: {e}[/yellow]")
        return False


def get_shell_profile(ctx: StepContext) -> Path | None:
    """Detect default shell profile file."""
    home = Path.home()
    if ctx.is_windows:
        # PowerShell profile path
        docs = home / "Documents" / "WindowsPowerShell" / "Microsoft.PowerShell_profile.ps1"
        pwsh7 = home / "Documents" / "PowerShell" / "Microsoft.PowerShell_profile.ps1"
        return pwsh7 if pwsh7.parent.exists() else docs
    if ctx.is_macos or ctx.is_linux:
        zshrc = home / ".zshrc"
        bashrc = home / ".bashrc"
        return zshrc if zshrc.exists() else bashrc
    return None


def run_terminal_setup(ctx: StepContext) -> None:
    """Set up shell prompt integration and terminal settings."""
    if ctx.is_windows:
        default_dir = "C:\\code" if Path("C:/code").is_dir() else str(Path.home() / "code")
        default_font = "JetBrainsMono Nerd Font"
        configure_windows_terminal(
            ctx,
            font_name=default_font,
            starting_directory=default_dir,
            disable_snap_to_grid=True,
        )

    omp_installed = shutil.which("oh-my-posh") is not None
    if not omp_installed:
        console.print("[yellow]! oh-my-posh command is not installed. Install via tools step first.[/yellow]")
        return

    profile_path = get_shell_profile(ctx)
    if not profile_path:
        console.print("[yellow]! Could not determine shell profile path.[/yellow]")
        return

    console.print(f"[cyan]Target shell profile:[/cyan] {profile_path}")

    # Check if hook already present
    hook_str = (
        'eval "$(oh-my-posh init zsh)"' if "zsh" in str(profile_path) else "oh-my-posh init pwsh | Invoke-Expression"
    )

    content = ""
    if profile_path.exists():
        content = profile_path.read_text(encoding="utf-8", errors="replace")

    if "oh-my-posh" in content:
        console.print("[green]✓ Oh My Posh is already configured in your shell profile.[/green]")
        return

    if ctx.dry_run:
        console.print(f"[dim]Dry run: would add prompt hook to {profile_path}[/dim]")
        return

    profile_path.parent.mkdir(parents=True, exist_ok=True)
    with profile_path.open("a", encoding="utf-8") as f:
        f.write(f"\n# Oh My Posh Prompt\n{hook_str}\n")

    console.print(f"[bold green]✓ Added Oh My Posh initialization to {profile_path}[/bold green]")
