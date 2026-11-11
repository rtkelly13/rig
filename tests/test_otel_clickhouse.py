"""The telemetry web view's readers: the ClickHouse HTTP client and the span files.

`otel-stack` itself (install, the collector, ClickHouse up/down, backfill and replay) is the C# CLI,
tested in tests/Rig.Core.Tests/Otel and tests/Rig.Cli.Tests/Otel.
"""

from __future__ import annotations

import json
import os
from dataclasses import dataclass
from pathlib import Path

import httpx
import pytest

from rig.otel_stack import backfill
from rig.otel_stack.query import LOCAL_URL, ClickHouse, ClickHouseError, SpanKey

pytestmark = pytest.mark.ci

NS = 1_000_000_000


@dataclass(frozen=True, slots=True)
class _Row:
    name: str
    count: int


def _param(request: httpx.Request, name: str) -> str:
    return next((value for key, value in request.url.params.multi_items() if key == name), "")


def _span(trace: str, span: str, end_s: int, *, incomplete: bool = False) -> dict[str, object]:
    attributes: list[object] = [{"key": "span.incomplete", "value": {"boolValue": True}}] if incomplete else []
    return {
        "traceId": trace,
        "spanId": span,
        "name": f"op-{span}",
        "startTimeUnixNano": str((end_s - 1) * NS),
        "endTimeUnixNano": str(end_s * NS),
        "attributes": attributes,
    }


def _line(service: str, *spans: dict[str, object]) -> str:
    resource = {"attributes": [{"key": "service.name", "value": {"stringValue": service}}]}
    return json.dumps(
        {"resourceSpans": [{"resource": resource, "scopeSpans": [{"scope": {"name": "mod"}, "spans": list(spans)}]}]}
    )


def _write(path: Path, *lines: str) -> Path:
    path.parent.mkdir(parents=True, exist_ok=True)
    _ = path.write_text("".join(line + "\n" for line in lines), encoding="utf-8")
    return path


def test_the_local_server_is_the_one_clickhouse_up_starts() -> None:
    assert LOCAL_URL == "http://127.0.0.1:18123"


def test_rows_decode_json_each_row_into_typed_rows_with_bound_parameters() -> None:
    seen: list[httpx.Request] = []

    def answer(request: httpx.Request) -> httpx.Response:
        seen.append(request)
        return httpx.Response(200, text='{"name": "a", "count": 2}\n\n{"name": "b", "count": 3}\n')

    hostile = "x'); DROP TABLE otel.otel_traces; --"
    with ClickHouse("http://ch", transport=httpx.MockTransport(answer)) as ch:
        rows = ch.rows("SELECT name, count FROM t WHERE name = {name:String};", _Row, {"name": hostile})

    assert rows == [_Row("a", 2), _Row("b", 3)]
    body = seen[0].content.decode()
    assert body.endswith("\nFORMAT JSONEachRow")
    assert hostile not in body
    assert _param(seen[0], "param_name") == hostile
    assert _param(seen[0], "output_format_json_quote_64bit_integers") == "0"


def test_errors_and_unreachable_servers_raise_clickhouse_error() -> None:
    def failing(_: httpx.Request) -> httpx.Response:
        return httpx.Response(500, text="Code: 62. DB::Exception: Syntax error")

    def down(request: httpx.Request) -> httpx.Response:
        raise httpx.ConnectError("refused", request=request)

    with ClickHouse("http://ch", transport=httpx.MockTransport(failing)) as ch:
        with pytest.raises(ClickHouseError, match="Syntax error"):
            _ = ch.text("SELEC 1")
        assert ch.ping() is False
    with ClickHouse("http://ch", transport=httpx.MockTransport(down)) as ch:
        with pytest.raises(ClickHouseError, match="unreachable"):
            _ = ch.text("SELECT 1")
        assert ch.ping() is False


def test_ping_is_true_when_clickhouse_answers_one() -> None:
    with ClickHouse.local(transport=httpx.MockTransport(lambda _: httpx.Response(200, text="1\n"))) as ch:
        assert ch.ping() is True


def test_the_complete_copy_of_a_span_written_twice_wins(tmp_path: Path) -> None:
    path = _write(
        tmp_path / "a.ndjson",
        _line("svc", _span("aa", "1", 100, incomplete=True)),
        "{torn line",
        _line("svc", _span("aa", "1", 120)),
    )
    chosen = backfill.best_copies(backfill.read_spans(path))
    assert [(s.key, s.end_ns, s.incomplete) for s in chosen] == [(SpanKey("aa", "1"), 120 * NS, False)]


def test_discovery_finds_day_files_and_extra_files_once(tmp_path: Path) -> None:
    day = _write(tmp_path / "sdk" / "estate" / "2026-10-25.ndjson", "")
    _ = _write(tmp_path / "sdk" / "notes.txt", "")
    extra = _write(tmp_path / "data" / "traces.jsonl", "")
    files = backfill.discover([tmp_path / "sdk", tmp_path / "missing"], [extra, extra, tmp_path / "gone.jsonl"])
    assert files == sorted([day.resolve(), extra.resolve()])


def test_default_roots_take_the_override_and_runtime_copies(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setenv(backfill.TRACE_DIR_ENV, str(tmp_path / "override"))
    monkeypatch.setattr(backfill, "base_dir", lambda: tmp_path)
    copy = tmp_path / "runtime" / "copy-a"
    copy.mkdir(parents=True)
    _ = (tmp_path / "runtime" / "state.json").write_text(json.dumps({"repo": str(tmp_path / "primary")}), "utf-8")

    roots = backfill.default_roots()

    assert roots[0] == tmp_path / "override"
    assert tmp_path / "primary" / "temp" / "traces" / "sdk" in roots
    assert copy / "temp" / "traces" / "sdk" in roots
    assert roots[-1] == tmp_path / "chrome-tabs" / "traces"
    assert os.fspath(roots[-1]).endswith("traces")
