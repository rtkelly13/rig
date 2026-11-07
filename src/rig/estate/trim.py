"""Regenerable output inside worktrees nobody has used for a week.

Worktrees pile up faster than branches land, and each one carries its own
`node_modules`, `.venv` and build output. `tidy` removes a worktree once its
branch is provably finished; until then the worktree stays, but the bulk inside
it comes back from `pnpm install`, `uv sync` or a build, so it need not stay.

**Idle is judged from git's own bookkeeping**, not from file timestamps: the
newest of the worktree's `index`, `HEAD` and `logs/HEAD` moves on every commit,
checkout, stage and status refresh, while a build rewrites `node_modules` and
`obj` long after anyone looked at the branch. A worktree whose bookkeeping
cannot be read counts as active, so it is never trimmed.

**Each directory proves its own safety.** Whatever its name, it is refused when:

- git does not ignore it, or tracks a file inside it;
- it resolves outside its worktree (a symlink or junction);
- a `.git` lives anywhere below it, i.e. a checkout parked inside it.

`bin` and `obj` count only next to a .NET project file, `packages` only next to
a `.sln`, and `target` only next to `Cargo.toml`, so a hand-written `bin/` of
scripts is never mistaken for build output. Locked worktrees, the main checkout
and the worktree's own `temp/` (often the only copy of a session's output) are
never touched.
"""

from __future__ import annotations

import contextlib
import functools
import os
import pathlib
import shutil
import stat
import subprocess
import time
from collections.abc import Callable, Iterator
from dataclasses import dataclass

from rig import proc
from rig.estate.checks import list_worktrees
from rig.estate.disksize import Measure, measure

DEFAULT_IDLE_DAYS = 7
DEFAULT_FLOOR = 10 * 1024 * 1024
MAX_DEPTH = 3

#: Directory name → category, matched anywhere within `MAX_DEPTH`.
_BY_NAME: dict[str, str] = {
    "node_modules": "node_modules",
    ".venv": "python venv",
    "venv": "python venv",
    "dist": "js build output",
    ".next": "js build output",
    ".turbo": "js build output",
    "storybook-static": "js build output",
    ".pytest_cache": "tool caches",
    ".mypy_cache": "tool caches",
    ".ruff_cache": "tool caches",
    ".tox": "tool caches",
    ".nox": "tool caches",
}
_DOTNET_DIRS = frozenset({"bin", "obj"})
_DOTNET_PROJECTS: tuple[str, ...] = ("*.csproj", "*.fsproj", "*.vbproj")
_NEVER_DESCEND = frozenset({".git", ".worktrees", "temp"})


@dataclass(frozen=True, slots=True)
class Trimmable:
    """One directory inside an idle worktree, and whether it may go."""

    worktree: pathlib.Path
    path: pathlib.Path
    category: str
    size: Measure
    #: Why it may not be removed; empty when it may.
    refused: str = ""


def category_of(path: pathlib.Path) -> str | None:
    """The regenerable category *path* belongs to, or None when it is not one."""
    name, parent = path.name, path.parent
    if name in _BY_NAME:
        return _BY_NAME[name]
    if name in _DOTNET_DIRS and any(next(parent.glob(p), None) for p in _DOTNET_PROJECTS):
        return "dotnet build output"
    if name == "packages" and next(parent.glob("*.sln"), None):
        return "dotnet build output"
    if name == "target" and (parent / "Cargo.toml").is_file():
        return "rust build output"
    return None


def _is_link(path: pathlib.Path) -> bool:
    return path.is_symlink() or path.is_junction()


@dataclass(frozen=True, slots=True)
class _Found:
    path: pathlib.Path
    category: str


def _walk(root: pathlib.Path) -> Iterator[_Found]:
    """Every candidate directory within `MAX_DEPTH` of *root*, without entering one."""
    stack = [(root, 0)]
    while stack:
        current, depth = stack.pop()
        try:
            children = sorted(p for p in current.iterdir() if p.is_dir())
        except OSError:
            continue
        for child in children:
            if child.name in _NEVER_DESCEND or _is_link(child):
                continue
            category = category_of(child)
            if category:
                yield _Found(child, category)
            elif depth + 1 < MAX_DEPTH:
                stack.append((child, depth + 1))


