"""Where the estate is: the code root, its clones, and paths rendered for a table.

Shared by the `estate` command modules. A leaf: it imports no command module,
so any of them can use it without an import cycle.
"""

from __future__ import annotations

import datetime as dt
import pathlib
from collections.abc import Sequence

import typer
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


def _toplevel(path: pathlib.Path) -> pathlib.Path | None:
    top = proc.git_out("rev-parse", "--path-format=absolute", "--show-toplevel", cwd=path)
    return pathlib.Path(top).resolve() if top else None


def _checkout(value: str, *, err: Console) -> pathlib.Path | None:
    """The clone behind *value* when it names a git checkout on disk; a worktree resolves to its clone.

    Never derived from where the git directory lives: `--separate-git-dir` puts it anywhere, and its
    parent can be an unrelated enclosing repo. The answer must itself be a working-tree root, or the
    value is refused, because `--apply` acts on whatever this returns.
    """
    candidate = pathlib.Path(value).expanduser()
    if not (candidate / ".git").exists():
        return None
    dirs = proc.git_out("rev-parse", "--path-format=absolute", "--git-dir", "--git-common-dir", cwd=candidate)
    pair = [pathlib.Path(d).resolve() for d in dirs.splitlines()]
    if len(pair) == 2 and pair[0] != pair[1]:
        # A linked worktree: the first `worktree list` entry names the main checkout.
        listing = proc.git_out("worktree", "list", "--porcelain", cwd=candidate)
        first = next(
            (line.removeprefix("worktree ") for line in listing.splitlines() if line.startswith("worktree ")), ""
        )
        chosen = pathlib.Path(first).resolve() if first else None
    else:
        chosen = _toplevel(candidate)
    if chosen is None or _toplevel(chosen) != chosen:
        err.print(f"[bold red]--repo {value}: cannot find the main working tree of this checkout.[/bold red]")
        raise typer.Exit(code=2)
    return chosen


def selected_clones(
    root: pathlib.Path, wanted: Sequence[str], *, refresh: bool, quiet: bool, err: Console
) -> list[pathlib.Path]:
    """The clones `--repo` names: a path to a checkout, or a clone under *root* by folder name.

    A path skips discovery entirely. A name matches a clone's folder name, or its path relative to
    *root* (`acme/playbook`), ignoring case. A name matching nothing, or more than one clone, is
    an error rather than a silent empty run.
    """
    chosen: list[pathlib.Path] = []
    names: list[str] = []
    for value in wanted:
        found = _checkout(value, err=err)
        if found is None:
            names.append(value)
        elif found not in chosen:
            chosen.append(found)
    if not names:
        return chosen

    known = clones(root, refresh=refresh, quiet=quiet, err=err)
    for name in names:
        key = name.strip().replace("\\", "/").strip("/").casefold()
        hits = [c for c in known if key in (c.name.casefold(), short(c, root).casefold())]
        if len(hits) != 1:
            problem = "matches no clone" if not hits else "is ambiguous: " + ", ".join(short(c, root) for c in hits)
            err.print(f"[bold red]--repo {name} {problem} under {root}.[/bold red] Pass a path, or --root.")
            raise typer.Exit(code=2)
        if hits[0] not in chosen:
            chosen.append(hits[0])
    return chosen
