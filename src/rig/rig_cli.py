"""The unified ``rig`` CLI — developer operations cockpit & estate toolchain.

Provides a unified interface across all developer tools:
- AI coding agent launcher (Antigravity, Claude Code, OpenCode, Codex)
- Estate governance & workspace sync
- Desktop manager & window IaC reconciliation
- Development environment doctor & setup
"""

from __future__ import annotations

import sys
from collections.abc import Sequence

import typer
from rich.console import Console

from desktop_manager.web_cli import web_app
from rig import tracing
from rig.agent_browser.cli import app as browser_app
from rig.agent_launcher.cli import agent_main
from rig.c4_okf.cli import app as c4_app
from rig.devenv.cli import app as devenv_app
from rig.estate.cli import app as estate_app
from rig.flags.cli import app as flags_app
from rig.gh_cli import app as gh_app
from rig.models.cli import app as models_app
from rig.triage.cli import app as triage_app
from rig.workspace.cli import app as workspace_app

console = Console()

app = typer.Typer(
    name="rig",
    help="Developer rig — unified CLI cockpit for workspace management, AI agents, and estate tooling.",
    no_args_is_help=False,
    add_completion=False,
)

app.add_typer(workspace_app, name="workspace")
app.add_typer(devenv_app, name="devenv")
app.add_typer(estate_app, name="estate")
app.add_typer(models_app, name="models")
app.add_typer(triage_app, name="triage")
app.add_typer(web_app, name="web")
app.add_typer(flags_app, name="flags")
app.add_typer(browser_app, name="browser")
app.add_typer(gh_app, name="gh")
app.add_typer(c4_app, name="c4")


@app.command("agent", context_settings={"allow_extra_args": True, "ignore_unknown_options": True})
def agent_cmd(ctx: typer.Context) -> None:
    """Launch preferred AI coding agent or selected flag (-agy, -ccc, -oc, -codex, -dng)."""
    exit_code = agent_main(ctx.args)
    raise typer.Exit(code=exit_code)


@app.command("status", context_settings={"allow_extra_args": True, "ignore_unknown_options": True})
def status_cmd(ctx: typer.Context) -> None:
    """Report repository health, git status, and estate verification."""
    from repo_health.cli import app as status_app

    status_app(args=ctx.args, prog_name="rig status")


@app.command("sync")
def sync_cmd(
    root: str | None = typer.Option(None, "--root", "-r", help="Path to personal estate root directory"),
    config: str | None = typer.Option(None, "--config", "-c", help="Path to managed-projects.json registry file"),
    gitignore: bool = typer.Option(
        True, "--gitignore/--no-gitignore", help="Synchronize common gitignores across managed projects"
    ),
) -> None:
    """Synchronize estate index (AGENTS.md) and personal.code-workspace."""
    from rig.workspace.cli import sync_command

    sync_command(root=root, config=config, gitignore=gitignore)


@app.command("doctor")
def doctor_cmd() -> None:
    """Validate environment health across tools, runtimes, git, and AI assistants."""
    from rig.devenv.doctor.core import run_doctor

    rc = run_doctor()
    raise typer.Exit(code=rc)


@app.command("align", context_settings={"allow_extra_args": True, "ignore_unknown_options": True})
def align_cmd(ctx: typer.Context) -> None:
    """Reconcile desktop window layout and virtual desktop state."""
    from desktop_manager.cli import app as dm_app

    dm_app(args=["align", *ctx.args], prog_name="rig align")


AGENT_SHORTCUT_FLAGS = {
    "-agy",
    "-ccc",
    "-agywsl",
    "-oc",
    "-opencode",
    "-codex",
    "-cpl",
    "-copilot",
    "-dng",
    "--dng",
    "--dangerously-skip-permissions",
    "-r",
    "--remote-control",
    "--print-config",
}


def main(argv: Sequence[str] | None = None) -> int:
    """Unified entry point for the rig CLI.

    Dispatches directly to agent_main if invoked with agent shortcut flags or empty args.
    """
    raw_args = list(sys.argv[1:] if argv is None else argv)

    # If first token is an agent flag, dispatch straight to agent launcher
    if raw_args and raw_args[0] in AGENT_SHORTCUT_FLAGS:
        return agent_main(raw_args)

    # If no args at all, default to launching preferred agent
    if not raw_args:
        return agent_main([])

    tracing.init_tracing("rig")
    tracing.flush_periodically(30)
    command = " ".join(arg for arg in raw_args[:2] if not arg.startswith("-"))
    try:
        with tracing.span(f"rig {command}".rstrip(), scope="rig.cli", **{"cli.argv": raw_args}):
            app(raw_args, prog_name="rig")
        return 0
    except SystemExit as exc:
        code = exc.code
        return code if isinstance(code, int) else 0


if __name__ == "__main__":
    sys.exit(main())