def _git_lines(cwd: pathlib.Path, args: list[str]) -> list[str] | None:
    """Output lines of a git command; None when git failed (1 is a valid "no match")."""
    ran = proc.git(*args, cwd=cwd, timeout=60)
    if ran.code not in {0, 1}:
        return None
    return [line.strip().rstrip("/") for line in ran.out.splitlines() if line.strip()]


def refusal(root: pathlib.Path, path: pathlib.Path, *, ignored: bool) -> str:
    """Why *path* may not be removed from the checkout at *root*; empty when it may."""
    try:
        _ = path.resolve().relative_to(root.resolve())
    except ValueError:
        return "resolves outside the worktree"
    tracked = _git_lines(root, ["ls-files", "--", path.relative_to(root).as_posix()])
    if tracked is None or tracked:
        return "contains tracked files"
    if not ignored:
        return "not gitignored"
    return ""


def judge(root: pathlib.Path, found: list[_Found], *, floor: int) -> list[Trimmable]:
    """Measure and rule on each candidate directory under *root*."""
    rels = [f.path.relative_to(root).as_posix() for f in found]
    ignored = set(_git_lines(root, ["check-ignore", "--", *rels]) or []) if rels else set[str]()
    out: list[Trimmable] = []
    for item, rel in zip(found, rels, strict=True):
        size = measure(item.path)
        if size.holds_checkout:
            out.append(Trimmable(root, item.path, item.category, size, "holds a git checkout"))
            continue
        if size.freeable < floor:
            continue
        reason = refusal(root, item.path, ignored=rel in ignored)
        out.append(Trimmable(root, item.path, item.category, size, reason))
    return out


def last_activity(worktree: pathlib.Path) -> float:
    """When anyone last used *worktree* (epoch seconds), from git's admin files for it.

    Unreadable bookkeeping reads as "now", so a worktree we cannot judge is never idle.
    """
    marker = worktree / ".git"
    try:
        if marker.is_file():
            pointer = marker.read_text(encoding="utf-8").split("gitdir:", 1)[1].strip()
            admin = pathlib.Path(pointer)
            if not admin.is_absolute():
                admin = worktree / admin
        else:
            admin = marker
    except (OSError, IndexError, UnicodeDecodeError):
        return time.time()
    stamps: list[float] = []
    for name in ("index", "HEAD", "logs/HEAD"):
        try:
            stamps.append((admin / name).stat().st_mtime)
        except OSError:
            continue
    return max(stamps) if stamps else time.time()


def idle_worktrees(clone: pathlib.Path, *, idle_days: int, now: float) -> list[pathlib.Path]:
    """Linked worktrees of *clone* that are unlocked, present, and idle *idle_days* or more."""
    out: list[pathlib.Path] = []
    for tree in list_worktrees(clone):
        if tree.is_main or tree.bare or tree.locked or tree.prunable or not tree.path.is_dir():
            continue
        if now - last_activity(tree.path) >= idle_days * 86400:
            out.append(tree.path)
    return out


def scan(
    clone: pathlib.Path,
    *,
    idle_days: int = DEFAULT_IDLE_DAYS,
    floor: int = DEFAULT_FLOOR,
    now: float | None = None,
) -> list[Trimmable]:
    """Regenerable directories inside *clone*'s idle worktrees, biggest first."""
    moment = time.time() if now is None else now
    out: list[Trimmable] = []
    for worktree in idle_worktrees(clone, idle_days=idle_days, now=moment):
        out += judge(worktree, list(_walk(worktree)), floor=floor)
    return sorted(out, key=lambda t: -t.size.freeable)


def _registration_refusal(worktree: pathlib.Path) -> str:
    """Why git's registration of *worktree* now rules it out, as `idle_worktrees` would; empty when it does not.

    A lock does not touch the activity files `last_activity` reads, so it has to be re-read here.
    """
    target = worktree.resolve()
    tree = next((t for t in list_worktrees(worktree) if t.path.resolve() == target), None)
    if tree is None:
        return "worktree is no longer registered"
    if tree.locked:
        return "worktree is locked"
    if tree.is_main or tree.bare or tree.prunable:
        return "no longer a linked worktree"
    return ""


