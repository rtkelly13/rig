"""Recent trace summaries, from ClickHouse when it is up and from the recorded NDJSON files when it is not.

Ported from #274's hub (`telemetry_clickhouse.py`, `telemetry_ndjson.py`), on main's own pieces:

- `ClickHouseTraces` asks the on-demand ClickHouse (`query.ClickHouse`, `otel.otel_traces`) with every value bound
  as a `{name:Type}` parameter, never formatted into the SQL. `StatusCode` is `Error` in current exporter versions
  and `STATUS_CODE_ERROR` in older ones; both count as errors.
- `FileTraces` reads the tracer's own day files, from the roots backfill reads (`backfill.default_roots`,
  `discover`, `read_spans`). Only files that can hold spans from the last `days` days are opened: a
  `<YYYY-MM-DD>.ndjson` day file by its name, anything else by its mtime. A span written twice (open, then
  finished) counts once, the finished copy. The always-on collector's `traces*.jsonl` are deliberately left out:
  they hold every OTLP sender on the machine and run to gigabytes a day, far too much to parse per request; load
  them into ClickHouse with `otel-stack clickhouse backfill` instead.
"""

from __future__ import annotations

import datetime as dt
import time
from collections.abc import Callable, Iterable
from dataclasses import dataclass
from pathlib import Path
from typing import Annotated

from pydantic import Field

from rig.otel_stack.backfill import FileSpan, best_copies, default_roots, discover, read_spans
from rig.otel_stack.query import LOCAL_URL, ClickHouse
from rig.typed_json import Lenient, try_decode

PING_TIMEOUT_S = 0.5
QUERY_TIMEOUT_S = 5.0
_DAY_NS = 86_400 * 1_000_000_000
_ERROR_CODES = frozenset({"2", "STATUS_CODE_ERROR", "Error"})


@dataclass(frozen=True, slots=True)
class TraceQuery:
    days: int = 1
    limit: int = 100
    service: str | None = None


@dataclass(frozen=True, slots=True)
class TraceSummary:
    """One trace, as much as a list row needs."""

    trace_id: str
    service: str
    #: The root span's name, or the earliest span's when the root never arrived.
    root: str
    start_ns: int
    end_ns: int
    spans: int
    errors: int

    @property
    def duration_ms(self) -> float:
        return max(self.end_ns - self.start_ns, 0) / 1_000_000


# -- ClickHouse ---------------------------------------------------------------------

RECENT_SQL = """
SELECT
    TraceId AS trace_id,
    if(countIf(ParentSpanId = '') > 0, argMinIf(ServiceName, Timestamp, ParentSpanId = ''),
       argMin(ServiceName, Timestamp)) AS service,
    if(countIf(ParentSpanId = '') > 0, argMinIf(SpanName, Timestamp, ParentSpanId = ''),
       argMin(SpanName, Timestamp)) AS root,
    toInt64(toUnixTimestamp64Nano(min(Timestamp))) AS start_ns,
    toInt64(max(toUnixTimestamp64Nano(Timestamp) + toInt64(Duration))) AS end_ns,
    toInt64(uniqExact(SpanId)) AS spans,
    toInt64(uniqExactIf(SpanId, StatusCode IN ('Error', 'STATUS_CODE_ERROR'))) AS errors
FROM otel.otel_traces
WHERE Timestamp >= now() - INTERVAL {days:UInt32} DAY
GROUP BY TraceId
HAVING {service:String} = '' OR service = {service:String}
ORDER BY start_ns DESC
LIMIT {limit:UInt32}
"""


@dataclass(frozen=True, slots=True)
class _Row:
    trace_id: str
    service: str
    root: str
    start_ns: int
    end_ns: int
    spans: int
    errors: int


def _connect(timeout: float) -> ClickHouse:
    return ClickHouse(LOCAL_URL, timeout=timeout)


@dataclass(frozen=True, slots=True)
class ClickHouseTraces:
    """Trace summaries from `otel.otel_traces`. `connect` builds a client with the given timeout."""

    connect: Callable[[float], ClickHouse] = _connect

    def reachable(self) -> bool:
        """ClickHouse answers a trivial query within `PING_TIMEOUT_S`; never raises."""
        with self.connect(PING_TIMEOUT_S) as clickhouse:
            return clickhouse.ping()

    def recent(self, query: TraceQuery) -> list[TraceSummary]:
        """Newest first. Raises `ClickHouseError` (or a `ValidationError` on an unreadable row)."""
        params = {"days": str(query.days), "limit": str(query.limit), "service": query.service or ""}
        with self.connect(QUERY_TIMEOUT_S) as clickhouse:
            rows = clickhouse.rows(RECENT_SQL, _Row, params)
        return [
            TraceSummary(
                trace_id=row.trace_id,
                service=row.service,
                root=row.root,
                start_ns=row.start_ns,
                end_ns=row.end_ns,
                spans=row.spans,
                errors=row.errors,
            )
            for row in rows
        ]


