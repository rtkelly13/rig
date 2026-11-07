"""Shell shortcut configuration for the ``agent`` command.

Maintains an idempotent block in PowerShell $PROFILE or POSIX rc file defining
an ``agent`` function that delegates directly to the root shim without modifying cwd.
"""

from __future__ import annotations

import os
import sys
from pathlib import Path

from rich.console import Console

console = Console()

BEGIN_MARKER = "# >>> shared-utilities agent shortcut >>>"
END_MARKER = "# <<< shared-utilities agent shortcut <<<"


def upsert_block(existing: str, block: str, begin_marker: str, end_marker: str) -> str:
    """Insert ``block`` into ``existing`` text or replace region between markers."""
    begin = existing.find(begin_marker)
    end = existing.find(end_marker)
    if begin != -1 and end != -1 and end > begin:
        end += len(end_marker)
        before = existing[:begin].rstrip("\n")
        after = existing[end:].lstrip("\n")
        parts = [p for p in (before, block, after) if p]
        return "\n\n".join(parts).rstrip("\n") + "\n"
    base = existing.rstrip("\n")
    if base:
        return f"{base}\n\n{block}\n"
    return f"{block}\n"


def upsert_agent_shortcut(existing: str, block: str) -> str:
    """Insert block, replacing an existing shared-utilities shortcut block."""
    return upsert_block(existing, block, BEGIN_MARKER, END_MARKER)


def powershell_profile_path() -> Path:
    """Find active PowerShell profile, honoring OneDrive redirection if present."""
    home = Path.home()
    # Check OneDrive-redirected profile paths first
    onedrive_pwsh = list(home.glob("*/Documents/PowerShell/Microsoft.PowerShell_profile.ps1"))
    if onedrive_pwsh:
        return onedrive_pwsh[0]

    # Standard Documents/PowerShell path
    standard_pwsh = home / "Documents" / "PowerShell" / "Microsoft.PowerShell_profile.ps1"
    if standard_pwsh.is_file():
        return standard_pwsh

    # WindowsPowerShell fallback
    onedrive_win_pwsh = list(home.glob("*/Documents/WindowsPowerShell/Microsoft.PowerShell_profile.ps1"))
    if onedrive_win_pwsh:
        return onedrive_win_pwsh[0]

    standard_win_pwsh = home / "Documents" / "WindowsPowerShell" / "Microsoft.PowerShell_profile.ps1"
    if standard_win_pwsh.is_file():
        return standard_win_pwsh

    return standard_pwsh


def posix_profile_path() -> Path:
    """Return user's POSIX shell profile path (.zshrc or .bashrc)."""
    home = Path.home()
    shell = os.environ.get("SHELL", "")
    rc = ".zshrc" if "zsh" in shell or (home / ".zshrc").exists() else ".bashrc"
    return home / rc


def generate_powershell_block(shim_path: Path) -> str:
    """Generate the PowerShell profile block calling the shim with @args."""
    return f"""{BEGIN_MARKER}
# Managed by `devenv` setup — do not edit between these markers.
# `agent` launches your preferred AI coding agent or selected flag (-agy, -ccc, -agywsl).
function agent {{
    & '{shim_path}' @args
}}
{END_MARKER}"""


def generate_posix_block(shim_path: Path) -> str:
    """Generate the POSIX shell profile block calling the shim with $@."""
    posix_shim = shim_path.as_posix()
    return f"""{BEGIN_MARKER}
# Managed by `devenv` setup — do not edit between these markers.
# `agent` launches your preferred AI coding agent or selected flag (-agy, -ccc, -agywsl).
agent() {{
    "{posix_shim}" "$@"
}}
{END_MARKER}"""


def configure_agent_shortcut(
    shim_path: Path,
    *,
    profile_path: Path | None = None,
    dry_run: bool = False,
) -> bool:
    """Write or refresh the ``agent`` shell shortcut in the user profile."""
    is_windows = sys.platform == "win32"
    target_profile = profile_path or (powershell_profile_path() if is_windows else posix_profile_path())
    block = generate_powershell_block(shim_path) if is_windows else generate_posix_block(shim_path)

    try:
        existed = target_profile.is_file()
        existing = target_profile.read_text(encoding="utf-8") if existed else ""
        updated = upsert_agent_shortcut(existing, block)

        if existed and updated == existing:
            console.print(f"[dim]Agent shortcut block in {target_profile.name} is already up to date.[/dim]")
            return True

        if dry_run:
            console.print(f"[dim][dry-run] Would update agent shortcut in {target_profile}[/dim]")
            return True

        target_profile.parent.mkdir(parents=True, exist_ok=True)
        target_profile.write_text(updated, encoding="utf-8")
        console.print(f"[bold green]OK: Agent shortcut configured in {target_profile.name}[/bold green]")
        return True
    except OSError as e:
        console.print(f"[red]Failed to configure agent shortcut: {e}[/red]")
        return False