def recheck(item: Trimmable, *, idle_days: int = DEFAULT_IDLE_DAYS, now: float | None = None) -> str:
    """Why *item*, found by an earlier `scan`, may no longer be removed; empty when it still may.

    Runs every test `scan` ran against the disk as it is now, so a directory that
    became a link, gained a checkout or tracked file, or whose worktree was locked,
    unregistered or used again since the scan is refused rather than deleted.
    """
    path, worktree = item.path, item.worktree
    if _is_link(path):
        return "is a link"
    if not path.is_dir():
        return "no longer exists"
    if reason := _registration_refusal(worktree):
        return reason
    if category_of(path) != item.category:
        return "no longer regenerable output"
    moment = time.time() if now is None else now
    if moment - last_activity(worktree) < idle_days * 86400:
        return "worktree is in use again"
    if measure(path).holds_checkout:
        return "holds a git checkout"
    try:
        rel = path.relative_to(worktree).as_posix()
    except ValueError:
        return "resolves outside the worktree"
    ignored = _git_lines(worktree, ["check-ignore", "--", rel])
    return refusal(worktree, path, ignored=ignored is not None and rel in ignored)


def _clear_readonly(func: Callable[[str], object], path: str, _exc: BaseException) -> None:
    """Retry a delete that failed on a read-only file, or on a file whose ACL denies delete."""
    with contextlib.suppress(PermissionError):
        pathlib.Path(path).chmod(stat.S_IWRITE)
    try:
        _ = func(path)
    except PermissionError:
        if os.name != "nt" or func not in (os.unlink, os.remove):
            raise
        _allow_delete_child(str(pathlib.Path(path).parent))
        _ = func(path)


@functools.cache
def _user_sid() -> str:
    try:
        done = subprocess.run(["whoami", "/user", "/fo", "csv", "/nh"], capture_output=True, text=True, check=True)
    except subprocess.SubprocessError as exc:
        raise OSError(f"whoami: {exc}") from exc
    return done.stdout.strip().split(",")[-1].strip('"')


@functools.cache
def _allow_delete_child(directory: str) -> None:
    """Grant the current user delete-child on *directory*.

    A uv `.venv` hardlinks files from uv's cache, and some carry an ACL that
    denies the user delete. Changing that ACL would change every link, the
    cache's included; delete-child on the parent allows the unlink instead and
    touches only the tree being removed.
    """
    _ = subprocess.run(["icacls", directory, "/grant", f"*{_user_sid()}:(DC)"], capture_output=True, check=False)


#: git for anything that deletes or moves files. Windows git stops at 260
#: characters without `core.longpaths`, part-way through a tree: a worktree
#: holding `node_modules` is unregistered first and then left half-deleted.
#: Prepended to git writes, so a write never stops part-way through a tree with long paths.
LONG_PATHS: tuple[str, ...] = ("-c", "core.longpaths=true")


def long_path(path: pathlib.Path) -> str:
    """Return *path* absolute, with the long-path prefix on Windows that lifts the 260-character limit."""
    target = str(path.absolute())
    if os.name == "nt" and not target.startswith("\\\\"):
        target = "\\\\?\\" + target
    return target


def remove_tree(path: pathlib.Path) -> None:
    """Delete *path* and everything under it, read-only and over-long paths included. Raises `OSError`."""
    try:
        shutil.rmtree(long_path(path), onexc=_clear_readonly)
    except PermissionError:
        if os.name != "nt":
            raise
        _unlock_owner_dirs(path)
        shutil.rmtree(long_path(path), onexc=_clear_readonly)


def _unlock_owner_dirs(root: pathlib.Path) -> None:
    """Reset the ACL of each directory under *root* that its owner cannot list.

    Python 3.13.0's `os.mkdir(mode=0o700)` wrote a protected ACL granting only
    SYSTEM and Administrators, so pytest's `.pytest_cache` locks out the user
    who made it. The owner can still rewrite the ACL. Links are never entered:
    a pnpm junction points into a store shared by every clone.
    """
    stack = [long_path(root)]
    while stack:
        current = stack.pop()
        try:
            entries = list(os.scandir(current))
        except PermissionError:
            _ = subprocess.run(["icacls", current, "/reset", "/q"], capture_output=True, check=False)
            try:
                entries = list(os.scandir(current))
            except OSError:
                continue
        except OSError:
            continue
        stack.extend(e.path for e in entries if e.is_dir(follow_symlinks=False) and not e.is_junction())


def remove(item: Trimmable) -> str:
    """Delete one candidate, returning `removed`, `refused` or `failed: <reason>`."""
    if item.refused:
        return "refused"
    try:
        remove_tree(item.path)
    except OSError as exc:
        return f"failed: {exc}"
    return "removed"
