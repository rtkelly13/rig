"""Where to send someone who wants to see a trace, and whether those UIs are up (ported from #274's `links.py`).

HyperDX (the ClickStack UI) reads the same ClickHouse tables and keeps history; otel_stack does not run it (it needs
Docker and MongoDB), so it is only linked when `RIG_HYPERDX_URL` names one. Aspire's dashboard is live-only: it holds
what a collector forwarded since it last started. The browser fills `{trace_id}` into the `*_trace_url` templates, so
nothing else needs to know these settings.

Each UI is probed at most once per `RECHECK_SECONDS`, with a `PROBE_TIMEOUT_S` timeout; the answer is cached
in-process and shared by the server's threads.
"""

from __future__ import annotations

import os
import threading
import time
from collections.abc import Callable
from dataclasses import dataclass

import httpx

ENV_HYPERDX_URL = "RIG_HYPERDX_URL"
ENV_ASPIRE_URL = "RIG_ASPIRE_URL"
DEFAULT_ASPIRE_URL = "http://localhost:18888"
#: HyperDX's search page filtered to one trace, and Aspire's trace page; `{trace_id}` is left for the browser.
HYPERDX_TRACE_PATH = "/search?where=TraceId%3D%27{trace_id}%27&whereLanguage=sql"
ASPIRE_TRACE_PATH = "/traces/detail/{trace_id}"
RECHECK_SECONDS = 30.0
PROBE_TIMEOUT_S = 0.5


@dataclass(frozen=True, slots=True)
class ExplorerSettings:
    #: Empty when not configured.
    hyperdx_url: str
    aspire_url: str

    @classmethod
    def from_env(cls) -> ExplorerSettings:
        """`RIG_HYPERDX_URL` (unset: none) and `RIG_ASPIRE_URL` (unset: the default; set empty: none)."""
        return cls(
            hyperdx_url=os.environ.get(ENV_HYPERDX_URL, "").strip().rstrip("/"),
            aspire_url=os.environ.get(ENV_ASPIRE_URL, DEFAULT_ASPIRE_URL).strip().rstrip("/"),
        )


@dataclass(frozen=True, slots=True)
class ExplorerStatus:
    hyperdx_url: str
    hyperdx_reachable: bool
    hyperdx_trace_url: str
    aspire_url: str
    aspire_reachable: bool
    aspire_trace_url: str


def http_probe(url: str) -> bool:
    """Something answers HTTP at `url`; any status counts, since a login redirect is still a live UI."""
    try:
        _ = httpx.get(url, timeout=PROBE_TIMEOUT_S, follow_redirects=False)
    except (httpx.HTTPError, httpx.InvalidURL):
        return False
    return True


@dataclass(frozen=True, slots=True)
class _Probe:
    checked_at: float
    reachable: bool


class Explorers:
    """The configured trace UIs, each probed sparingly."""

    _settings: Callable[[], ExplorerSettings]
    _probe: Callable[[str], bool]
    _clock: Callable[[], float]
    _seen: dict[str, _Probe]
    _lock: threading.Lock

    def __init__(
        self,
        settings: Callable[[], ExplorerSettings] = ExplorerSettings.from_env,
        probe: Callable[[str], bool] = http_probe,
        clock: Callable[[], float] = time.monotonic,
    ) -> None:
        self._settings = settings
        self._probe = probe
        self._clock = clock
        self._seen = {}
        self._lock = threading.Lock()

    def _reachable(self, url: str) -> bool:
        if not url:
            return False
        with self._lock:
            now = self._clock()
            cached = self._seen.get(url)
            if cached is None or now - cached.checked_at >= RECHECK_SECONDS:
                cached = _Probe(now, self._probe(url))
                self._seen[url] = cached
            return cached.reachable

    def status(self) -> ExplorerStatus:
        settings = self._settings()
        hyperdx = settings.hyperdx_url
        aspire = settings.aspire_url
        return ExplorerStatus(
            hyperdx_url=hyperdx,
            hyperdx_reachable=self._reachable(hyperdx),
            hyperdx_trace_url=hyperdx + HYPERDX_TRACE_PATH if hyperdx else "",
            aspire_url=aspire,
            aspire_reachable=self._reachable(aspire),
            aspire_trace_url=aspire + ASPIRE_TRACE_PATH if aspire else "",
        )
