"""Find the git repos under a code root, cheaply, and remember where they are.

The scan is bounded two levels deep and **stops descending the moment a repo is
found**. That is what keeps it cheap: a code root holding twenty repos costs
twenty-odd `stat` calls rather than a walk of every `node_modules` beneath them.

Worktrees are excluded two ways, and both are needed. The convention puts them
at `<repo>/temp/<name>`, inside a repo, where the halt-at-the-clone rule already
skips them. But one can be placed anywhere — `Parquet.SourceGenerator-golden` is
a worktree of `Parquet.SourceGenerator` living as its sibling — so `is_repo`
also requires `.git` to be a *directory*. A linked worktree carries a `.git`
file. Without that second test the sibling was counted as a clone and its 118
local-only commits were added to the estate total twice.

The result is cached under the home directory, keyed by root, with a TTL. Repos
are created and cloned rarely; paying for a filesystem walk on every invocation
of an audit is the kind of cost that stops people running it.
"""

from __future__ import annotations

import datetime as dt
import json
import pathlib
from dataclasses import dataclass

from rig.typed_json import JsonValue, try_decode, try_decode_json

CACHE_FILE = pathlib.Path.home() / ".estate-repos.json"
DEFAULT_TTL = dt.timedelta(hours=12)
DEFAULT_MAX_DEPTH = 2

#: Never descended into. Cheap to skip and never a code root.
_SKIP = frozenset(
    {
        ".git",
        "node_modules",
        ".venv",
        "venv",
        "__pycache__",
        ".mypy_cache",
        ".pytest_cache",
        ".ruff_cache",
        "dist",
        "build",
        ".next",
        "target",
        "obj",
        "bin",
        "Library",
        ".Trash",
    }
)


def is_repo(path: pathlib.Path) -> bool:
    """Return True if *path* is a clone in its own right.

    Requires `.git` to be a **directory**. A linked worktree — and a submodule —
    carries a `.git` *file* holding a `gitdir:` pointer, and neither is a clone.

    The module docstring used to claim worktrees are never reached because the
    scan halts at the clone. That is only true of worktrees placed *inside* it.
    `Parquet.SourceGenerator-golden` is a worktree of `Parquet.SourceGenerator`
    living as its **sibling**, so it was discovered as a clone of its own and
    double-counted 118 local-only commits into the estate total.
    """
    return (path / ".git").is_dir()


def scan(root: pathlib.Path, *, max_depth: int = DEFAULT_MAX_DEPTH) -> list[pathlib.Path]:
    """Return repo roots under *root*, at most *max_depth* levels down.

    Depth counts directories below *root*: a repo at ``root/foo`` is depth 1.
    *root* itself is tested first, and a repo there ends the scan — there is
    nothing to look for inside a checkout.
    """
    root = root.expanduser().resolve()
    if not root.is_dir():
        return []
    if is_repo(root):
        return [root]

    found: list[pathlib.Path] = []
    frontier = [root]
    for _ in range(max_depth):
        next_frontier: list[pathlib.Path] = []
        for parent in frontier:
            for child in _children(parent):
                if is_repo(child):
                    found.append(child)  # and do not descend
                else:
                    next_frontier.append(child)
        frontier = next_frontier
        if not frontier:
            break
    return sorted(found)


def _children(parent: pathlib.Path) -> list[pathlib.Path]:
    """Return visible, non-skipped subdirectories of *parent*. Never raises."""
    try:
        entries = list(parent.iterdir())
    except (OSError, PermissionError):
        return []
    return [entry for entry in entries if entry.is_dir() and not entry.name.startswith(".") and entry.name not in _SKIP]


@dataclass(frozen=True, slots=True)
class _CacheEntry:
    """One root's entry in the cache file, as written by `CachedScan.to_json`."""

    scanned_at: str
    repos: list[str]
    root: str = ""


@dataclass(frozen=True)
class CachedScan:
    """One root's scan result, as cached."""

    root: str
    scanned_at: dt.datetime
    repos: tuple[str, ...]

    def is_fresh(self, ttl: dt.timedelta, *, now: dt.datetime | None = None) -> bool:
        """Return True if this entry is still within *ttl*."""
        moment = now or dt.datetime.now(dt.UTC)
        return moment - self.scanned_at < ttl

    def paths(self) -> list[pathlib.Path]:
        return [pathlib.Path(p) for p in self.repos]

    def to_json(self) -> dict[str, object]:
        return {"root": self.root, "scanned_at": self.scanned_at.isoformat(), "repos": list(self.repos)}

    @classmethod
    def from_json(cls, raw: JsonValue) -> CachedScan | None:
        """Parse one cache entry, returning None rather than raising on junk."""
        entry = try_decode(_CacheEntry, raw)
        if entry is None:
            return None
        try:
            scanned = dt.datetime.fromisoformat(entry.scanned_at)
        except ValueError:
            return None
        if scanned.tzinfo is None:
            scanned = scanned.replace(tzinfo=dt.UTC)
        return cls(root=entry.root, scanned_at=scanned, repos=tuple(entry.repos))


def _read_cache(cache_file: pathlib.Path) -> dict[str, CachedScan]:
    """Load the whole cache. A corrupt file is treated as empty, never fatal."""
    if not cache_file.is_file():
        return {}
    try:
        raw = try_decode_json(dict[str, JsonValue], cache_file.read_text(encoding="utf-8"))
    except OSError:
        return {}
    if raw is None:
        return {}
    out: dict[str, CachedScan] = {}
    for key, value in raw.items():
        entry = CachedScan.from_json(value)
        if entry is not None:
            out[key] = entry
    return out


def _write_cache(cache_file: pathlib.Path, entries: dict[str, CachedScan]) -> None:
    """Persist the cache, ignoring a write failure — a cache is not the point."""
    try:
        cache_file.parent.mkdir(parents=True, exist_ok=True)
        payload = {key: entry.to_json() for key, entry in entries.items()}
        cache_file.write_text(json.dumps(payload, indent=2) + "\n", encoding="utf-8")
    except OSError:
        return


@dataclass(frozen=True, slots=True)
class Discovered:
    """The repo roots found under a scan root, and whether they came from the cache."""

    paths: list[pathlib.Path]
    from_cache: bool


def repos(
    root: pathlib.Path,
    *,
    ttl: dt.timedelta = DEFAULT_TTL,
    refresh: bool = False,
    cache_file: pathlib.Path = CACHE_FILE,
    max_depth: int = DEFAULT_MAX_DEPTH,
    now: dt.datetime | None = None,
) -> Discovered:
    """Return *root*'s repo roots, and whether they came from the cache.

    Rescans when the entry is missing, stale, or *refresh* is set. Paths that
    have since disappeared are dropped from a cached answer without forcing a
    full rescan — a repo being deleted is far more common than one appearing,
    and the stale entry would otherwise report findings for a path that is gone.
    """
    key = str(root.expanduser().resolve())
    cache = _read_cache(cache_file)
    entry = cache.get(key)

    if not refresh and entry is not None and entry.is_fresh(ttl, now=now):
        live = [path for path in entry.paths() if path.is_dir()]
        return Discovered(paths=live, from_cache=True)

    found = scan(root, max_depth=max_depth)
    cache[key] = CachedScan(
        root=key,
        scanned_at=now or dt.datetime.now(dt.UTC),
        repos=tuple(str(p) for p in found),
    )
    _write_cache(cache_file, cache)
    return Discovered(paths=found, from_cache=False)
