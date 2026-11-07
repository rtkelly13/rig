"""otel-stack's on-demand half: pinned binaries, the low-power ClickHouse config, the client,
trace backfill, logs/metrics replay, and the up/down lifecycle.

No network and no real ClickHouse: an `httpx.MockTransport` stands in for ClickHouse and
the collector, fetches are fakes, and process control is patched at its seams.
"""

from __future__ import annotations

import hashlib
import io
import json
import os
import re
import tarfile
from dataclasses import dataclass, field
from pathlib import Path
from typing import Annotated

import httpx
import pytest
from pydantic import Field
from typer.testing import CliRunner

from rig.otel_stack import backfill, cli, cli_clickhouse, clickhouse, clickhouse_config, pinned, replay
from rig.otel_stack.collector import DigestMismatchError, Fetch, UnsupportedPlatformError, sha256_of
from rig.otel_stack.query import ClickHouse, ClickHouseError, SpanKey, SpanRow
from rig.typed_json import try_decode_json
from rig.typed_yaml import decode_yaml

pytestmark = pytest.mark.ci

NS = 1_000_000_000
NOW = 1_800_000_000 * NS


# -- fakes ------------------------------------------------------------------------


@dataclass
class FakeClickHouse:
    """Span-key lookups over a stored set, canned answers by SQL fragment, and an OTLP collector."""

    stored: set[SpanKey] = field(default_factory=set[SpanKey])
    canned: dict[str, str] = field(default_factory=dict[str, str])
    queries: list[str] = field(default_factory=list[str])
    posted: list[str] = field(default_factory=list[str])
    collector_status: int = 200
    deliver: bool = True
    reject_after: int = -1
    #: Hold accepted spans back until time passes (`settle`), as a collector's batch does.
    lag: bool = False
    pending: set[SpanKey] = field(default_factory=set[SpanKey])

    def settle(self, seconds: float) -> None:
        assert seconds >= 0
        self.stored.update(self.pending)
        self.pending.clear()

    def handler(self, request: httpx.Request) -> httpx.Response:
        if request.url.path.startswith("/v1/"):
            if 0 <= self.reject_after <= len(self.posted):
                return httpx.Response(503, text="boom")
            body = request.content.decode()
            self.posted.append(body)
            if self.deliver and self.collector_status == 200:
                (self.pending if self.lag else self.stored).update(_keys_in(body))
            return httpx.Response(self.collector_status, text="" if self.collector_status == 200 else "boom")
        sql = request.content.decode()
        self.queries.append(sql)
        if "SELECT DISTINCT TraceId, SpanId" in sql:
            ids = _param(request, "param_ids")
            wanted = {match.group(1) or "" for match in re.finditer(r"'([0-9a-f]+)'", ids)}
            rows = [
                json.dumps({"TraceId": k.trace_id, "SpanId": k.span_id}) for k in self.stored if k.trace_id in wanted
            ]
            return httpx.Response(200, text="\n".join(sorted(rows)))
        for fragment, answer in self.canned.items():
            if fragment in sql:
                return httpx.Response(200, text=answer)
        return httpx.Response(200, text="1\n")


@dataclass(frozen=True, slots=True)
class _PostedSpan:
    trace_id: Annotated[str, Field(alias="traceId")]
    span_id: Annotated[str, Field(alias="spanId")]


@dataclass(frozen=True, slots=True)
class _PostedScope:
    spans: list[_PostedSpan]


@dataclass(frozen=True, slots=True)
class _PostedResource:
    scope_spans: Annotated[list[_PostedScope], Field(alias="scopeSpans")]


@dataclass(frozen=True, slots=True)
class _Posted:
    resource_spans: Annotated[list[_PostedResource], Field(alias="resourceSpans")] = field(
        default_factory=list[_PostedResource]
    )


def _param(request: httpx.Request, name: str) -> str:
    return next((value for key, value in request.url.params.multi_items() if key == name), "")


def _keys_in(text: str) -> set[SpanKey]:
    posted = try_decode_json(_Posted, text)
    if posted is None:
        return set()
    return {
        SpanKey(trace_id=span.trace_id, span_id=span.span_id)
        for resource in posted.resource_spans
        for scope in resource.scope_spans
        for span in scope.spans
    }


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


