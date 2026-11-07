"""Replay recorded spans into ClickHouse through the on-demand contrib collector (revived from #275).

Spans are always written to files whether or not ClickHouse is up: rig's tracer
writes NDJSON day files, and the always-on collector writes `traces*.jsonl`. Both
hold the same OTLP/JSON envelope, one per line. Backfill sends each span
ClickHouse does not hold yet:

- spans are compared with `otel.otel_traces` by (TraceId, SpanId), so a span an
  earlier backfill sent is never sent twice, even after the collector renames its
  active file on rotation;
- of a span written twice (`span.incomplete` at a forced flush, then complete),
  the complete copy wins;
- spans that ended inside the settle window are left for the next run;
- a file is marked done (by size and mtime) only once every span in it is
  confirmed in ClickHouse; nothing is marked on the strength of a 200 alone.
"""

from __future__ import annotations

import json
import os
import time
from collections.abc import Callable, Iterable, Sequence
from dataclasses import dataclass, field
from pathlib import Path
from typing import Annotated

import httpx
from pydantic import Field

from desktop_manager.runtime import base_dir, traces_dir
from rig.otel_stack.paths import clickhouse_home, data_dir
from rig.otel_stack.query import ClickHouse, ClickHouseError, SpanKey
from rig.tracing import checkout_traces_root
from rig.typed_json import JsonObject, JsonValue, Lenient, try_decode_json

SETTLE_SECONDS = 300
CONFIRM_SECONDS = 30
BATCH_SPANS = 500
TRACE_DIR_ENV = "SHARED_UTILITIES_TRACE_DIR"
STATE_FILE = "backfill-state.json"


# -- the OTLP/JSON envelope -------------------------------------------------------


@dataclass(frozen=True, slots=True)
class _AttrValue:
    bool_value: Annotated[Lenient[bool], Field(alias="boolValue")] = None


@dataclass(frozen=True, slots=True)
class _Attr:
    key: Lenient[str] = None
    value: Lenient[_AttrValue] = None


@dataclass(frozen=True, slots=True)
class _SpanView:
    trace_id: Annotated[str, Field(alias="traceId")]
    span_id: Annotated[str, Field(alias="spanId")]
    end_ns: Annotated[Lenient[int], Field(alias="endTimeUnixNano")] = None
    start_ns: Annotated[Lenient[int], Field(alias="startTimeUnixNano")] = None
    attributes: Lenient[list[_Attr]] = None

    @property
    def incomplete(self) -> bool:
        return any(
            a.key == "span.incomplete" and a.value is not None and a.value.bool_value for a in self.attributes or []
        )


@dataclass(frozen=True, slots=True)
class _ScopeSpans:
    scope: JsonValue = None
    spans: list[JsonObject] = field(default_factory=list[JsonObject])


@dataclass(frozen=True, slots=True)
class _ResourceSpans:
    resource: JsonValue = None
    scope_spans: Annotated[list[_ScopeSpans], Field(alias="scopeSpans")] = field(default_factory=list[_ScopeSpans])


@dataclass(frozen=True, slots=True)
class _Envelope:
    resource_spans: Annotated[list[_ResourceSpans], Field(alias="resourceSpans")] = field(
        default_factory=list[_ResourceSpans]
    )


@dataclass(frozen=True, slots=True)
class FileSpan:
    """One span read from a file, with the resource and scope it was written under."""

    key: SpanKey
    resource: JsonValue
    scope: JsonValue
    span: JsonObject
    end_ns: int
    incomplete: bool


def read_spans(path: Path) -> list[FileSpan]:
    """Every span in a trace file; unreadable lines are skipped, as a torn last line can be."""
    spans: list[FileSpan] = []
    try:
        lines = path.read_text(encoding="utf-8", errors="replace").splitlines()
    except OSError:
        return spans
    for line in lines:
        envelope = try_decode_json(_Envelope, line) if line.strip() else None
        for resource in envelope.resource_spans if envelope else []:
            for scope in resource.scope_spans:
                for raw in scope.spans:
                    view = try_decode_json(_SpanView, json.dumps(raw))
                    if view is None or not view.trace_id or not view.span_id:
                        continue
                    spans.append(
                        FileSpan(
                            key=SpanKey(trace_id=view.trace_id, span_id=view.span_id),
                            resource=resource.resource,
                            scope=scope.scope,
                            span=raw,
                            end_ns=view.end_ns or view.start_ns or 0,
                            incomplete=view.incomplete,
                        )
                    )
    return spans


