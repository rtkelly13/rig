"""Observe exported OTLP facts in behavior tests without importing a product report reader."""

from __future__ import annotations

import json
from dataclasses import dataclass
from pathlib import Path
from typing import Any


@dataclass(frozen=True)
class Event:
    name: str
    attributes: dict[str, Any]


@dataclass(frozen=True)
class Span:
    trace_id: str
    span_id: str
    parent_id: str
    name: str
    start_ns: int
    status_code: int
    error: str
    attributes: dict[str, Any]
    events: list[Event]


@dataclass(frozen=True)
class ObservedRun:
    root: Span
    spans: list[Span]


def _value(raw: dict[str, Any]) -> Any:
    kind, value = next(iter(raw.items()))
    if kind == "arrayValue":
        return [_value(item) for item in value["values"]]
    return int(value) if kind == "intValue" else value


def _attributes(raw: list[dict[str, Any]]) -> dict[str, Any]:
    return {item["key"]: _value(item["value"]) for item in raw}


def observe_runs(directory: Path, root_name: str) -> list[ObservedRun]:
    spans: dict[tuple[str, str], Span] = {}
    for path in directory.rglob("*.ndjson"):
        for line in path.read_text(encoding="utf-8").splitlines():
            for resource in json.loads(line)["resourceSpans"]:
                for scope in resource["scopeSpans"]:
                    for raw in scope["spans"]:
                        span = Span(
                            raw["traceId"],
                            raw["spanId"],
                            raw.get("parentSpanId", ""),
                            raw["name"],
                            int(raw["startTimeUnixNano"]),
                            raw.get("status", {}).get("code", 0),
                            raw.get("status", {}).get("message", ""),
                            _attributes(raw.get("attributes", [])),
                            [
                                Event(event["name"], _attributes(event.get("attributes", [])))
                                for event in raw.get("events", [])
                            ],
                        )
                        spans[span.trace_id, span.span_id] = span
    runs = []
    for root in (span for span in spans.values() if span.name == root_name):
        connected = {root.span_id}
        children: list[Span] = []
        pending = [span for span in spans.values() if span.trace_id == root.trace_id and span.span_id != root.span_id]
        # Several organise roots may share a trace. Observe only this root's connected subtree.
        while ready := [span for span in pending if span.parent_id in connected]:
            children.extend(ready)
            connected.update(span.span_id for span in ready)
            pending = [span for span in pending if span not in ready]
        runs.append(ObservedRun(root, children))
    return runs