def _write(path: Path, *lines: str, mtime_s: int = 1_700_000_000) -> Path:
    path.parent.mkdir(parents=True, exist_ok=True)
    _ = path.write_text("".join(line + "\n" for line in lines), encoding="utf-8")
    os.utime(path, ns=(mtime_s * NS, mtime_s * NS))
    return path


def _client(fake: FakeClickHouse) -> ClickHouse:
    return ClickHouse("http://ch", transport=httpx.MockTransport(fake.handler))


def _sender(fake: FakeClickHouse, client: httpx.Client) -> backfill.Sender:
    del fake
    return backfill.Sender(client, "http://collector:14318")


def _run(fake: FakeClickHouse, files: list[Path], state: Path, *, dry_run: bool = False) -> backfill.BackfillReport:
    with _client(fake) as ch, httpx.Client(transport=httpx.MockTransport(fake.handler)) as http:
        timing = backfill.Timing(now_ns=lambda: NOW, confirm_seconds=0, sleep=lambda _: None)
        return backfill.run_backfill(files, ch, _sender(fake, http), state_path=state, timing=timing, dry_run=dry_run)


# -- pinned binaries --------------------------------------------------------------


def _tarball(member: str, payload: bytes) -> bytes:
    buffer = io.BytesIO()
    with tarfile.open(fileobj=buffer, mode="w:gz") as tar:
        info = tarfile.TarInfo(member)
        info.size = len(payload)
        tar.addfile(info, io.BytesIO(payload))
        extra = tarfile.TarInfo("README.md")
        extra.size = 2
        tar.addfile(extra, io.BytesIO(b"hi"))
    return buffer.getvalue()


def _fetcher(content: bytes, calls: list[str]) -> Fetch:
    def fetch(url: str, dest: Path) -> None:
        calls.append(url)
        _ = dest.write_bytes(content)

    return fetch


def _pin_for(content: bytes, *, member: str = "") -> pinned.Pin:
    return pinned.Pin("https://example.test/bin", hashlib.sha256(content).hexdigest(), "clickhouse", member=member)


def test_a_bare_binary_is_verified_then_installed_once(tmp_path: Path) -> None:
    content = b"#!/bin/sh\necho clickhouse\n"
    pin = _pin_for(content)
    calls: list[str] = []
    first = pinned.install(pin, home=tmp_path, fetch=_fetcher(content, calls))
    second = pinned.install(pin, home=tmp_path, fetch=_fetcher(content, calls))
    assert first == second == pinned.binary(pin, tmp_path)
    assert first.read_bytes() == content
    assert os.access(first, os.X_OK)
    assert calls == ["https://example.test/bin"], "a verified install is not fetched again"


def test_only_the_named_member_of_a_tarball_is_unpacked(tmp_path: Path) -> None:
    archive = _tarball("pkg/usr/bin/clickhouse", b"binary")
    pin = _pin_for(archive, member="pkg/usr/bin/clickhouse")
    target = pinned.install(pin, home=tmp_path, fetch=_fetcher(archive, []))
    assert target.read_bytes() == b"binary"
    assert sorted(p.name for p in target.parent.iterdir()) == [".sha256", "clickhouse"]


def test_a_digest_mismatch_unpacks_nothing(tmp_path: Path) -> None:
    pin = _pin_for(b"expected")
    with pytest.raises(DigestMismatchError):
        _ = pinned.install(pin, home=tmp_path, fetch=_fetcher(b"tampered", []))
    assert not pinned.binary(pin, tmp_path).exists()
    assert list(pinned.pin_dir(pin, tmp_path).iterdir()) == []


def test_clickhouse_has_no_windows_pin_and_says_why() -> None:
    with pytest.raises(UnsupportedPlatformError, match="WSL"):
        _ = pinned.clickhouse_pin("windows_amd64")
    assert pinned.contrib_pin("windows_amd64").binary == "otelcol-contrib.exe"
    assert all(pin.url.startswith("https://") for pin in [*pinned.CLICKHOUSE.values(), *pinned.CONTRIB.values()])


