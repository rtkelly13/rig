"""Which directories some process is working in, read once and asked many times.

A worktree with a shell, an agent session or an Orca terminal sitting in it is in
use however finished its branch looks. Removing it pulls the directory out from
under that process. Measured on this estate on 2026-11-06: `estate tidy --merged`
would have removed 62 worktrees, 8 of which had live Orca terminals, 7 used that
day.

The process working directory is the signal rather than Orca's terminal list,
because it also covers a plain shell or a `claude` session started outside Orca,
and it needs no running app. One `lsof` call answers for every path in the run:
it takes seconds, so it is not something to repeat per worktree.
"""

from __future__ import annotations

import pathlib
import shutil
import sys

from rig import proc

LSOF_TIMEOUT = 60.0


def working_dirs() -> frozenset[pathlib.Path] | None:
    """Every process's working directory, or None when that cannot be read.

    None (no `lsof`, Windows, a failure or timeout) is not "nothing is busy":
    callers that would remove something must treat an unchecked path as in use.
    """
    lsof = shutil.which("lsof")
    if lsof is None or sys.platform == "win32":
        return None
    ran = proc.run([lsof, "-n", "-d", "cwd", "-Fn"], timeout=LSOF_TIMEOUT)
    # lsof exits 1 when some process could not be read, yet still prints the rest.
    if not ran.ok and not ran.out:
        return None
    return frozenset(pathlib.Path(line[1:]) for line in ran.out.splitlines() if line.startswith("n/"))


def in_use(path: pathlib.Path, dirs: frozenset[pathlib.Path]) -> bool:
    """Whether any of *dirs* is *path* or lies below it."""
    try:
        target = path.resolve()
    except OSError:
        target = path
    return any(cwd == target or target in cwd.parents for cwd in dirs)
