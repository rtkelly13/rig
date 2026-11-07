"""Telemetry for the `rig web` ops pages: which store answers, which explorers are up, and recent traces.

- `GET /api/ops/telemetry/status` -> `TelemetryView`
- `GET /api/ops/telemetry/traces?days=1..30&limit=1..500&service=` -> `TraceListView`, newest first

Traces come from the on-demand ClickHouse when it answers and from the recorded NDJSON files otherwise
(`trace_list`). ClickHouse's reachability is remembered for `RECHECK_SECONDS`, so a stopped stack costs one short
timeout per half minute rather than one per request. A ClickHouse that is down is not an error: status reports it
and the list falls back, saying so in `note`. Only a failure of the fallback itself is a 503.
"""

from __future__ import annotations

import threading
import time
from collections.abc import Callable
from dataclasses import dataclass
from http.server import BaseHTTPRequestHandler
from typing import Literal

import httpx
from pydantic import ValidationError

from rig.otel_stack.explorers import Explorers, ExplorerStatus
from rig.otel_stack.query import ClickHouseError
from rig.otel_stack.trace_list import ClickHouseTraces, FileTraces, TraceQuery, TraceSummary
from rig.typed_json import encode_json
from rig.web_routes import Request, RouteNotFoundError, respond

PREFIX = "/api/ops/telemetry"
RECHECK_SECONDS = 30.0
UNAVAILABLE: tuple[type[Exception], ...] = (OSError, ClickHouseError, httpx.HTTPError)

type Source = Literal["clickhouse", "ndjson"]


@dataclass(frozen=True, slots=True)
class TelemetryStatus:
    source: Source
    clickhouse_reachable: bool
    detail: str


@dataclass(frozen=True, slots=True)
class TelemetryView:
    store: TelemetryStatus
    explorers: ExplorerStatus


@dataclass(frozen=True, slots=True)
class TraceView:
    trace_id: str
    service: str
    root: str
    start_ns: int
    duration_ms: float
    spans: int
    errors: int


@dataclass(frozen=True, slots=True)
class TraceListView:
    source: Source
    #: Why ClickHouse did not answer; empty when it did.
    note: str
    traces: list[TraceView]


def _view(summary: TraceSummary) -> TraceView:
    return TraceView(
        trace_id=summary.trace_id,
        service=summary.service,
        root=summary.root,
        start_ns=summary.start_ns,
        duration_ms=summary.duration_ms,
        spans=summary.spans,
        errors=summary.errors,
    )


@dataclass(frozen=True, slots=True)
class _Reachability:
    checked_at: float
    reachable: bool


class Telemetry:
    """ClickHouse when it answers, the NDJSON files otherwise, plus the explorer UIs."""

    _clickhouse: ClickHouseTraces
    _files: FileTraces
    _explorers: Explorers
    _clock: Callable[[], float]
    _state: _Reachability | None
    _lock: threading.Lock

    def __init__(
        self,
        clickhouse: ClickHouseTraces | None = None,
        files: FileTraces | None = None,
        explorers: Explorers | None = None,
        clock: Callable[[], float] = time.monotonic,
    ) -> None:
        self._clickhouse = clickhouse or ClickHouseTraces()
        self._files = files or FileTraces()
        self._explorers = explorers or Explorers()
        self._clock = clock
        self._state = None
        self._lock = threading.Lock()

    def clickhouse_reachable(self) -> bool:
        with self._lock:
            now = self._clock()
            if self._state is None or now - self._state.checked_at >= RECHECK_SECONDS:
                self._state = _Reachability(now, self._clickhouse.reachable())
            return self._state.reachable

    def _mark_down(self) -> None:
        with self._lock:
            self._state = _Reachability(self._clock(), reachable=False)

    def status(self) -> TelemetryView:
        if self.clickhouse_reachable():
            store = TelemetryStatus("clickhouse", clickhouse_reachable=True, detail="reading ClickHouse's otel tables")
        else:
            store = TelemetryStatus(
                "ndjson", clickhouse_reachable=False, detail="ClickHouse unreachable: reading local NDJSON trace files"
            )
        return TelemetryView(store=store, explorers=self._explorers.status())

    def traces(self, query: TraceQuery) -> TraceListView:
        note = "ClickHouse unreachable: listing traces from local NDJSON files"
        if self.clickhouse_reachable():
            try:
                found = self._clickhouse.recent(query)
            except (ClickHouseError, ValidationError) as error:
                self._mark_down()
                note = f"ClickHouse query failed, listing traces from local NDJSON files: {str(error)[:300]}"
            else:
                return TraceListView("clickhouse", note="", traces=[_view(summary) for summary in found])
        found = self._files.recent(query)
        return TraceListView("ndjson", note=note, traces=[_view(summary) for summary in found])


class TelemetryRoute:
    """The mounted route over one `Telemetry`; built lazily so importing it does no I/O."""

    _telemetry: Telemetry | None
    _factory: Callable[[], Telemetry]
    _lock: threading.Lock

    def __init__(self, factory: Callable[[], Telemetry] = Telemetry) -> None:
        self._telemetry = None
        self._factory = factory
        self._lock = threading.Lock()

    def telemetry(self) -> Telemetry:
        with self._lock:
            if self._telemetry is None:
                self._telemetry = self._factory()
            return self._telemetry

    def __call__(self, handler: BaseHTTPRequestHandler, request: Request, /) -> bool:
        if request.method != "GET" or not (request.path == PREFIX or request.path.startswith(PREFIX + "/")):
            return False
        return respond(handler, lambda: self.answer(request), unavailable=UNAVAILABLE)

    def answer(self, request: Request) -> str:
        if request.path == f"{PREFIX}/status":
            return encode_json(TelemetryView, self.telemetry().status())
        if request.path == f"{PREFIX}/traces":
            query = TraceQuery(
                days=request.int_param("days", default=1, low=1, high=30),
                limit=request.int_param("limit", default=100, low=1, high=500),
                service=request.param("service"),
            )
            return encode_json(TraceListView, self.telemetry().traces(query))
        raise RouteNotFoundError(f"No telemetry endpoint at {request.path}")


telemetry_route = TelemetryRoute()
