"""Where the estate is: the code root, its clones, and paths rendered for a table.

Shared by the `estate` command modules. A leaf: it imports no command module,
so any of them can use it without an import cycle.
"""

from __future__ import annotations

import datetime as dt
import pathlib

from rich.console import Console

from rig import proc, roots, tracing
from rig.estate import discovery


def code_root(explicit: str | None) -> pathlib.Path:
    """Return the folder to scan: an explicit path, else the personal folder (`rig.roots`)."""
    if explicit:
        return pathlib.Path(explicit).expanduser().resolve()
    return roots.personal_dir().resolve()


def ago(delta: dt.timedelta | None) -> str:
    if delta is None:
        return "just now"
    hours = int(delta.total_seconds() // 3600)
    if hours < 1:
        return f"{int(delta.total_seconds() // 60)}m ago"
    return f"{hours}h ago" if hours < 48 else f"{hours // 24}d ago"


def clones(root: pathlib.Path, *, refresh: bool, quiet: bool, err: Console) -> list[pathlib.Path]:
    """Resolve the clone list, reporting cache provenance to *err*."""
    with tracing.span("estate.discover", scope="rig.estate", **{"estate.root": str(root)}) as s:
        discovered = discovery.repos(root, refresh=refresh)
        s.set(**{"estate.clones": len(discovered.paths), "estate.from_cache": discovered.from_cache})
    if not quiet:
        age = discovery.cache_age(root)
        provenance = f"cached {ago(age)}" if discovered.from_cache and age else "freshly scanned"
        err.print(f"[dim]{len(discovered.paths)} clones under {root} ({provenance})[/dim]")
    return discovered.paths


def short(path: pathlib.Path | str, clone: pathlib.Path) -> str:
    """Render *path* relative to *clone* when it is inside it, else in full.

    A worktree can live outside its clone, so `relative_to` can raise here.
    """
    candidate = pathlib.Path(path)
    try:
        return candidate.relative_to(clone).as_posix()
    except ValueError:
        return str(candidate)


def one_clone(path: str | None) -> pathlib.Path:
    """Resolve a single clone: the given path, or the repo containing the cwd."""
    if path:
        return pathlib.Path(path).expanduser().resolve()
    top = proc.git_out("rev-parse", "--show-toplevel")
    return pathlib.Path(top) if top else pathlib.Path.cwd()