def best_copies(spans: Iterable[FileSpan]) -> list[FileSpan]:
    """One copy per span: the complete one when both were written, otherwise the last."""
    chosen: dict[SpanKey, FileSpan] = {}
    for span in spans:
        held = chosen.get(span.key)
        if held is None or held.incomplete or not span.incomplete:
            chosen[span.key] = span
    return list(chosen.values())


def envelopes(spans: Sequence[FileSpan], *, batch: int = BATCH_SPANS) -> list[JsonObject]:
    """OTLP/JSON request bodies for `spans`, regrouped under their resource and scope, `batch` spans each."""
    bodies: list[JsonObject] = []
    for start in range(0, len(spans), batch):
        grouped: dict[str, dict[str, list[JsonValue]]] = {}
        resources: dict[str, JsonValue] = {}
        scopes: dict[str, JsonValue] = {}
        for span in spans[start : start + batch]:
            resource_key = json.dumps(span.resource, sort_keys=True)
            scope_key = json.dumps(span.scope, sort_keys=True)
            resources[resource_key] = span.resource
            scopes[scope_key] = span.scope
            grouped.setdefault(resource_key, {}).setdefault(scope_key, []).append(span.span)
        resource_spans: list[JsonValue] = []
        for resource_key, by_scope in grouped.items():
            scope_spans: list[JsonValue] = [
                {"scope": scopes[scope_key], "spans": members} for scope_key, members in by_scope.items()
            ]
            resource_spans.append({"resource": resources[resource_key], "scopeSpans": scope_spans})
        bodies.append({"resourceSpans": resource_spans})
    return bodies


# -- which files ------------------------------------------------------------------


@dataclass(frozen=True, slots=True)
class _RuntimeState:
    repo: Lenient[str] = None


def default_roots() -> list[Path]:
    """Every directory rig's tracer writes day files to: this checkout, the per-user folder, runtime copies."""
    base = base_dir()
    runtime = base / "runtime"
    roots: list[Path] = []
    override = os.environ.get(TRACE_DIR_ENV)
    if override:
        roots.append(Path(override))
    roots.extend((traces_dir(), checkout_traces_root()))
    try:
        primary = try_decode_json(_RuntimeState, (runtime / "state.json").read_text(encoding="utf-8"))
    except OSError:
        primary = None
    if primary is not None and primary.repo:
        roots.append(Path(primary.repo) / "temp" / "traces" / "sdk")
    if runtime.is_dir():
        roots.extend(copy / "temp" / "traces" / "sdk" for copy in sorted(runtime.iterdir()) if copy.is_dir())
    roots.append(base / "chrome-tabs" / "traces")
    return roots


def collector_trace_files(folder: Path | None = None) -> list[Path]:
    """The always-on collector's trace files: the active `traces.jsonl` and its rotated copies."""
    root = folder or data_dir()
    return sorted(root.glob("traces*.jsonl")) if root.is_dir() else []


def discover(roots: Iterable[Path], extra: Iterable[Path] = ()) -> list[Path]:
    """The NDJSON files under `roots` plus `extra` files, each once (junctions and repeats collapse)."""
    seen: dict[str, Path] = {}
    candidates: list[Path] = []
    for root in roots:
        if root.is_dir():
            candidates.extend(sorted(root.rglob("*.ndjson")))
    candidates.extend(path for path in extra if path.is_file())
    for path in candidates:
        resolved = path.resolve()
        seen.setdefault(os.path.normcase(str(resolved)), resolved)
    return sorted(seen.values())


# -- state ------------------------------------------------------------------------


@dataclass(frozen=True, slots=True)
class FileMark:
    size: int
    mtime_ns: int


def read_marks(path: Path) -> dict[str, FileMark]:
    try:
        text = path.read_text(encoding="utf-8")
    except OSError:
        return {}
    return try_decode_json(dict[str, FileMark], text) or {}


def write_marks(path: Path, marks: dict[str, FileMark]) -> None:
    payload = {key: {"size": mark.size, "mtime_ns": mark.mtime_ns} for key, mark in sorted(marks.items())}
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix(".tmp")
    _ = temporary.write_text(json.dumps(payload, indent=2) + "\n", encoding="utf-8")
    _ = temporary.replace(path)


def _mark(path: Path) -> FileMark | None:
    try:
        stat = path.stat()
    except OSError:
        return None
    return FileMark(size=stat.st_size, mtime_ns=stat.st_mtime_ns)


