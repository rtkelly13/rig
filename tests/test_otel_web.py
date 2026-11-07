"""The telemetry routes `rig web` mounts: ClickHouse first, the NDJSON files when it is down, and the explorer probes.

No network: ClickHouse is an `httpx.MockTransport`, trace files are written under tmp_path, and the explorer probe and
clocks are fakes.
"""

from __future__ import annotations

import contextlib
import json
import os
import threading
import urllib.error
import urllib.request
from collections.abc import Iterator
from dataclasses import dataclass, field
from pathlib import Path

import httpx
import pytest

from desktop_manager.web_cli import mounted_routes
from rig.image_drift.server import StudioServer
from rig.otel_stack.explorers import Explorers, ExplorerSettings
from rig.otel_stack.query import ClickHouse
from rig.otel_stack.trace_list import ClickHouseTraces, FileTraces, TraceQuery, recent_files
from rig.otel_stack.web import Telemetry, TelemetryRoute, telemetry_route
from rig.typed_json import JsonObject, JsonValue

pytestmark = pytest.mark.ci

NS = 1_000_000_000
DAY = 86_400 * NS
NOW = 1_800_000_000 * NS  # 2027-01-15


# -- fakes ------------------------------------------------------------------------


@dataclass
class FakeClickHouse:
    up: bool = True
    rows: list[JsonObject] = field(default_factory=list[JsonObject])
    status: int = 200
    queries: list[str] = field(default_factory=list[str])
    params: list[dict[str, str]] = field(default_factory=list[dict[str, str]])

    def handler(self, request: httpx.Request) -> httpx.Response:
        if not self.up:
            raise httpx.ConnectError("refused", request=request)
        sql = request.content.decode("utf-8")
        self.queries.append(sql)
        self.params.append(dict(request.url.params))
        if sql.strip() == "SELECT 1":
            return httpx.Response(200, text="1\n")
        if self.status != 200:
            return httpx.Response(self.status, text="Code: 60. Table otel.otel_traces does not exist")
        return httpx.Response(200, text="".join(json.dumps(row) + "\n" for row in self.rows))

    def traces(self) -> ClickHouseTraces:
        def connect(timeout: float) -> ClickHouse:
            return ClickHouse("http://clickhouse.test", timeout=timeout, transport=httpx.MockTransport(self.handler))

        return ClickHouseTraces(connect=connect)


@dataclass
class Clock:
    now: float = 1000.0

    def __call__(self) -> float:
        return self.now


def span(trace: str, span_id: str, name: str, start_s: float, *, parent: str = "", error: bool = False) -> JsonObject:
    start = NOW + int(start_s * NS)
    return {
        "traceId": trace,
        "spanId": span_id,
        "parentSpanId": parent,
        "name": name,
        "startTimeUnixNano": str(start),
        "endTimeUnixNano": str(start + 250_000_000),
        "status": {"code": 2 if error else 1},
    }


def envelope(service: str, *spans: JsonObject) -> str:
    members: list[JsonValue] = list(spans)
    body = {
        "resourceSpans": [
            {
                "resource": {"attributes": [{"key": "service.name", "value": {"stringValue": service}}]},
                "scopeSpans": [{"scope": {"name": "test"}, "spans": members}],
            }
        ]
    }
    return json.dumps(body) + "\n"


def write(path: Path, *lines: str, mtime_ns: int = NOW) -> Path:
    path.parent.mkdir(parents=True, exist_ok=True)
    _ = path.write_text("".join(lines), encoding="utf-8")
    os.utime(path, ns=(mtime_ns, mtime_ns))
    return path


def files(*roots: Path) -> FileTraces:
    return FileTraces(roots=lambda: list(roots), now_ns=lambda: NOW + 10 * NS)


