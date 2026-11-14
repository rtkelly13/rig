"""Legacy task target: delegate to the published windowless C# coordinator.

New tasks start rigw directly. Keep this adapter while older task registrations
still reference the module through runtime/current; it contains no scheduler.
"""

from __future__ import annotations

import subprocess
import sys
from collections.abc import Callable, Sequence
from pathlib import Path

from desktop_manager import runtime
from rig import tracing

type Runner = Callable[[list[str]], int]


def _native(command: list[str]) -> int:
    return tracing.run(
        command,
        capture_output=True,
        stdin=subprocess.DEVNULL,
        creationflags=subprocess.CREATE_NO_WINDOW if sys.platform == "win32" else 0,
    ).returncode


def main(argv: Sequence[str] | None = None, *, runner: Runner = _native) -> int:
    """Keep the original trigger and extra arguments, returning rigw's exit code."""
    arguments = list(sys.argv[1:] if argv is None else argv)
    root = Path(__file__).resolve().parents[3]
    publish = root / "rig" if runtime.running_copy(__file__) else root / "temp" / "jobs-runtime"
    command = [str(publish / "rigw.exe"), "jobs", "run", "--scheduled"]
    if arguments:
        command.extend(["--trigger", *arguments])
    try:
        return runner(command)
    except FileNotFoundError:
        return 127


if __name__ == "__main__":
    raise SystemExit(main())
