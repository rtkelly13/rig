"""``rig browser setup``: provision the environment the upstream ``agent-browser`` CLI reads.

Only setup lives here. Run the upstream ``agent-browser`` binary directly; the
environment variables written here (``AGENT_BROWSER_PROFILE`` and friends) are its
defaults, so there is one writer of the default profile, not two.
"""

from __future__ import annotations

import typer
from rich.console import Console

_console = Console()

app = typer.Typer(
    name="browser",
    help="Set up the environment the upstream agent-browser CLI reads.",
    no_args_is_help=True,
    add_completion=False,
)


# ---------------------------------------------------------------------------
# setup
# ---------------------------------------------------------------------------


@app.command()
def setup(
    dry_run: bool = typer.Option(False, "--dry-run", help="Show planned changes without writing."),
    force: bool = typer.Option(
        False,
        "--force",
        help="Overwrite already-set variables (ignores never-clobber).",
    ),
) -> None:
    """Provision user-scoped agent-browser environment variables.

    Writes AGENT_BROWSER_PROFILE, AGENT_BROWSER_AUTO_CONNECT, and
    AGENT_BROWSER_HEADED at User scope using a never-clobber strategy.
    Also warns if a live daemon is running (it will ignore the new vars
    until restarted; close it with ``agent-browser close --all``).
    """
    # Lazy import: keeps `rig --help` fast.
    from rig.agent_browser.config import apply_env_config

    outcome = apply_env_config(dry_run=dry_run, force=force)

    if outcome.skipped and not outcome.applied:
        pass  # apply_env_config already printed the "already configured" message.
    elif outcome.skipped:
        _console.print(f"[dim]{outcome.skipped} var(s) already set — left untouched.[/dim]")
