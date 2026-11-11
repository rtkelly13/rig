"""The unified ``rig`` CLI — developer operations cockpit & estate toolchain.

Provides a unified interface across all developer tools:
- AI coding agent launcher (Antigravity, Claude Code, OpenCode, Codex)
- Estate governance & workspace sync
- Desktop manager & window IaC reconciliation
- Commands C# owns (devenv, doctor and the rest of csharp_cli.PORTED) are stubs that hand over to it
"""

from __future__ import annotations

import sys
from collections.abc import Sequence

import typer
from rich.console import Console

from desktop_manager.web_cli import web_app
from rig import csharp_cli, tracing
from rig.c4_okf.cli import app as c4_app
from rig.flags.cli import app as flags_app
from rig.repo_governance.align_repo_conventions import app as repo_governance_app
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
app.add_typer(triage_app, name="triage")
app.add_typer(web_app, name="web")
app.add_typer(flags_app, name="flags")
app.add_typer(c4_app, name="c4")
app.add_typer(repo_governance_app, name="repo-governance")


for _name in csharp_cli.PORTED:
    csharp_cli.add_stub(app, _name)


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

    A bare ``rig`` or a leading agent shortcut flag launches an agent: the C# CLI's launcher takes the line whole.
    """
    raw_args = list(sys.argv[1:] if argv is None else argv)
    if not raw_args or raw_args[0] in AGENT_SHORTCUT_FLAGS:
        return csharp_cli.run(raw_args)

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