def test_sha256_of_matches_hashlib(tmp_path: Path) -> None:
    path = tmp_path / "f"
    _ = path.write_bytes(b"abc")
    assert sha256_of(path) == hashlib.sha256(b"abc").hexdigest()


# -- config -----------------------------------------------------------------------


def test_server_config_is_loopback_only_with_no_system_logs_or_idle_timers(tmp_path: Path) -> None:
    xml = clickhouse_config.server_xml(tmp_path / "data", tmp_path / "users.xml")
    assert "<listen_host>127.0.0.1</listen_host>" in xml
    assert f"<http_port>{clickhouse_config.HTTP_PORT}</http_port>" in xml
    assert "remove=" not in xml, "in a standalone config `remove` keeps an empty section, which enables the log"
    assert not re.search(r"<\w+_log>", xml), "no system log section at all"
    assert "<global_profiler_real_time_period_ns>0<" in xml
    assert "<config_reload_interval_ms>3600000<" in xml
    assert f"<path>{(tmp_path / 'data').as_posix()}/</path>" in xml


@dataclass(frozen=True, slots=True)
class _Exporter:
    endpoint: str
    database: str
    ttl: str
    create_schema: bool


@dataclass(frozen=True, slots=True)
class _Exporters:
    clickhouse: _Exporter


@dataclass(frozen=True, slots=True)
class _Pipeline:
    receivers: list[str]
    exporters: list[str]


@dataclass(frozen=True, slots=True)
class _Service:
    pipelines: dict[str, _Pipeline]


@dataclass(frozen=True, slots=True)
class _Collector:
    exporters: _Exporters
    service: _Service


def test_contrib_collector_writes_every_signal_to_clickhouse_with_a_seven_day_ttl() -> None:
    config = decode_yaml(_Collector, clickhouse_config.collector_yaml())
    assert config.exporters.clickhouse.ttl == "168h"
    assert config.exporters.clickhouse.endpoint.startswith("tcp://127.0.0.1:")
    assert config.exporters.clickhouse.create_schema
    assert sorted(config.service.pipelines) == ["logs", "metrics", "traces"]
    assert all(p.exporters == ["clickhouse"] for p in config.service.pipelines.values())


# -- query ------------------------------------------------------------------------


def test_rows_decode_json_each_row_into_typed_rows() -> None:
    span = {
        "Timestamp": "2026-10-25T09:26:04Z",
        "TraceId": "t1",
        "SpanId": "s1",
        "ParentSpanId": "",
        "ServiceName": "svc",
        "SpanName": "root",
        "Duration": 44600,
        "StatusCode": "Ok",
    }
    fake = FakeClickHouse(canned={"TraceId = {trace_id:String}": json.dumps(span) + "\n"})
    with _client(fake) as ch:
        assert ch.trace("t1") == [SpanRow("2026-10-25T09:26:04Z", "t1", "s1", "", "svc", "root", 44600, "Ok")]
    assert fake.queries[0].endswith("FORMAT JSONEachRow")


def test_values_are_bound_parameters_never_spliced_into_sql() -> None:
    seen: list[httpx.Request] = []

    def record(request: httpx.Request) -> httpx.Response:
        seen.append(request)
        return httpx.Response(200, text="1\n" if "system.tables" in request.content.decode() else "")

    hostile = "x'); DROP TABLE otel.otel_traces; --"
    with ClickHouse("http://ch", transport=httpx.MockTransport(record)) as ch:
        _ = ch.trace(hostile)
        _ = ch.existing_spans([hostile])
    assert all(hostile not in request.content.decode() for request in seen)
    assert _param(seen[0], "param_trace_id") == hostile
    assert "\\'" in _param(seen[-1], "param_ids"), "array elements are escaped literals"


def test_errors_and_unreachable_servers_raise_clickhouse_error() -> None:
    def failing(_: httpx.Request) -> httpx.Response:
        return httpx.Response(500, text="Code: 62. DB::Exception: Syntax error")

    def down(request: httpx.Request) -> httpx.Response:
        raise httpx.ConnectError("refused", request=request)

    with ClickHouse("http://ch", transport=httpx.MockTransport(failing)) as ch, pytest.raises(ClickHouseError):
        _ = ch.text("SELEC 1")
    with ClickHouse("http://ch", transport=httpx.MockTransport(down)) as ch:
        assert ch.ping() is False


