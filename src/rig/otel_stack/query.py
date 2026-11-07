"""Typed reads of the on-demand ClickHouse over its HTTP interface (revived from #275).

`ClickHouse.local()` connects to the loopback server `otel-stack clickhouse up`
starts; `rows(sql, Row)` decodes `FORMAT JSONEachRow` output into a typed row per
line, and the helpers below cover the questions asked most.

    from rig.otel_stack.query import ClickHouse
    with ClickHouse.local() as ch:
        spans = ch.trace("9f42...")
"""

from __future__ import annotations

from collections.abc import Iterable
from dataclasses import dataclass
from types import TracebackType
from typing import Annotated, Self

import httpx
from pydantic import Field

from rig.otel_stack.clickhouse_config import DATABASE, HOST, HTTP_PORT
from rig.typed_json import decode_json

#: 64-bit integers as JSON numbers, not ClickHouse's default quoted strings.
_JSON_SETTINGS = {"output_format_json_quote_64bit_integers": "0", "date_time_output_format": "iso"}
LOCAL_URL = f"http://{HOST}:{HTTP_PORT}"


class ClickHouseError(RuntimeError):
    """ClickHouse answered, but with an error (bad SQL), or did not answer at all."""


@dataclass(frozen=True, slots=True)
class SpanRow:
    """One span of `otel.otel_traces`, the columns a reader needs."""

    timestamp: Annotated[str, Field(alias="Timestamp")]
    trace_id: Annotated[str, Field(alias="TraceId")]
    span_id: Annotated[str, Field(alias="SpanId")]
    parent_span_id: Annotated[str, Field(alias="ParentSpanId")]
    service_name: Annotated[str, Field(alias="ServiceName")]
    span_name: Annotated[str, Field(alias="SpanName")]
    duration_ns: Annotated[int, Field(alias="Duration")]
    status_code: Annotated[str, Field(alias="StatusCode")]


@dataclass(frozen=True, slots=True)
class SpanKey:
    """A span's identity: what backfill compares to avoid sending one twice."""

    trace_id: Annotated[str, Field(alias="TraceId")]
    span_id: Annotated[str, Field(alias="SpanId")]


@dataclass(frozen=True, slots=True)
class TableStats:
    """One `otel` table's active parts, from `system.parts`."""

    table: str
    rows: int
    bytes_on_disk: int


def quote(value: str) -> str:
    """A ClickHouse string literal, for the elements of an array parameter."""
    return "'" + value.replace("\\", "\\\\").replace("'", "\\'") + "'"


class ClickHouse:
    """A ClickHouse HTTP client. Pass `transport` to test against an `httpx.MockTransport`."""

    def __init__(
        self, url: str = LOCAL_URL, *, timeout: float = 30.0, transport: httpx.BaseTransport | None = None
    ) -> None:
        self._client: httpx.Client = httpx.Client(
            base_url=url, auth=("default", ""), timeout=timeout, transport=transport
        )

    @classmethod
    def local(cls, *, transport: httpx.BaseTransport | None = None) -> ClickHouse:
        return cls(LOCAL_URL, transport=transport)

    def __enter__(self) -> Self:
        return self

    def __exit__(
        self, exc_type: type[BaseException] | None, exc: BaseException | None, tb: TracebackType | None
    ) -> None:
        self.close()

    def close(self) -> None:
        self._client.close()

    def text(self, sql: str, params: dict[str, str] | None = None) -> str:
        """Run `sql` and return ClickHouse's response body as is (whatever FORMAT the SQL names).

        Values go in *params*, bound by ClickHouse to `{name:Type}` placeholders, never
        spliced into the SQL text.
        """
        query = {**_JSON_SETTINGS, **{f"param_{name}": value for name, value in (params or {}).items()}}
        try:
            response = self._client.post("/", params=query, content=sql.encode("utf-8"))
        except httpx.HTTPError as exc:
            raise ClickHouseError(f"ClickHouse unreachable at {self._client.base_url}: {exc}") from exc
        if not response.is_success:
            raise ClickHouseError(response.text.strip() or f"HTTP {response.status_code}")
        return response.text

    def rows[T](self, sql: str, kind: type[T], params: dict[str, str] | None = None) -> list[T]:
        """Run `sql` (without a FORMAT clause) and decode each JSONEachRow line as `kind`."""
        body = self.text(sql.rstrip().rstrip(";") + "\nFORMAT JSONEachRow", params)
        return [decode_json(kind, line) for line in body.splitlines() if line.strip()]

    def ping(self) -> bool:
        try:
            return self.text("SELECT 1").strip() == "1"
        except ClickHouseError:
            return False

    def has_table(self, table: str) -> bool:
        try:
            answer = self.text(_HAS_TABLE, {"database": DATABASE, "table": table})
        except ClickHouseError:
            return False
        return answer.strip() == "1"

    def table_stats(self) -> list[TableStats]:
        """Rows and disk per `otel` table, largest first; an empty table is listed with zeros."""
        return self.rows(_TABLE_STATS, TableStats, {"database": DATABASE})

    def trace(self, trace_id: str) -> list[SpanRow]:
        """Every span of one trace, in start order."""
        return self.rows(_TRACE, SpanRow, {"trace_id": trace_id})

    def existing_spans(self, trace_ids: Iterable[str], *, chunk: int = 500) -> frozenset[SpanKey]:
        """Which spans of these traces ClickHouse already holds. Empty before the table exists."""
        ids = sorted(set(trace_ids))
        if not ids or not self.has_table("otel_traces"):
            return frozenset()
        found: set[SpanKey] = set()
        for start in range(0, len(ids), chunk):
            batch = "[" + ", ".join(quote(trace_id) for trace_id in ids[start : start + chunk]) + "]"
            found.update(self.rows(_EXISTING_SPANS, SpanKey, {"ids": batch}))
        return frozenset(found)


# The table is `otel.otel_traces` (`clickhouse_config.DATABASE`); identifiers cannot be bound,
# so they are written out, and every value is a bound parameter.
_HAS_TABLE = "SELECT count() FROM system.tables WHERE database = {database:String} AND name = {table:String}"
_TABLE_STATS = """
SELECT name AS table,
       toUInt64(ifNull(total_rows, 0)) AS rows,
       toUInt64(ifNull(total_bytes, 0)) AS bytes_on_disk
FROM system.tables WHERE database = {database:String} AND engine LIKE '%MergeTree'
ORDER BY bytes_on_disk DESC, table
"""
_TRACE = (
    "SELECT Timestamp, TraceId, SpanId, ParentSpanId, ServiceName, SpanName, Duration, StatusCode "
    "FROM otel.otel_traces WHERE TraceId = {trace_id:String} ORDER BY Timestamp"
)
_EXISTING_SPANS = "SELECT DISTINCT TraceId, SpanId FROM otel.otel_traces WHERE TraceId IN {ids:Array(String)}"
