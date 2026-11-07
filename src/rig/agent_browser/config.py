"""User-scoped environment variable provisioning for ``agent-browser``.

Ports the workspace/playbook ``devenv_setup/steps/agent_browser_config.py``
pattern to the personal shared-utilities repo — without the workspace's
``common`` dependency. Provisions ``AGENT_BROWSER_PROFILE``,
``AGENT_BROWSER_AUTO_CONNECT``, and ``AGENT_BROWSER_HEADED`` at user scope
(shell profile on macOS/Linux, registry on Windows) using a **never-clobber**
strategy: anything already set is left untouched.

Two dedicated profiles are available for agent-browser-only sessions:

- **dev** (``~/.agent-browser-profiles/dev``) — the default. For development
  automation: localhost servers, personal projects, GitHub, self-hosted tooling.
- **personal** (``~/.agent-browser-profiles/personal``) — for personal services
  unrelated to development (banking, shopping, etc.). Opt in per invocation with
  ``AGENT_BROWSER_PROFILE=~/.agent-browser-profiles/personal agent-browser ...``.

Both are separate from the developer's daily Chrome — never locked, safe to
drive while normal Chrome is open. Sign into the relevant services in each once.

Also includes the PROJ-2766 daemon-restart probe: warns when a live
``agent-browser`` daemon is detected after env var changes, because profile and
headed settings bind only at daemon startup.
"""

from __future__ import annotations

import os
import subprocess
import sys
from dataclasses import dataclass
from pathlib import Path

from rich.console import Console

_console = Console(stderr=True)

# ---------------------------------------------------------------------------
# Baseline team-standard defaults
# ---------------------------------------------------------------------------

#: Root of the dedicated agent-browser Chrome profiles; one directory per profile.
PROFILES_ROOT: Path = Path.home() / ".agent-browser-profiles"
PROFILE_NAMES: list[str] = ["dev", "personal"]
DEV_PROFILE_DIR: Path = PROFILES_ROOT / "dev"

# Profile env var: points at the dedicated agent-browser dev Chrome dir.
PROFILE_ENV_VAR = "AGENT_BROWSER_PROFILE"

# Always-set defaults (never-clobber applied).
# - dev profile is the default; auto-connect (reuse running daily Chrome) is opt-in.
# - personal profile is used per invocation: AGENT_BROWSER_PROFILE=<personal-dir>
# - Headed by default.
BASELINE_ENV: dict[str, str] = {
    "AGENT_BROWSER_AUTO_CONNECT": "0",
    "AGENT_BROWSER_HEADED": "1",
    PROFILE_ENV_VAR: str(DEV_PROFILE_DIR),
}


# ---------------------------------------------------------------------------
# Shell profile path
# ---------------------------------------------------------------------------


def _unix_shell_profile() -> Path:
    """The shell profile file to append exports to on macOS/Linux."""
    shell = os.environ.get("SHELL", "/bin/bash")
    if "zsh" in shell:
        return Path.home() / ".zshrc"
    return Path.home() / ".bashrc"


# ---------------------------------------------------------------------------
# Reading / writing user-scoped env vars
# ---------------------------------------------------------------------------


def get_user_env_var(var_name: str) -> str | None:
    """Return the persisted value of ``var_name``, or ``None`` if unset.

    Checks the current process environment first, then the persisted user-scope
    store: the Windows *User* registry on Windows, or the shell profile on
    macOS/Linux.
    """
    value = os.environ.get(var_name)
    if value:
        return value

    if sys.platform == "win32":
        result = subprocess.run(
            [
                "powershell",
                "-NoProfile",
                "-Command",
                f'[Environment]::GetEnvironmentVariable("{var_name}", "User")',
            ],
            capture_output=True,
            text=True,
        )
        if result.returncode == 0 and result.stdout.strip():
            return result.stdout.strip()
        return None

    profile_path = _unix_shell_profile()
    if profile_path.exists():
        content = profile_path.read_text(encoding="utf-8")
        for line in content.splitlines():
            stripped = line.strip()
            if stripped.startswith(f"export {var_name}="):
                return stripped.split("=", 1)[1].strip().strip('"')
    return None


def set_user_env_var(var_name: str, value: str) -> bool:
    """Persist ``var_name=value`` at **User** scope (no admin required).

    On Windows uses ``[Environment]::SetEnvironmentVariable(..., "User")``; on
    macOS/Linux appends a guarded ``export`` line to the shell profile.
    Returns ``True`` on success.
    """
    if sys.platform == "win32":
        result = subprocess.run(
            [
                "powershell",
                "-NoProfile",
                "-Command",
                f'[Environment]::SetEnvironmentVariable("{var_name}", "{value}", "User")',
            ],
            capture_output=True,
            text=True,
        )
        if result.returncode != 0:
            _console.print(f"[red]✗[/] Failed to set {var_name}: {result.stderr.strip()}")
        return result.returncode == 0

    # macOS / Linux: append a guarded export to the shell profile.
    profile_path = _unix_shell_profile()
    try:
        export_line = f'\nexport {var_name}="{value}"\n'
        with profile_path.open("a", encoding="utf-8") as f:
            f.write(export_line)
        return True
    except OSError as e:
        _console.print(f"[red]✗[/] Failed to update shell profile: {e}")
        return False


# ---------------------------------------------------------------------------
# Plan
# ---------------------------------------------------------------------------


