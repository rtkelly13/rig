"""The estate's JSON routes in `rig web`: the decision feed, disk headroom, and trimming idle worktrees.

| route | answer |
|---|---|
| `GET /api/ops/decisions` | `DecisionFeed`: the feed the `estate-decisions` job writes |
| `GET /api/ops/estate/disk` | `DiskUsage` of the drive holding the code root |
| `GET /api/ops/estate/trim?refresh=true` | `TrimScan`: the last finished scan; `refresh` starts a new one |
| `POST /api/ops/estate/trim/apply` | `list[TrimResult]` for `{"paths": [...]}` |

The decision feed is only read here: a missing file is normal before the job's first run, so it is an empty list with
a `note`, not an error.

Trimming takes the same action `estate trim --apply` does (`trim.scan` finds, `trim.remove` deletes). A scan walks
every idle worktree and takes a while, so it runs on a background thread and `TrimCache` keeps the last finished one;
a `GET` never waits for it. An apply removes only paths the last finished scan offered and did not refuse, and first
runs `trim.recheck` on each, so a directory that changed since the scan (became a link, gained a checkout, its
worktree was used again) is refused, not deleted. Afterwards the cache is stale and the next `GET` rescans.
"""

from __future__ import annotations

import datetime as dt
import shutil
import threading
from collections.abc import Callable, Iterable
from dataclasses import dataclass
from http.server import BaseHTTPRequestHandler
from pathlib import Path

from rig.estate import discovery, locate, trim
from rig.estate.decisions import Decision, default_feed_path, read_feed
from rig.estate.trim import Trimmable
from rig.typed_json import decode_json, encode_json
from rig.web_routes import Request, Route, RouteNotFoundError, json_body, respond

DECISIONS_PATH = "/api/ops/decisions"
ESTATE_PREFIX = "/api/ops/estate/"
REMOVED = "removed"
NOT_IN_SCAN = "not-in-scan"


@dataclass(frozen=True, slots=True)
class DecisionFeed:
    decisions: tuple[Decision, ...]
    #: When the feed was last written, ISO 8601 UTC; empty when there is no feed.
    written: str
    #: Why the list is empty when it is not because nothing needs deciding; empty otherwise.
    note: str


@dataclass(frozen=True, slots=True)
class DiskUsage:
    path: str
    total: int
    used: int
    free: int


@dataclass(frozen=True, slots=True)
class TrimCandidate:
    worktree: str
    path: str
    category: str
    freeable: int
    #: Why it may not be removed; empty when it may.
    refused: str


@dataclass(frozen=True, slots=True)
class TrimScan:
    #: When the shown list was taken, ISO 8601 UTC; empty before the first scan finishes.
    scanned_at: str
    candidates: tuple[TrimCandidate, ...]
    scanning: bool
    #: Why the last scan failed; empty when it did not.
    error: str


@dataclass(frozen=True, slots=True)
class TrimResult:
    path: str
    #: `removed`, `not-in-scan`, `refused: <reason>` or `failed: <reason>`.
    status: str
    freeable: int


@dataclass(frozen=True, slots=True)
class ApplyRequest:
    paths: tuple[str, ...]


def read_decisions(path: Path) -> DecisionFeed:
    """The feed at *path*, or an empty one saying why there is nothing to show."""
    try:
        mtime = path.stat().st_mtime
    except FileNotFoundError:
        return DecisionFeed((), "", f"No decisions feed yet: the estate-decisions job has not written {path}.")
    except OSError as exc:
        return DecisionFeed((), "", f"Cannot read the decisions feed {path}: {exc}")
    # The feed is a bare array that records no timestamp of its own, so the file mtime is when it was written.
    written = dt.datetime.fromtimestamp(mtime, dt.UTC).isoformat(timespec="seconds")
    decisions = read_feed(path)
    if decisions is None:
        return DecisionFeed((), written, f"The decisions feed {path} is not in the expected shape.")
    note = "" if decisions else "Nothing needs deciding."
    return DecisionFeed(tuple(decisions), written, note)


def disk_usage(root: Path) -> DiskUsage:
    usage = shutil.disk_usage(root)
    return DiskUsage(path=str(root), total=usage.total, used=usage.used, free=usage.free)


def default_root() -> Path:
    """The code root `estate trim` scans by default."""
    return locate.code_root(None)


def default_clones(root: Path) -> list[Path]:
    return discovery.repos(root).paths


def _now() -> str:
    return dt.datetime.now(dt.UTC).isoformat(timespec="seconds")


def _candidate(item: Trimmable) -> TrimCandidate:
    return TrimCandidate(
        worktree=str(item.worktree),
        path=str(item.path),
        category=item.category,
        freeable=item.size.freeable,
        refused=item.refused,
    )


def _key(path: str | Path) -> str:
    return str(Path(path))