def explorers(probed: list[str] | None = None, *, hyperdx: str = "", up: bool = True) -> Explorers:
    seen = probed if probed is not None else []

    def probe(url: str) -> bool:
        seen.append(url)
        return up

    return Explorers(
        settings=lambda: ExplorerSettings(hyperdx_url=hyperdx, aspire_url="http://localhost:18888"),
        probe=probe,
        clock=Clock(),
    )


# -- the NDJSON fallback ----------------------------------------------------------


def test_files_summarise_each_trace_newest_first(tmp_path: Path) -> None:
    sdk = tmp_path / "sdk"
    _ = write(
        sdk / "rig" / "2027-01-15.ndjson",
        envelope("rig", span("t1", "a", "rig.status", 0), span("t1", "b", "git", 0.1, parent="a", error=True)),
        envelope("rig", span("t2", "c", "rig.web", 5)),
        "{torn line\n",
    )
    found = files(sdk).recent(TraceQuery(days=1))
    assert [t.trace_id for t in found] == ["t2", "t1"]
    first = found[1]
    assert (first.service, first.root, first.spans, first.errors) == ("rig", "rig.status", 2, 1)
    assert first.duration_ms == pytest.approx(350.0)


def test_a_span_flushed_open_then_finished_counts_once(tmp_path: Path) -> None:
    sdk = tmp_path / "sdk"
    open_copy = span("t1", "a", "rig.run", 0) | {
        "attributes": [{"key": "span.incomplete", "value": {"boolValue": True}}]
    }
    _ = write(
        sdk / "rig" / "2027-01-15.ndjson", envelope("rig", open_copy), envelope("rig", span("t1", "a", "rig.run", 0))
    )
    [only] = files(sdk).recent(TraceQuery())
    assert only.spans == 1


def test_the_root_wins_over_an_earlier_orphan(tmp_path: Path) -> None:
    sdk = tmp_path / "sdk"
    _ = write(
        sdk / "rig" / "2027-01-15.ndjson",
        envelope("child-svc", span("t1", "b", "early-child", 0, parent="missing")),
        envelope("root-svc", span("t1", "a", "root", 1)),
    )
    [only] = files(sdk).recent(TraceQuery())
    assert (only.root, only.service) == ("root", "root-svc")


def test_days_bound_which_files_are_read(tmp_path: Path) -> None:
    sdk = tmp_path / "sdk"
    old = write(sdk / "rig" / "2026-12-01.ndjson", envelope("rig", span("old", "a", "x", -45 * 86_400)))
    new = write(sdk / "rig" / "2027-01-15.ndjson", envelope("rig", span("new", "a", "x", 0)))
    stale = write(tmp_path / "collector" / "traces-1.jsonl", "", mtime_ns=NOW - 40 * DAY)
    fresh = write(tmp_path / "collector" / "traces.jsonl", "", mtime_ns=NOW)
    assert recent_files([old, new, stale, fresh], cutoff_ns=NOW - DAY) == [new, fresh]
    assert [t.trace_id for t in files(sdk).recent(TraceQuery(days=1))] == ["new"]


def test_every_root_is_read_and_the_service_filter_applies(tmp_path: Path) -> None:
    sdk = tmp_path / "sdk"
    chrome = tmp_path / "chrome-tabs" / "traces"
    _ = write(sdk / "rig" / "2027-01-15.ndjson", envelope("rig", span("t1", "a", "x", 0)))
    _ = write(chrome / "2027-01-15.ndjson", envelope("chrome-tabs", span("t2", "b", "y", 1)))
    assert [t.trace_id for t in files(sdk, chrome).recent(TraceQuery())] == ["t2", "t1"]
    assert [t.trace_id for t in files(sdk, chrome).recent(TraceQuery(service="rig"))] == ["t1"]
    assert len(files(sdk, chrome).recent(TraceQuery(limit=1))) == 1


# -- ClickHouse first -------------------------------------------------------------


ROW: JsonObject = {
    "trace_id": "abc",
    "service": "rig",
    "root": "rig.status",
    "start_ns": NOW,
    "end_ns": NOW + 2_000_000,
    "spans": 3,
    "errors": 0,
}