def build_target_env() -> dict[str, str]:
    """The full set of env vars we want set (before never-clobber filtering)."""
    return dict(BASELINE_ENV)


def plan_env_changes(target: dict[str, str]) -> dict[str, str]:
    """Filter ``target`` to only the vars not already persisted (never-clobber): name to value."""
    return {name: value for name, value in target.items() if get_user_env_var(name) is None}


def detect_legacy_auto_connect_default() -> bool:
    """True if an older (auto-connect-default) config appears already persisted.

    The initial version of this setup set ``AGENT_BROWSER_AUTO_CONNECT=1``.
    Because the step is never-clobber it will not flip that to the new
    dedicated-profile default, so we detect the stale value to print a
    manual-migration hint.
    """
    return get_user_env_var("AGENT_BROWSER_AUTO_CONNECT") == "1"


# ---------------------------------------------------------------------------
# Daemon probe (PROJ-2766 pattern)
# ---------------------------------------------------------------------------


def is_agent_browser_daemon_running() -> bool:
    """Best-effort probe for a live ``agent-browser`` daemon process.

    Profile and headed settings bind only at **daemon startup**, so a daemon
    already running when this function writes env vars keeps its *old* profile
    until restarted. Fails safe to ``False`` — a false negative only omits an
    advisory hint, so any probe error is swallowed.
    """
    try:
        if sys.platform == "win32":
            result = subprocess.run(
                [
                    "powershell",
                    "-NoProfile",
                    "-Command",
                    "Get-Process -Name agent-browser -ErrorAction SilentlyContinue "
                    "| Select-Object -First 1 | ForEach-Object { $_.Id }",
                ],
                capture_output=True,
                text=True,
            )
            return result.returncode == 0 and bool(result.stdout.strip())
        result = subprocess.run(["pgrep", "-f", "agent-browser"], capture_output=True, text=True)
        return result.returncode == 0 and bool(result.stdout.strip())
    except (OSError, subprocess.SubprocessError):
        return False


# ---------------------------------------------------------------------------
# Apply
# ---------------------------------------------------------------------------


@dataclass(frozen=True, slots=True)
class EnvConfigOutcome:
    """How many variables were written, and how many were already set and left alone."""

    applied: int
    skipped: int


def apply_env_config(*, dry_run: bool = False, force: bool = False) -> EnvConfigOutcome:
    """Provision agent-browser user-scoped environment variables.

    Args:
        dry_run: Report the changes that would be made without writing.
        force: Overwrite already-set variables (ignores never-clobber).

    Returns:
        The applied and skipped counts.
    """
    target = build_target_env()
    changes = plan_env_changes(target) if not force else dict(target)

    if detect_legacy_auto_connect_default():
        _console.print(
            "[yellow]⚠[/] AGENT_BROWSER_AUTO_CONNECT is already set to '1' (the previous default). "
            "This step will not overwrite it. To adopt the dedicated-profile default, "
            "set these at User scope by hand and open a new terminal:\n"
            "  AGENT_BROWSER_AUTO_CONNECT=0\n"
            f"  {PROFILE_ENV_VAR}={DEV_PROFILE_DIR}"
        )

    skipped = len(target) - len(changes)

    if not changes:
        _console.print("[green]✓[/] agent-browser environment already configured.")
        _print_signin_hint()
        return EnvConfigOutcome(0, skipped)

    _console.print("[bold]Planned user-scope environment variables:[/]")
    for name, value in changes.items():
        _console.print(f"  [green]+ {name}={value}[/green]")
    _console.print()

    if dry_run:
        _console.print("[dim]Dry run — no changes written.[/dim]")
        return EnvConfigOutcome(0, skipped)

    applied = sum(1 for name, value in changes.items() if set_user_env_var(name, value))

    if applied:
        _console.print(
            f"[green]✓[/] Applied {applied} agent-browser environment setting(s). "
            "Open a new terminal for them to take effect."
        )
        _warn_daemon_restart_needed()
        _print_signin_hint()

    return EnvConfigOutcome(applied, skipped)


def _warn_daemon_restart_needed() -> None:
    """Warn if a live daemon will ignore the just-written profile env vars.

    ``agent-browser`` binds profile/headed only at daemon startup, so a daemon
    that was already running keeps its old profile despite the successful write.
    Prompt a restart so the new dedicated-profile default actually takes effect.
    """
    if not is_agent_browser_daemon_running():
        return
    _console.print(
        "[yellow]⚠[/] An agent-browser daemon is already running and will keep using its "
        "current profile until it is restarted. Close it so the new settings "
        "take effect on the next run:\n"
        "  agent-browser close --all\n"
        "(This only shuts down agent-browser's own Chrome-for-Testing; it will "
        "not close your daily Chrome unless a run used --auto-connect.)"
    )


def _print_signin_hint() -> None:
    """Tell the developer how to sign into the dedicated profile once."""
    _console.print(
        "[dim]Dedicated profile is the default. Sign into the services you need "
        "in it once (a headed window opens; complete any sign-in, then close):\n"
        f"  agent-browser open --profile {DEV_PROFILE_DIR} --headed <url>\n"
        "For an ad-hoc check against a third-party site already open in your daily "
        "Chrome, opt into auto-connect for that run: "
        "AGENT_BROWSER_AUTO_CONNECT=1 agent-browser open <url>[/dim]"
    )