# -- NDJSON files -------------------------------------------------------------------


@dataclass(frozen=True, slots=True)
class _AnyValue:
    string_value: Annotated[Lenient[str], Field(alias="stringValue")] = None


@dataclass(frozen=True, slots=True)
class _KeyValue:
    key: Lenient[str] = None
    value: Lenient[_AnyValue] = None


@dataclass(frozen=True, slots=True)
class _Resource:
    attributes: Lenient[list[_KeyValue]] = None

    @property
    def service(self) -> str:
        for attribute in self.attributes or []:
            if attribute.key == "service.name" and attribute.value is not None:
                return attribute.value.string_value or ""
        return ""


@dataclass(frozen=True, slots=True)
class _Status:
    code: Lenient[int | str] = None


@dataclass(frozen=True, slots=True)
class _Span:
    parent: Annotated[Lenient[str], Field(alias="parentSpanId")] = None
    name: Lenient[str] = None
    start_ns: Annotated[Lenient[int], Field(alias="startTimeUnixNano")] = None
    end_ns: Annotated[Lenient[int], Field(alias="endTimeUnixNano")] = None
    status: Lenient[_Status] = None


@dataclass(frozen=True, slots=True)
class _Flat:
    service: str
    parent: str
    name: str
    start_ns: int
    end_ns: int
    error: bool


def _flatten(span: FileSpan) -> _Flat | None:
    view = try_decode(_Span, span.span)
    if view is None or view.start_ns is None:
        return None
    resource = try_decode(_Resource, span.resource)
    code = view.status.code if view.status is not None else None
    return _Flat(
        service=resource.service if resource is not None else "",
        parent=view.parent or "",
        name=view.name or "",
        start_ns=view.start_ns,
        end_ns=max(view.end_ns or view.start_ns, view.start_ns),
        error=code is not None and str(code) in _ERROR_CODES,
    )


def _summarise(trace_id: str, spans: list[_Flat]) -> TraceSummary:
    roots = [span for span in spans if not span.parent]
    first = min(roots or spans, key=lambda span: span.start_ns)
    return TraceSummary(
        trace_id=trace_id,
        service=first.service,
        root=first.name,
        start_ns=min(span.start_ns for span in spans),
        end_ns=max(span.end_ns for span in spans),
        spans=len(spans),
        errors=sum(span.error for span in spans),
    )


def _dated(stem: str) -> dt.date | None:
    try:
        return dt.date.fromisoformat(stem)
    except ValueError:
        return None


def recent_files(paths: Iterable[Path], *, cutoff_ns: int) -> list[Path]:
    """The files that can hold spans started after `cutoff_ns`: day files by name, the rest by mtime.

    A day file's date is the writer's local date, so one extra day is kept to cover any time zone.
    """
    cutoff_day = dt.datetime.fromtimestamp(cutoff_ns / 1e9, tz=dt.UTC).date() - dt.timedelta(days=1)
    kept: list[Path] = []
    for path in paths:
        day = _dated(path.stem)
        if day is not None:
            if day >= cutoff_day:
                kept.append(path)
            continue
        try:
            if path.stat().st_mtime_ns >= cutoff_ns:
                kept.append(path)
        except OSError:
            continue
    return kept


@dataclass(frozen=True, slots=True)
class FileTraces:
    """Trace summaries from the tracer's NDJSON day files."""

    roots: Callable[[], list[Path]] = default_roots
    now_ns: Callable[[], int] = time.time_ns

    def recent(self, query: TraceQuery) -> list[TraceSummary]:
        """Newest first; unreadable lines and files are skipped."""
        cutoff_ns = self.now_ns() - query.days * _DAY_NS
        files = recent_files(discover(self.roots()), cutoff_ns=cutoff_ns)
        copies = best_copies(span for path in files for span in read_spans(path))
        by_trace: dict[str, list[_Flat]] = {}
        for copy in copies:
            flat = _flatten(copy)
            if flat is not None:
                by_trace.setdefault(copy.key.trace_id, []).append(flat)
        summaries = [_summarise(trace_id, spans) for trace_id, spans in by_trace.items()]
        wanted = [
            summary
            for summary in summaries
            if summary.start_ns >= cutoff_ns and (not query.service or summary.service == query.service)
        ]
        wanted.sort(key=lambda summary: summary.start_ns, reverse=True)
        return wanted[: query.limit]
