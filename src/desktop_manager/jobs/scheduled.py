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


def _native(
    command: list[str],
    *,
    runner: Callable[..., subprocess.CompletedProcess[str]] = tracing.run,
    platform: str = sys.platform,
) -> int:
    return runner(
        command,
        capture_output=True,
        stdin=subprocess.DEVNULL,
        creationflags=0x08000000 if platform == "win32" else 0,
    ).returncode


def main(
    argv: Sequence[str] | None = None,
    *,
    runner: Runner = _native,
    file: Path | str | None = None,
    running_copy: Callable[[str], bool] | None = None,
) -> int:
    """Keep the original trigger and extra arguments, returning rigw's exit code."""
    arguments = list(sys.argv[1:] if argv is None else argv)
    target_file = __file__ if file is None else str(file)
    is_installed = (running_copy or runtime.running_copy)(target_file)
    root = Path(target_file).resolve().parents[3]
    publish = root / "rig" if is_installed else root / "temp" / "jobs-runtime"
    command = [str(publish / "rigw.exe"), "jobs", "run", "--scheduled"]
    if arguments:
        command.extend(["--trigger", *arguments])
    try:
        return runner(command)
    except FileNotFoundError:
        return 127


if __name__ == "__main__":
    raise SystemExit(main())
