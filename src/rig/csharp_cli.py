"""The commands already ported to the C# rig CLI, reached from this Python one. TEMPORARY.

rig is moving to C# (#85). Until the ``rig`` shim starts the C# CLI itself, a ported command typed
here is handed to it: ``dotnet run --project <this checkout>/src/Rig.Cli -- <command> ...``, attached
to this console, with its exit code returned unchanged. The project is found beside this file, so
both halves always come from the same tree. ``RIG_FROM_PYTHON`` tells the C# side not to hand a
command back, so a disagreement about who owns one fails loudly instead of looping.

Retire this module when the shim points at C#: then nothing reaches the Python CLI for these.
"""

from __future__ import annotations

import os
import sys
from collections.abc import Callable, Sequence
from pathlib import Path

import typer

from rig import tracing

#: Commands the C# CLI owns. ``tests/Rig.Cli.Tests`` checks each is registered there and gone from here.
PORTED: tuple[str, ...] = ("gh",)

#: Set for the C# CLI so it refuses to forward a command back to Python.
BRIDGE_VARIABLE = "RIG_FROM_PYTHON"

#: Runs an attached command with extra environment and returns its exit code.
type Runner = Callable[[list[str], dict[str, str]], int]


def cli_project(here: Path | None = None) -> Path:
    """``src/Rig.Cli`` in the checkout this module was loaded from."""
    module = (here or Path(__file__)).resolve()
    return module.parents[2] / "src" / "Rig.Cli"


def argv(project: Path, args: Sequence[str]) -> list[str]:
    """The command line that runs *args* on the C# CLI built from *project*."""
    return ["dotnet", "run", "--project", str(project), "--", *args]


def _attached(command: list[str], env: dict[str, str]) -> int:
    return tracing.run(command, scope="rig.csharp_cli", env={**os.environ, **env}).returncode


def run(args: Sequence[str], *, project: Path | None = None, runner: Runner = _attached) -> int:
    """Run *args* (a ported command and its arguments) on the C# CLI; return its exit code."""
    target = project or cli_project()
    name = args[0] if args else "rig"
    if not (target / "Rig.Cli.csproj").is_file():
        _ = sys.stderr.write(
            f"rig {name}: this command now runs on the C# rig CLI, and {target} is not here. Run it from a rig clone.\n"
        )
        return 2
    try:
        return runner(argv(target, args), {BRIDGE_VARIABLE: "1"})
    except FileNotFoundError:
        _ = sys.stderr.write(f"rig {name}: this command now runs on the C# rig CLI, which needs dotnet on PATH.\n")
        return 2


def add_stub(app: typer.Typer, name: str, handover: Callable[[list[str]], int] | None = None) -> None:
    """Register *name* on *app* as a stub that hands the whole line, ``--help`` included, to the C# CLI."""
    send: Callable[[list[str]], int] = handover or run

    def stub(ctx: typer.Context) -> None:
        raise typer.Exit(code=send([name, *ctx.args]))

    app.command(
        name,
        context_settings={"allow_extra_args": True, "ignore_unknown_options": True, "help_option_names": list[str]()},
        help="Runs on the C# rig CLI.",
    )(stub)
