"""The entry point of a rig command still written in Python, started by the C# rig CLI.

The C# CLI owns ``rig`` and forwards the commands it has not ported (its ``Forwarded`` table) to a Python program in
this checkout's uv environment: ``python -m rig.triage.cli …`` for these. Each such module ends with
``if __name__ == "__main__": main(app, "<name>")``, which traces the run the way the Python ``rig`` CLI did.
"""

from __future__ import annotations

import sys
from collections.abc import Sequence

import typer

from rig import tracing


def main(app: typer.Typer, name: str, argv: Sequence[str] | None = None) -> None:
    """Run *app* as ``rig <name>`` on *argv* (the process's arguments when none), inside a ``rig <name> …`` span."""
    args = list(sys.argv[1:] if argv is None else argv)
    tracing.init_tracing("rig")
    tracing.flush_periodically(30)
    command = " ".join([name, *(arg for arg in args[:1] if not arg.startswith("-"))])
    with tracing.span(f"rig {command}", scope="rig.cli", **{"cli.argv": [name, *args]}):
        app(args, prog_name=f"rig {name}")
