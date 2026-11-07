"""Caches that refill themselves: prune them, and say how much each one gave back.

Five categories, each owned by its own tool:

| category | command | measured by |
|---|---|---|
| uv cache | `uv cache prune` | walking `uv cache dir` before and after |
| pnpm store | `pnpm store prune` | walking `pnpm store path` before and after |
| nuget http-cache | `dotnet nuget locals http-cache --clear` | walking the listed http-cache |
| claude temp | deletes Claude Code session dirs idle `idle_days`+ | the dirs themselves |
| docker build cache | `docker builder prune -f --filter until=168h` | Docker's own `Total:` line |

Every tool is optional. A tool missing from PATH is skipped with that reason,
and so is Docker when its daemon is not running. Scheduled tasks start with a
thinner PATH than a terminal, so each tool is also looked for in the places its
installer puts it (`find_tool`).

A dry run runs only the read-only commands that locate each cache. `freed` is
filled in for a dry run only where it is knowable without pruning: the NuGet
http-cache is cleared outright, and the idle Claude sessions are listed exactly.
"""

from __future__ import annotations

import os
import pathlib
import re
import shutil
import subprocess
import tempfile
import time
from collections.abc import Sequence
from dataclasses import dataclass
from enum import StrEnum

from rig.estate.disksize import footprint
from rig.estate.trim import remove_tree

DEFAULT_IDLE_DAYS = 7
PROBE_TIMEOUT = 60
PRUNE_TIMEOUT = 15 * 60
DOCKER_FILTER = "until=168h"
TIMED_OUT = 124
NOT_STARTED = 127

_SESSION_NAME = re.compile(r"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", re.IGNORECASE)
_DOCKER_TOTAL = re.compile(r"^Total(?: reclaimed space)?:\s*([\d.]+)\s*([kKMGT]?i?B)\s*$", re.MULTILINE)
_DOCKER_UNITS = {"B": 1, "KB": 1000, "MB": 1000**2, "GB": 1000**3, "TB": 1000**4}


class Status(StrEnum):
    WOULD_RUN = "would run"
    DONE = "done"
    SKIPPED = "skipped"
    FAILED = "failed"


@dataclass(frozen=True, slots=True)
class Pruned:
    """What one category's prune did, or would do."""

    category: str
    command: str
    status: Status
    reason: str = ""
    #: Bytes in the cache before pruning; None when it cannot be measured from here (Docker).
    size_before: int | None = None
    #: Bytes freed; under a dry run, bytes that would be freed where that is knowable.
    freed: int | None = None


@dataclass(frozen=True, slots=True)
class Completed:
    """The parts of a finished command this module reads."""

    returncode: int
    stdout: str
    stderr: str


def run(argv: Sequence[str], *, timeout: int = PROBE_TIMEOUT) -> Completed:
    """Run *argv* with no stdin, capturing text output. Never raises."""
    try:
        done = subprocess.run(
            list(argv),
            stdin=subprocess.DEVNULL,
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="replace",
            check=False,
            timeout=timeout,
        )
    except subprocess.TimeoutExpired:
        return Completed(TIMED_OUT, "", f"timed out after {timeout}s")
    except OSError as exc:
        return Completed(NOT_STARTED, "", str(exc))
    return Completed(done.returncode, done.stdout or "", done.stderr or "")


def _install_dirs() -> list[pathlib.Path]:
    """Where the installers of these tools put them, for a PATH that lacks them."""
    home = pathlib.Path.home()
    local = pathlib.Path(os.environ.get("LOCALAPPDATA") or home / "AppData" / "Local")
    roaming = pathlib.Path(os.environ.get("APPDATA") or home / "AppData" / "Roaming")
    program_files = pathlib.Path(os.environ.get("PROGRAMFILES") or r"C:\Program Files")
    return [
        home / ".local" / "bin",
        home / ".cargo" / "bin",
        local / "Microsoft" / "WinGet" / "Links",
        local / "pnpm",
        roaming / "npm",
        program_files / "dotnet",
        program_files / "Docker" / "Docker" / "resources" / "bin",
    ]


def find_tool(name: str) -> str | None:
    """*name* on PATH, else in its installer's usual directory; None when neither has it."""
    return shutil.which(name) or shutil.which(name, path=os.pathsep.join(str(d) for d in _install_dirs()))


def _last_line(text: str) -> str:
    lines = [line.strip() for line in text.splitlines() if line.strip()]
    return lines[-1] if lines else ""


def _failure(done: Completed) -> str:
    return _last_line(done.stderr) or _last_line(done.stdout) or f"exit {done.returncode}"


def _size(directory: pathlib.Path) -> int:
    return footprint(directory).bytes_used if directory.is_dir() else 0


@dataclass(frozen=True, slots=True)
class ToolCache:
    """A cache whose tool can print where it lives and prune it."""

    category: str
    tool: str
    locate: tuple[str, ...]
    prune: tuple[str, ...]
    #: Text before the path in the locate output, e.g. `http-cache:`.
    prefix: str = ""
    #: The prune empties the cache, so a dry run knows exactly what it would free.
    clears_all: bool = False

    @property
    def command(self) -> str:
        return " ".join((self.tool, *self.prune))


UV = ToolCache("uv cache", "uv", ("cache", "dir"), ("cache", "prune"))
PNPM = ToolCache("pnpm store", "pnpm", ("store", "path"), ("store", "prune"))
NUGET = ToolCache(
    "nuget http-cache",
    "dotnet",
    ("nuget", "locals", "http-cache", "--list"),
    ("nuget", "locals", "http-cache", "--clear"),
    prefix="http-cache:",
    clears_all=True,
)
TOOL_CACHES = (UV, PNPM, NUGET)