def test_clickhouse_answers_with_bound_parameters(tmp_path: Path) -> None:
    fake = FakeClickHouse(rows=[ROW])
    telemetry = Telemetry(fake.traces(), files(tmp_path), explorers(), Clock())
    listed = telemetry.traces(TraceQuery(days=3, limit=7, service="it's"))
    assert listed.source == "clickhouse"
    assert listed.note == ""
    assert [(t.trace_id, t.duration_ms, t.spans) for t in listed.traces] == [("abc", 2.0, 3)]
    assert "it's" not in fake.queries[-1]
    assert fake.params[-1].items() >= {"param_days": "3", "param_limit": "7", "param_service": "it's"}.items()


def test_a_dead_clickhouse_falls_back_and_is_rechecked_sparingly(tmp_path: Path) -> None:
    sdk = tmp_path / "sdk"
    _ = write(sdk / "rig" / "2027-01-15.ndjson", envelope("rig", span("t1", "a", "x", 0)))
    fake = FakeClickHouse(up=False)
    clock = Clock()
    telemetry = Telemetry(fake.traces(), files(sdk), explorers(), clock)
    listed = telemetry.traces(TraceQuery())
    assert (listed.source, [t.trace_id for t in listed.traces]) == ("ndjson", ["t1"])
    assert "unreachable" in listed.note
    status = telemetry.status().store
    assert (status.source, status.clickhouse_reachable) == ("ndjson", False)
    fake.up = True
    assert not telemetry.clickhouse_reachable()  # still inside the recheck window
    clock.now += 31
    assert telemetry.clickhouse_reachable()


def test_a_failing_query_falls_back_with_the_reason(tmp_path: Path) -> None:
    fake = FakeClickHouse(status=404)
    telemetry = Telemetry(fake.traces(), files(tmp_path), explorers(), Clock())
    listed = telemetry.traces(TraceQuery())
    assert listed.source == "ndjson"
    assert "otel_traces does not exist" in listed.note
    assert not telemetry.clickhouse_reachable()


# -- explorers --------------------------------------------------------------------


def test_explorer_status_carries_trace_url_templates() -> None:
    status = explorers(hyperdx="http://hdx:8080").status()
    assert status.hyperdx_trace_url == "http://hdx:8080/search?where=TraceId%3D%27{trace_id}%27&whereLanguage=sql"
    assert status.aspire_trace_url == "http://localhost:18888/traces/detail/{trace_id}"
    assert status.hyperdx_reachable
    assert status.aspire_reachable


def test_an_unconfigured_hyperdx_is_empty_and_never_probed() -> None:
    probed: list[str] = []
    status = explorers(probed).status()
    assert (status.hyperdx_url, status.hyperdx_trace_url, status.hyperdx_reachable) == ("", "", False)
    assert probed == ["http://localhost:18888"]


def test_explorers_are_probed_at_most_once_per_window() -> None:
    probed: list[str] = []
    clock = Clock()
    subject = Explorers(
        settings=lambda: ExplorerSettings(hyperdx_url="http://hdx", aspire_url="http://aspire"),
        probe=lambda url: probed.append(url) is None,
        clock=clock,
    )
    _ = subject.status()
    _ = subject.status()
    assert probed == ["http://hdx", "http://aspire"]
    clock.now += 30
    _ = subject.status()
    assert len(probed) == 4


