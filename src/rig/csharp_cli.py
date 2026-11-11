"""The entry point of every rig script but ``desktop-manager``: each starts the C# rig CLI.

rig runs on C# (#85). ``uv run rig …``, ``uv run workspace …``, ``uv run latex …`` and the rest hand their
line to ``dotnet run --project <this checkout>/src/Rig.Cli -- …``, attached to this console, with the exit
code returned unchanged. The project is found beside this file, so the C# CLI and the Python commands it
still forwards (its ``Forwarded`` table) always come from the same tree.
"""

from __future__ import annotations

import os
import sys
from collections.abc import Callable, Sequence
from contextvars import ContextVar
from pathlib import Path

from rig import tracing

#: The rig checkout the C# CLI runs Python tools from (mdformat, forwarded commands): set to this one, so both
#: halves and their uv environment come from the same tree.
HOME_VARIABLE = "RIG_HOME"

#: Runs an attached command with extra environment and returns its exit code.
type Runner = Callable[[list[str], dict[str, str]], int]

#: The runner :func:`run` uses when given none: the test suite sets one, so a delegated command never starts dotnet.
RUNNER: ContextVar[Runner | None] = ContextVar("rig_csharp_runner", default=None)


def cli_project(here: Path | None = None) -> Path:
    """``src/Rig.Cli`` in the checkout this module was loaded from."""
    module = (here or Path(__file__)).resolve()
    return module.parents[2] / "src" / "Rig.Cli"


def argv(project: Path, args: Sequence[str]) -> list[str]:
    """The command line that runs *args* on the C# CLI built from *project*."""
    return ["dotnet", "run", "--project", str(project), "--", *args]


def _attached(command: list[str], env: dict[str, str]) -> int:
    return tracing.run(command, scope="rig.csharp_cli", env={**os.environ, **env}).returncode


def run(args: Sequence[str], *, project: Path | None = None, runner: Runner | None = None) -> int:
    """Run *args* (a rig command line) on the C# CLI; return its exit code."""
    send = runner or RUNNER.get() or _attached
    target = project or cli_project()
    name = args[0] if args else "rig"
    if not (target / "Rig.Cli.csproj").is_file():
        _ = sys.stderr.write(
            f"rig {name}: this command now runs on the C# rig CLI, and {target} is not here. Run it from a rig clone.\n"
        )
        return 2
    try:
        return send(argv(target, args), {HOME_VARIABLE: str(target.parents[1])})
    except FileNotFoundError:
        _ = sys.stderr.write(f"rig {name}: this command now runs on the C# rig CLI, which needs dotnet on PATH.\n")
        return 2


def entry(argv0_and_args: Sequence[str] | None = None, handover: Callable[[list[str]], int] = run) -> None:
    """A rig script (``uv run latex …``): run ``rig <its name> …`` on the C# CLI, keeping the exit code.

    The name is the invoked script's stem, so ``latex`` and Windows' ``latex.exe`` both hand over as ``rig latex``.
    The ``rig`` script is the C# CLI's own front door, so its line goes over as typed.
    """
    line = list(sys.argv if argv0_and_args is None else argv0_and_args)
    name = Path(line[0]).stem
    raise SystemExit(handover(line[1:] if name == "rig" else [name, *line[1:]]))
