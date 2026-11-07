"""Agent browser profile provisioning step and shell integration."""

from __future__ import annotations

import sys
from pathlib import Path

from rich.console import Console

from rig.agent_browser.config import PROFILE_NAMES, PROFILES_ROOT

console = Console()


def inject_agent_browser_shell_helpers(dry_run: bool = False) -> None:
    """Inject per-profile shortcuts into the user's shell profile.

    The machine default profile is `rig browser setup`'s job (user-scoped
    AGENT_BROWSER_PROFILE); these shortcuts only override it for one command.
    """
    if dry_run:
        return

    home = Path.home()
    if sys.platform == "win32":
        profile_file = home / "Documents" / "WindowsPowerShell" / "Microsoft.PowerShell_profile.ps1"
        snippet = (
            "\n# Agent Browser Helpers\n"
            'function agent-browser-dev { agent-browser --profile "$HOME\\.agent-browser-profiles\\dev" @args }\n'
            "function agent-browser-personal "
            '{ agent-browser --profile "$HOME\\.agent-browser-profiles\\personal" @args }\n'
        )
    else:
        profile_file = home / ".zshrc"
        snippet = (
            "\n# Agent Browser Helpers\n"
            'alias agent-browser-dev="agent-browser --profile $HOME/.agent-browser-profiles/dev"\n'
            'alias agent-browser-personal="agent-browser --profile $HOME/.agent-browser-profiles/personal"\n'
        )

    if not profile_file.is_file():
        return

    try:
        content = profile_file.read_text(encoding="utf-8")
        if "# Agent Browser Helpers" not in content:
            profile_file.write_text(content + snippet, encoding="utf-8")
            console.print(f"[bold green]✓ Injected Agent Browser shell helpers into {profile_file.name}[/bold green]")
    except (OSError, UnicodeError) as e:
        console.print(f"[yellow]Could not update shell profile for agent browser: {e}[/yellow]")


def setup_agent_browser_profiles(dry_run: bool = False) -> None:
    """Provision ~/.agent-browser-profiles directory layout and shell helpers."""
    base_dir = PROFILES_ROOT
    if dry_run:
        console.print(f"[dim]Dry run: would ensure {base_dir} profiles exist[/dim]")
        return

    base_dir.mkdir(parents=True, exist_ok=True)
    for p in PROFILE_NAMES:
        profile_path = base_dir / p
        profile_path.mkdir(parents=True, exist_ok=True)

    console.print(f"[bold green]✓ Agent-browser profiles initialized in {base_dir}[/bold green]")
    inject_agent_browser_shell_helpers(dry_run=dry_run)