# -- the run ----------------------------------------------------------------------


@dataclass(frozen=True, slots=True)
class FileResult:
    path: Path
    spans: int
    present: int
    sent: int
    unsettled: int
    confirmed: bool


@dataclass(frozen=True, slots=True)
class BackfillReport:
    files: list[FileResult]
    unchanged: int
    errors: list[str]

    @property
    def sent(self) -> int:
        return sum(item.sent for item in self.files)


@dataclass(frozen=True, slots=True)
class Sender:
    """POSTs OTLP/JSON to the contrib collector."""

    client: httpx.Client
    base_url: str

    def send(self, signal: str, body: JsonObject) -> str | None:
        return self.send_text(signal, json.dumps(body))

    def send_text(self, signal: str, body: str) -> str | None:
        url = f"{self.base_url.rstrip('/')}/v1/{signal}"
        try:
            response = self.client.post(url, content=body.encode("utf-8"), headers={"Content-Type": "application/json"})
        except httpx.HTTPError as exc:
            return f"collector unreachable at {url}: {exc}"
        if not response.is_success:
            return f"collector rejected a batch: HTTP {response.status_code} {response.text.strip()[:200]}"
        return None


@dataclass(frozen=True, slots=True)
class Timing:
    now_ns: Callable[[], int] = time.time_ns
    settle_seconds: int = SETTLE_SECONDS
    confirm_seconds: int = CONFIRM_SECONDS
    sleep: Callable[[float], None] = time.sleep


def run_backfill(
    files: Sequence[Path],
    clickhouse: ClickHouse,
    sender: Sender,
    *,
    state_path: Path | None = None,
    timing: Timing | None = None,
    dry_run: bool = False,
) -> BackfillReport:
    """Send what ClickHouse lacks from each file; see the module docstring for the guarantees."""
    clock = timing or Timing()
    marks_path = state_path or clickhouse_home() / STATE_FILE
    marks = read_marks(marks_path)
    results: list[FileResult] = []
    errors: list[str] = []
    unchanged = 0
    for path in files:
        key = os.path.normcase(str(path))
        mark = _mark(path)
        if mark is None:
            continue
        if marks.get(key) == mark:
            unchanged += 1
            continue
        try:
            result = _backfill_file(path, clickhouse, sender, clock, dry_run)
        except ClickHouseError as exc:
            errors.append(str(exc))
            break
        if isinstance(result, str):
            errors.append(result)
            break
        results.append(result)
        if result.confirmed and not dry_run:
            marks[key] = mark
            write_marks(marks_path, marks)
    return BackfillReport(files=results, unchanged=unchanged, errors=errors)


def _backfill_file(
    path: Path, clickhouse: ClickHouse, sender: Sender, clock: Timing, dry_run: bool
) -> FileResult | str:
    spans = best_copies(read_spans(path))
    present = clickhouse.existing_spans(span.key.trace_id for span in spans)
    settle_from = clock.now_ns() - clock.settle_seconds * 1_000_000_000
    file_mtime = path.stat().st_mtime_ns
    missing = [span for span in spans if span.key not in present]
    ready = [span for span in missing if (file_mtime if span.incomplete else span.end_ns) < settle_from]
    unsettled = len(missing) - len(ready)
    if dry_run:
        return FileResult(path, len(spans), len(spans) - len(missing), len(ready), unsettled, confirmed=False)
    if not ready:
        return FileResult(path, len(spans), len(spans) - len(missing), 0, unsettled, confirmed=not missing)
    for body in envelopes(ready):
        problem = sender.send("traces", body)
        if problem:
            return problem
    # Wait for what was just sent to land even when the file cannot be marked done yet:
    # a later file holding the same spans must see them present, not send them again.
    landed = _confirm(clickhouse, ready, clock)
    confirmed = unsettled == 0 and landed
    return FileResult(path, len(spans), len(spans) - len(missing), len(ready), unsettled, confirmed=confirmed)


def _confirm(clickhouse: ClickHouse, sent: Sequence[FileSpan], clock: Timing) -> bool:
    """Wait for the collector's batch to land, up to `confirm_seconds`; False leaves the file for next run."""
    wanted = {span.key for span in sent}
    deadline = time.monotonic() + clock.confirm_seconds
    while True:
        if wanted <= clickhouse.existing_spans(key.trace_id for key in wanted):
            return True
        if time.monotonic() >= deadline:
            return False
        clock.sleep(2.0)
