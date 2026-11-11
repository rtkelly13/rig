"""Typer CLI entrypoint for development environment management."""

from __future__ import annotations

import typer

from rig import csharp_cli

from .doctor.core import run_doctor

app = typer.Typer(
    name="devenv",
    help="Unified development environment manager (tools, runtimes, fonts, git, AI coding agents).",
    no_args_is_help=True,
)


@app.command("doctor")
def doctor_cmd() -> None:
    """Validate environment health across tools, runtimes, git, fonts, and AI assistants.

    For lint, type and test gates on this repo, run `uv run status`.
    """
    raise typer.Exit(code=run_doctor())


csharp_cli.add_stub(app, "models")
csharp_cli.add_group_stubs(app, "devenv")


def main() -> None:
    app()


if __name__ == "__main__":
    main()