def test_settings_come_from_the_environment(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.delenv("RIG_HYPERDX_URL", raising=False)
    monkeypatch.delenv("RIG_ASPIRE_URL", raising=False)
    assert ExplorerSettings.from_env() == ExplorerSettings(hyperdx_url="", aspire_url="http://localhost:18888")
    monkeypatch.setenv("RIG_HYPERDX_URL", "http://hdx:8080/")
    monkeypatch.setenv("RIG_ASPIRE_URL", "")
    assert ExplorerSettings.from_env() == ExplorerSettings(hyperdx_url="http://hdx:8080", aspire_url="")


# -- end to end through the web server --------------------------------------------


@dataclass(frozen=True, slots=True)
class Reply:
    status: int
    body: str


def call(url: str) -> Reply:
    try:
        with urllib.request.urlopen(url, timeout=5) as response:  # noqa: S310 - loopback test server
            return Reply(response.status, response.read().decode("utf-8"))
    except urllib.error.HTTPError as error:
        return Reply(error.code, error.read().decode("utf-8"))


@contextlib.contextmanager
def serving(tmp_path: Path, route: TelemetryRoute) -> Iterator[str]:
    templates = tmp_path / "templates"
    templates.mkdir(exist_ok=True)
    server = StudioServer(
        port=0,
        templates_dir=templates,
        db_path=tmp_path / "rig.db",
        assets_root=tmp_path / "assets",
        variations_root=tmp_path / "variations",
        routes=(route,),
    )
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    try:
        yield f"http://127.0.0.1:{server.server_address[1]}"
    finally:
        server.shutdown()
        server.server_close()


@pytest.fixture
def base_url(tmp_path: Path) -> Iterator[str]:
    sdk = tmp_path / "sdk"
    _ = write(sdk / "rig" / "2027-01-15.ndjson", envelope("rig", span("t1", "a", "rig.web", 0, error=True)))
    route = TelemetryRoute(lambda: Telemetry(FakeClickHouse(up=False).traces(), files(sdk), explorers(), Clock()))
    with serving(tmp_path, route) as url:
        yield url


def test_status_and_traces_over_http(base_url: str) -> None:
    status = call(f"{base_url}/api/ops/telemetry/status")
    assert status.status == 200
    assert json.loads(status.body) == {
        "store": {
            "source": "ndjson",
            "clickhouse_reachable": False,
            "detail": "ClickHouse unreachable: reading local NDJSON trace files",
        },
        "explorers": {
            "hyperdx_url": "",
            "hyperdx_reachable": False,
            "hyperdx_trace_url": "",
            "aspire_url": "http://localhost:18888",
            "aspire_reachable": True,
            "aspire_trace_url": "http://localhost:18888/traces/detail/{trace_id}",
        },
    }
    traces = call(f"{base_url}/api/ops/telemetry/traces?days=2&limit=5")
    assert traces.status == 200
    body = json.loads(traces.body)
    assert body["source"] == "ndjson"
    assert body["note"]
    assert body["traces"] == [
        {
            "trace_id": "t1",
            "service": "rig",
            "root": "rig.web",
            "start_ns": NOW,
            "duration_ms": 250.0,
            "spans": 1,
            "errors": 1,
        }
    ]


@pytest.mark.parametrize(
    ("path", "status"),
    [
        ("/api/ops/telemetry/traces?days=31", 400),
        ("/api/ops/telemetry/traces?limit=0", 400),
        ("/api/ops/telemetry/x", 404),
    ],
)
def test_bad_requests_share_the_error_envelope(base_url: str, path: str, status: int) -> None:
    reply = call(base_url + path)
    assert reply.status == status
    assert set(json.loads(reply.body)) == {"error"}


def test_a_dead_fallback_is_a_503_not_a_crash(tmp_path: Path) -> None:
    def broken() -> list[Path]:
        raise OSError("disk gone")

    route = TelemetryRoute(
        lambda: Telemetry(FakeClickHouse(up=False).traces(), FileTraces(roots=broken), explorers(), Clock())
    )
    with serving(tmp_path, route) as url:
        reply = call(f"{url}/api/ops/telemetry/traces")
        assert reply == Reply(503, '{"error": "disk gone"}')
        assert call(f"{url}/api/ops/telemetry/status").status == 200


def test_rig_web_mounts_the_telemetry_route() -> None:
    assert telemetry_route in mounted_routes()