def test_existing_spans_is_empty_before_the_table_exists() -> None:
    fake = FakeClickHouse(canned={"name = {table:String}": "0\n"})
    with _client(fake) as ch:
        assert ch.existing_spans(["aa"]) == frozenset()
    assert not any("SELECT DISTINCT" in q for q in fake.queries)


# -- trace backfill ---------------------------------------------------------------


def test_backfill_sends_only_what_clickhouse_lacks_then_marks_the_file_done(tmp_path: Path) -> None:
    old = NOW // NS - 3600
    path = _write(tmp_path / "a.ndjson", _line("svc", _span("aa", "1", old), _span("aa", "2", old)))
    fake = FakeClickHouse(stored={SpanKey("aa", "1")})
    state = tmp_path / "state.json"

    first = _run(fake, [path], state)
    second = _run(fake, [path], state)

    assert first.files == [backfill.FileResult(path, spans=2, present=1, sent=1, unsettled=0, confirmed=True)]
    assert set().union(*(_keys_in(body) for body in fake.posted)) == {SpanKey("aa", "2")}
    assert second == backfill.BackfillReport(files=[], unchanged=1, errors=[])


def test_a_renamed_collector_file_resends_nothing(tmp_path: Path) -> None:
    """The collector rotates by renaming; span identity, not the path, decides what is sent."""
    old = NOW // NS - 3600
    active = _write(tmp_path / "traces.jsonl", _line("svc", _span("aa", "1", old)))
    fake = FakeClickHouse()
    state = tmp_path / "state.json"
    _ = _run(fake, [active], state)
    rotated = active.rename(tmp_path / "traces-2026-10-25T10-00-00.000.jsonl")

    report = _run(fake, [rotated], state)
    assert report.files == [backfill.FileResult(rotated, spans=1, present=1, sent=0, unsettled=0, confirmed=True)]
    assert len(fake.posted) == 1


def test_backfill_leaves_recent_spans_and_unconfirmed_files_for_the_next_run(tmp_path: Path) -> None:
    now_s = NOW // NS
    path = _write(tmp_path / "a.ndjson", _line("svc", _span("aa", "1", now_s - 3600), _span("aa", "2", now_s - 10)))
    fake = FakeClickHouse(deliver=False)
    state = tmp_path / "state.json"
    report = _run(fake, [path], state)
    assert report.files == [backfill.FileResult(path, spans=2, present=0, sent=1, unsettled=1, confirmed=False)]
    assert backfill.read_marks(state) == {}


def test_backfill_stops_when_the_collector_rejects_and_marks_nothing(tmp_path: Path) -> None:
    path = _write(tmp_path / "a.ndjson", _line("svc", _span("aa", "1", 100)))
    fake = FakeClickHouse(collector_status=503)
    state = tmp_path / "state.json"
    report = _run(fake, [path], state)
    assert report.errors == ["collector rejected a batch: HTTP 503 boom"]
    assert backfill.read_marks(state) == {}


def test_backfill_keeps_the_complete_copy_of_a_span_written_twice(tmp_path: Path) -> None:
    path = _write(
        tmp_path / "a.ndjson",
        _line("svc", _span("aa", "1", 100, incomplete=True)),
        "{torn line",
        _line("svc", _span("aa", "1", 120)),
    )
    chosen = backfill.best_copies(backfill.read_spans(path))
    assert [(s.key, s.end_ns, s.incomplete) for s in chosen] == [(SpanKey("aa", "1"), 120 * NS, False)]


def test_discovery_finds_day_files_and_the_collectors_trace_files(tmp_path: Path) -> None:
    day = _write(tmp_path / "sdk" / "estate" / "2026-10-25.ndjson", "")
    active = _write(tmp_path / "data" / "traces.jsonl", "")
    rotated = _write(tmp_path / "data" / "traces-2026-10-24T09-00-00.000.jsonl", "")
    _ = _write(tmp_path / "data" / "logs.jsonl", "")
    files = backfill.discover([tmp_path / "sdk"], backfill.collector_trace_files(tmp_path / "data"))
    assert files == sorted([day.resolve(), active.resolve(), rotated.resolve()])