def prune_tool_cache(cache: ToolCache, *, apply: bool) -> Pruned:
    """Locate *cache*, measure it, and under *apply* prune it and measure again."""
    exe = find_tool(cache.tool)
    if exe is None:
        return Pruned(cache.category, cache.command, Status.SKIPPED, f"{cache.tool} not found on PATH")
    located = run([exe, *cache.locate])
    where = _last_line(located.stdout).removeprefix(cache.prefix).strip()
    if located.returncode != 0 or not where:
        return Pruned(cache.category, cache.command, Status.FAILED, f"cannot locate the cache: {_failure(located)}")
    directory = pathlib.Path(where)
    before = _size(directory)
    if not apply:
        freed = before if cache.clears_all else None
        return Pruned(cache.category, cache.command, Status.WOULD_RUN, str(directory), before, freed)
    done = run([exe, *cache.prune], timeout=PRUNE_TIMEOUT)
    if done.returncode != 0:
        return Pruned(cache.category, cache.command, Status.FAILED, _failure(done), before)
    return Pruned(cache.category, cache.command, Status.DONE, str(directory), before, max(before - _size(directory), 0))


def docker_reclaimed(output: str) -> int | None:
    """Bytes from the `Total:` line `docker builder prune` ends with; None when absent."""
    match = _DOCKER_TOTAL.search(output)
    if match is None:
        return None
    unit = match.group(2).upper().replace("I", "")
    return int(float(match.group(1)) * _DOCKER_UNITS.get(unit, 1))


def prune_docker(*, apply: bool) -> Pruned:
    """Build cache older than a week, when Docker is installed and its daemon answers."""
    category = "docker build cache"
    command = f"docker builder prune -f --filter {DOCKER_FILTER}"
    exe = find_tool("docker")
    if exe is None:
        return Pruned(category, command, Status.SKIPPED, "docker not found on PATH")
    if run([exe, "info", "--format", "{{.ServerVersion}}"]).returncode != 0:
        return Pruned(category, command, Status.SKIPPED, "Docker daemon not running")
    if not apply:
        return Pruned(category, command, Status.WOULD_RUN, "size is known only after the prune")
    done = run([exe, "builder", "prune", "-f", "--filter", DOCKER_FILTER], timeout=PRUNE_TIMEOUT)
    if done.returncode != 0:
        return Pruned(category, command, Status.FAILED, _failure(done))
    return Pruned(category, command, Status.DONE, freed=docker_reclaimed(done.stdout) or 0)


def default_claude_temp() -> pathlib.Path:
    """Where Claude Code keeps per-session scratch: `<temp>/claude/<project>/<session-id>`."""
    return pathlib.Path(tempfile.gettempdir()) / "claude"


@dataclass(frozen=True, slots=True)
class IdleSession:
    path: pathlib.Path
    bytes_used: int
    #: Epoch seconds of the last write: the newest file, or the dir itself when that is newer.
    last_write: float


def _subdirs(path: pathlib.Path) -> list[pathlib.Path]:
    try:
        return sorted(child for child in path.iterdir() if child.is_dir() and not child.is_symlink())
    except OSError:
        return []


def idle_sessions(root: pathlib.Path, *, idle_days: int, now: float) -> list[IdleSession]:
    """Session dirs under *root* nothing has written to for *idle_days* days.

    A session dir is named by its session id. The dir's own mtime counts as a
    write, so a session that has only just created its empty scratchpad is not idle.
    """
    cutoff = now - idle_days * 86400
    out: list[IdleSession] = []
    for project in _subdirs(root):
        for session in _subdirs(project):
            if not _SESSION_NAME.match(session.name):
                continue
            size = footprint(session)
            try:
                own = session.stat().st_mtime
            except OSError:
                continue
            last = max(size.newest, own)
            if last <= cutoff:
                out.append(IdleSession(session, size.bytes_used, last))
    return out


def prune_claude_temp(root: pathlib.Path, *, apply: bool, idle_days: int, now: float) -> Pruned:
    """Delete the Claude Code session dirs idle *idle_days* or more."""
    category = "claude temp"
    command = f"delete session dirs idle {idle_days}+ days under {root}"
    if not root.is_dir():
        return Pruned(category, command, Status.SKIPPED, f"{root} does not exist")
    sessions = idle_sessions(root, idle_days=idle_days, now=now)
    total = sum(s.bytes_used for s in sessions)
    if not apply:
        return Pruned(category, command, Status.WOULD_RUN, f"{len(sessions)} idle session dir(s)", total, total)
    freed = 0
    failures: list[str] = []
    for session in sessions:
        try:
            remove_tree(session.path)
        except OSError as exc:
            failures.append(f"{session.path.name}: {exc}")
            continue
        freed += session.bytes_used
    if failures:
        detail = f"{len(failures)} of {len(sessions)} not removed; first: {failures[0]}"
        return Pruned(category, command, Status.FAILED, detail, total, freed)
    return Pruned(category, command, Status.DONE, f"{len(sessions)} session dir(s) removed", total, freed)


def prune_all(
    *,
    apply: bool,
    claude_temp: pathlib.Path,
    idle_days: int = DEFAULT_IDLE_DAYS,
    now: float | None = None,
) -> list[Pruned]:
    """Every category, in a fixed order, each independent of the others."""
    moment = time.time() if now is None else now
    results = [prune_tool_cache(cache, apply=apply) for cache in TOOL_CACHES]
    results.append(prune_claude_temp(claude_temp, apply=apply, idle_days=idle_days, now=moment))
    results.append(prune_docker(apply=apply))
    return results
