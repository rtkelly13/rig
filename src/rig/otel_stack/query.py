"""Typed reads of the on-demand ClickHouse over its HTTP interface (revived from #275).

`ClickHouse.local()` connects to the loopback server `otel-stack clickhouse up`
starts; `rows(sql, Row)` decodes `FORMAT JSONEachRow` output into a typed row per
line. The telemetry web view's reader; `otel-stack` itself is the C# CLI.
"""

from __future__ import annotations

from dataclasses import dataclass
from types import TracebackType
from typing import Annotated, Self

import httpx
from pydantic import Field

from rig.typed_json import decode_json

#: 64-bit integers as JSON numbers, not ClickHouse's default quoted strings.
_JSON_SETTINGS = {"output_format_json_quote_64bit_integers": "0", "date_time_output_format": "iso"}
#: The loopback server `otel-stack clickhouse up` starts (Rig.Core ClickHouseConfig.Host and HttpPort).
LOCAL_URL = "http://127.0.0.1:18123"


class ClickHouseError(RuntimeError):
    """ClickHouse answered, but with an error (bad SQL), or did not answer at all."""


@dataclass(frozen=True, slots=True)
class SpanKey:
    """A span's identity: what picks the best copy of a span written twice."""

    trace_id: Annotated[str, Field(alias="TraceId")]
    span_id: Annotated[str, Field(alias="SpanId")]


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