# -- logs and metrics replay ------------------------------------------------------


def _replay(fake: FakeClickHouse, folder: Path, state: Path) -> list[replay.ReplayResult]:
    with httpx.Client(transport=httpx.MockTransport(fake.handler)) as http:
        return replay.replay(_sender(fake, http), folder=folder, state_path=state)


def test_replay_sends_each_complete_line_once_and_survives_rotation(tmp_path: Path) -> None:
    data = tmp_path / "data"
    logs = data / "logs.jsonl"
    data.mkdir()
    _ = logs.write_text('{"resourceLogs":[1]}\n{"resourceLogs":[2]}\n{"resourceLo', encoding="utf-8")
    fake = FakeClickHouse()
    state = tmp_path / "replay.json"

    assert [r.lines for r in _replay(fake, data, state)] == [2]
    assert fake.posted == ['{"resourceLogs":[1]}', '{"resourceLogs":[2]}'], "the torn tail waits"

    with logs.open("a", encoding="utf-8") as handle:
        _ = handle.write('gs":[3]}\n')
    rotated = logs.rename(data / "logs-2026-10-25T10-00-00.000.jsonl")
    assert [r.lines for r in _replay(fake, data, state)] == [1]
    assert fake.posted[-1] == '{"resourceLogs":[3]}'
    assert rotated.exists()


def test_replay_resumes_after_a_rejection_without_resending(tmp_path: Path) -> None:
    data = tmp_path / "data"
    data.mkdir()
    _ = (data / "metrics.jsonl").write_text("a\nb\nc\n", encoding="utf-8")
    state = tmp_path / "replay.json"
    fake = FakeClickHouse(reject_after=1)

    first = _replay(fake, data, state)
    assert (first[0].lines, bool(first[0].error)) == (1, True)
    fake.reject_after = -1
    _ = _replay(fake, data, state)
    assert fake.posted == ["a", "b", "c"]


# -- lifecycle --------------------------------------------------------------------


