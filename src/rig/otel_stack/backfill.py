"""Read the span files rig's tracer and the always-on collector write, for the telemetry web view.

Both hold the same OTLP/JSON envelope, one per line. Of a span written twice (`span.incomplete` at a
forced flush, then complete), the complete copy wins. Sending them to ClickHouse is the C# `otel-stack
clickhouse backfill` (src/Rig.Core/Otel/Backfill.cs).
"""

from __future__ import annotations

import json
import os
from collections.abc import Iterable
from dataclasses import dataclass, field
from pathlib import Path
from typing import Annotated

from pydantic import Field

from desktop_manager.runtime import base_dir, traces_dir
from rig.otel_stack.query import SpanKey
from rig.tracing import checkout_traces_root
from rig.typed_json import JsonObject, JsonValue, Lenient, try_decode_json

TRACE_DIR_ENV = "SHARED_UTILITIES_TRACE_DIR"


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
