"""Nerd Font detection, download, and installation step."""

from __future__ import annotations

import http.client
import io
import shutil
import zipfile
import zlib
from dataclasses import dataclass
from pathlib import Path

from rich.console import Console

from ..downloads import fetch_bytes
from ..engine.context import StepContext

console = Console()


@dataclass(slots=True, frozen=True)
class NerdFont:
    """A Nerd Font family and the release zip that ships it."""

    name: str
    zip_name: str


NERD_FONTS = (
    NerdFont("JetBrainsMono Nerd Font", "JetBrainsMono.zip"),
    NerdFont("CascadiaCode Nerd Font", "CascadiaCode.zip"),
    NerdFont("FiraCode Nerd Font", "FiraCode.zip"),
)

NERD_FONT_RELEASE_BASE = "https://github.com/ryanoasis/nerd-fonts/releases/latest/download"


def get_font_dir(ctx: StepContext) -> Path:
    """Return user font directory for the current OS."""
    home = Path.home()
    if ctx.is_windows:
        return home / "AppData" / "Local" / "Microsoft" / "Windows" / "Fonts"
    if ctx.is_macos:
        return home / "Library" / "Fonts"
    return home / ".local" / "share" / "fonts"


def is_nerd_font_installed(ctx: StepContext, font_name: str) -> bool:
    """Check whether font files matching the font family name exist."""
    font_dir = get_font_dir(ctx)
    if not font_dir.exists():
        return False
    clean_name = font_name.replace(" ", "").lower()
    for ext in ("*.ttf", "*.otf"):
        for f in font_dir.glob(ext):
            if clean_name in f.name.replace(" ", "").lower():
                return True
    return False


def run_fonts_setup(ctx: StepContext) -> None:
    """Download and install Nerd Fonts."""
    font_dir = get_font_dir(ctx)
    font_dir.mkdir(parents=True, exist_ok=True)

    for font in NERD_FONTS:
        if is_nerd_font_installed(ctx, font.name):
            console.print(f"[green]✓ Font already present:[/green] {font.name}")
            continue

        if ctx.dry_run:
            console.print(f"[dim]Dry run: would download and install {font.name}[/dim]")
            continue

        url = f"{NERD_FONT_RELEASE_BASE}/{font.zip_name}"
        console.print(f"[cyan]Downloading {font.name} from {url}...[/cyan]")
        try:
            data = fetch_bytes(url, user_agent="shared-utilities/devenv")

            with zipfile.ZipFile(io.BytesIO(data)) as zf:
                for member in zf.namelist():
                    if member.endswith((".ttf", ".otf")) and "Windows Compatible" not in member:
                        target = font_dir / Path(member).name
                        with zf.open(member) as src, target.open("wb") as dst:
                            shutil.copyfileobj(src, dst)

            console.print(f"[green]✓ Installed font:[/green] {font.name}")
        except (OSError, EOFError, zlib.error, http.client.HTTPException, zipfile.BadZipFile) as e:
            console.print(f"[yellow]! Could not download/install {font.name}: {e}[/yellow]")