def test_a_stale_pid_file_counts_as_not_running(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    part = clickhouse.Part("clickhouse", tmp_path / "clickhouse.pid", tmp_path / "log")
    _ = part.pid_file.write_text("4242\n", encoding="utf-8")

    def alive(pid: int) -> bool:
        return pid != 4242

    monkeypatch.setattr(clickhouse, "alive", alive)
    assert clickhouse.running_pid(part) is None
    assert not part.pid_file.exists()


def test_down_stops_the_collector_then_clickhouse_and_waits(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    server = clickhouse.Part("clickhouse", tmp_path / "ch.pid", tmp_path / "ch.log")
    collector = clickhouse.Part("otelcol-contrib", tmp_path / "co.pid", tmp_path / "co.log")
    _ = server.pid_file.write_text("100\n", encoding="utf-8")
    _ = collector.pid_file.write_text("200\n", encoding="utf-8")
    running = {100, 200}
    signalled: list[int] = []

    def fake_signal(pid: int, sig: object) -> None:
        del sig
        signalled.append(pid)
        running.discard(pid)

    monkeypatch.setattr(clickhouse, "parts", lambda: [server, collector])

    def alive(pid: int) -> bool:
        return pid in running

    monkeypatch.setattr(clickhouse, "alive", alive)
    monkeypatch.setattr(clickhouse, "_signal", fake_signal)

    assert clickhouse.down(sleep=lambda _: None) == ["otelcol-contrib", "clickhouse"]
    assert signalled == [200, 100]
    assert not server.pid_file.exists() and not collector.pid_file.exists()


def test_configs_are_written_beside_the_data(tmp_path: Path) -> None:
    written = clickhouse.write_configs(tmp_path)
    assert {written.server.name, written.users.name, written.collector.name} == {
        "server.xml",
        "users.xml",
        "otelcol-contrib.yaml",
    }
    assert all(path.parent == tmp_path for path in (written.server, written.users, written.collector))
    assert (tmp_path / "data").is_dir()
    assert "<networks>" in written.users.read_text(encoding="utf-8")


# -- CLI --------------------------------------------------------------------------


def test_query_adds_a_format_unless_the_sql_names_one(monkeypatch: pytest.MonkeyPatch) -> None:
    fake = FakeClickHouse(canned={"count()": "42\n"})

    def local() -> ClickHouse:
        return _client(fake)

    monkeypatch.setattr(cli_clickhouse, "_clickhouse", local)
    result = CliRunner().invoke(cli.app, ["clickhouse", "query", "SELECT count() FROM otel.otel_traces"])
    assert result.exit_code == 0
    assert result.stdout == "42\n"
    assert fake.queries[-1].endswith("FORMAT PrettyCompactMonoBlock")


def test_backfill_refuses_when_clickhouse_is_down(monkeypatch: pytest.MonkeyPatch) -> None:
    def down(request: httpx.Request) -> httpx.Response:
        raise httpx.ConnectError("refused", request=request)

    def local() -> ClickHouse:
        return ClickHouse("http://ch", transport=httpx.MockTransport(down))

    monkeypatch.setattr(cli_clickhouse, "_clickhouse", local)
    result = CliRunner().invoke(cli.app, ["clickhouse", "backfill"])
    assert result.exit_code == 1
    assert "not answering" in result.stderr


# -- review findings on #341 ------------------------------------------------------


def test_spans_sent_from_a_partly_settled_file_are_confirmed_before_the_next_file(tmp_path: Path) -> None:
    """A file with settled and recent spans must still wait for what it sent, or an
    overlapping later file sees those spans missing and sends them again."""
    now_s = NOW // NS
    first = _write(tmp_path / "a.ndjson", _line("svc", _span("aa", "1", now_s - 3600), _span("aa", "2", now_s - 10)))
    later = _write(tmp_path / "b.ndjson", _line("svc", _span("aa", "1", now_s - 3600)))
    fake = FakeClickHouse(lag=True)
    with _client(fake) as ch, httpx.Client(transport=httpx.MockTransport(fake.handler)) as http:
        timing = backfill.Timing(now_ns=lambda: NOW, confirm_seconds=10, sleep=fake.settle)
        report = backfill.run_backfill(
            [first, later], ch, _sender(fake, http), state_path=tmp_path / "state.json", timing=timing
        )
    sent = [key for body in fake.posted for key in _keys_in(body)]
    assert sent.count(SpanKey("aa", "1")) == 1, "the settled span is sent once"
    assert report.files[0].confirmed is False, "the recent span keeps the first file open"


def test_replay_starts_over_when_a_new_file_reuses_an_old_inode(tmp_path: Path) -> None:
    data = tmp_path / "data"
    data.mkdir()
    logs = data / "logs.jsonl"
    _ = logs.write_text('{"resourceLogs":["old"]}\n', encoding="utf-8")
    state = tmp_path / "replay.json"
    fake = FakeClickHouse()
    _ = _replay(fake, data, state)

    # Simulate the filesystem handing the deleted file's inode to a new file of at least that size.
    inode = logs.stat().st_ino
    offsets = replay.read_offsets(state)
    _ = logs.write_text('{"resourceLogs":["new1"]}\n{"resourceLogs":["new2"]}\n', encoding="utf-8")
    assert logs.stat().st_ino == inode
    replay.write_offsets(state, offsets)

    assert [r.lines for r in _replay(fake, data, state)] == [2]
    assert fake.posted[-2:] == ['{"resourceLogs":["new1"]}', '{"resourceLogs":["new2"]}']


def test_replay_forgets_offsets_for_files_that_are_gone(tmp_path: Path) -> None:
    data = tmp_path / "data"
    data.mkdir()
    rotated = data / "logs-2026-10-18T10-00-00.000.jsonl"
    _ = rotated.write_text("a\n", encoding="utf-8")
    state = tmp_path / "replay.json"
    fake = FakeClickHouse()
    _ = _replay(fake, data, state)
    assert len(replay.read_offsets(state)) == 1

    rotated.unlink()
    _ = _replay(fake, data, state)
    assert replay.read_offsets(state) == {}