class TrimCache:
    """The last finished trim scan, refreshed on a background thread; safe to share between request threads."""

    root: Callable[[], Path]
    clones: Callable[[Path], list[Path]]
    scan: Callable[[Path], list[Trimmable]]
    recheck: Callable[[Trimmable], str]
    remove: Callable[[Trimmable], str]
    clock: Callable[[], str]
    _lock: threading.Lock
    _apply_lock: threading.Lock
    _thread: threading.Thread | None
    _items: list[Trimmable]
    _scanned_at: str
    _error: str
    _stale: bool

    def __init__(
        self,
        *,
        root: Callable[[], Path] = default_root,
        clones: Callable[[Path], list[Path]] = default_clones,
        scan: Callable[[Path], list[Trimmable]] = trim.scan,
        recheck: Callable[[Trimmable], str] = trim.recheck,
        remove: Callable[[Trimmable], str] = trim.remove,
        clock: Callable[[], str] = _now,
    ) -> None:
        self.root = root
        self.clones = clones
        self.scan = scan
        self.recheck = recheck
        self.remove = remove
        self.clock = clock
        self._lock = threading.Lock()
        self._apply_lock = threading.Lock()
        self._thread = None
        self._items = []
        self._scanned_at = ""
        self._error = ""
        self._stale = True

    def _scan_all(self) -> list[Trimmable]:
        found: list[Trimmable] = []
        for clone in self.clones(self.root()):
            found += self.scan(clone)
        return sorted(found, key=lambda item: -item.size.freeable)

    def _run_scan(self) -> None:
        try:
            items = self._scan_all()
        except OSError as exc:
            with self._lock:
                self._error = str(exc)
            return
        scanned_at = self.clock()
        with self._lock:
            self._items, self._scanned_at, self._error = items, scanned_at, ""

    def _running(self) -> bool:
        return self._thread is not None and self._thread.is_alive()

    def current(self, *, refresh: bool = False) -> TrimScan:
        """The last finished scan, starting a new one when none has run, the last is stale, or *refresh* asks."""
        with self._lock:
            running = self._running()
            if not running and (refresh or self._stale):
                self._stale = False
                self._thread = threading.Thread(target=self._run_scan, name="estate-trim-scan", daemon=True)
                self._thread.start()
                running = True
            candidates = tuple(_candidate(item) for item in self._items)
            return TrimScan(self._scanned_at, candidates, scanning=running, error=self._error)

    def wait(self, timeout: float | None = None) -> None:
        """Block until the running scan, if any, finishes."""
        with self._lock:
            thread = self._thread
        if thread is not None:
            thread.join(timeout)

    def apply(self, paths: Iterable[str]) -> list[TrimResult]:
        """Remove each requested path the last finished scan offered and the disk still allows."""
        with self._apply_lock:
            with self._lock:
                offered = {_key(item.path): item for item in self._items}
            results = [self._apply_one(path, offered.get(_key(path))) for path in dict.fromkeys(paths)]
            removed = {_key(result.path) for result in results if result.status == REMOVED}
            with self._lock:
                self._items = [item for item in self._items if _key(item.path) not in removed]
                self._stale = True
            return results

    def _apply_one(self, path: str, item: Trimmable | None) -> TrimResult:
        if item is None:
            return TrimResult(path, NOT_IN_SCAN, 0)
        freeable = item.size.freeable
        reason = item.refused or self.recheck(item)
        if reason:
            return TrimResult(path, f"refused: {reason}", freeable)
        return TrimResult(path, self.remove(item), freeable)


def make_route(
    cache: TrimCache | None = None,
    *,
    feed_path: Callable[[], Path] = default_feed_path,
    root: Callable[[], Path] = default_root,
) -> Route:
    """The estate's route, owning one `TrimCache` for the life of the server."""
    trims = cache or TrimCache(root=root)

    def answer(handler: BaseHTTPRequestHandler, request: Request) -> str:
        where = f"{request.method} {request.path.rstrip('/')}"
        if where == f"GET {DECISIONS_PATH}":
            return encode_json(DecisionFeed, read_decisions(feed_path()))
        if where == f"GET {ESTATE_PREFIX}disk":
            return encode_json(DiskUsage, disk_usage(root()))
        if where == f"GET {ESTATE_PREFIX}trim":
            return encode_json(TrimScan, trims.current(refresh=request.flag("refresh")))
        if where == f"POST {ESTATE_PREFIX}trim/apply":
            wanted = decode_json(ApplyRequest, json_body(handler))
            return encode_json(list[TrimResult], trims.apply(wanted.paths))
        raise RouteNotFoundError(f"No {request.method} {request.path}")

    def route(handler: BaseHTTPRequestHandler, request: Request) -> bool:
        if request.path.rstrip("/") != DECISIONS_PATH and not request.path.startswith(ESTATE_PREFIX):
            return False
        return respond(handler, lambda: answer(handler, request))

    return route
