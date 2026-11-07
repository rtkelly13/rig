"""Organise-run traces: where they land, how long they are kept, and the reports read from them.

Every `organise` run is one `chrome_tabs.organise` span, with a child span per
phase: reading the windows, the GitHub lookups (one `github.graphql` span per
query, so per repo), close planning, closing window by window, group planning,
the idle/lock wait (one `check` event per look), the re-plan, and grouping window
by window. The spans go through `shared_utilities.tracing`, as OTLP-JSON lines, to

    %LOCALAPPDATA%/shared-utilities/chrome-tabs/traces/<YYYY-MM-DD>.ndjson

beside `organise.log`, flushed every `FLUSH_INTERVAL_S` and at exit. Files older
than `KEEP_DAYS` are deleted by the first scheduled run of each day (`prune_daily`)
or by `chrome-tabs traces prune`.

The rest of this module reads those files back for `chrome-tabs traces`. It is
pure file work, so the reports run anywhere, not just on Windows.
"""

from __future__ import annotations

import re
import statistics
from collections import Counter
from collections.abc import Iterable, Sequence
from contextlib import suppress
from dataclasses import dataclass, field
from datetime import UTC, date, datetime, timedelta
from functools import cached_property
from pathlib import Path
from typing import Annotated

from pydantic import Field, StrictBool, StrictFloat, StrictInt, StrictStr

from desktop_manager.chrome.install_location import install_root
from desktop_manager.models import JsonObject, JsonValue
from rig.typed_json import Lenient, decode, load_json, try_decode

SCOPE = "desktop_manager.chrome"
RUN_SPAN = "chrome_tabs.organise"
KEEP_DAYS = 30
FLUSH_INTERVAL_S = 30.0
_PRUNE_MARKER = ".last-prune"
_OTLP_STATUS_ERROR = 2
_FILE = re.compile(r"^(\d{4}-\d{2}-\d{2})\.ndjson$")


def traces_dir() -> Path:
    return install_root() / "traces"


def start(directory: Path | None = None) -> None:
    """Send this process's spans to the organise traces directory, flushed on a timer."""
    from rig import tracing

    tracing.set_trace_dir(directory or traces_dir())
    tracing.flush_periodically(FLUSH_INTERVAL_S)


# -- retention -----------------------------------------------------------------


@dataclass(frozen=True, slots=True, order=True)
class TraceFile:
    """One day's trace file."""

    day: date
    path: Path


def trace_files(directory: Path) -> list[TraceFile]:
    """The day files in `directory`, oldest first. Anything not named `<YYYY-MM-DD>.ndjson` is ignored."""
    found: list[TraceFile] = []
    with suppress(OSError):
        for path in directory.iterdir():
            match = _FILE.match(path.name)
            if match and path.is_file():
                with suppress(ValueError):
                    found.append(TraceFile(date.fromisoformat(match.group(1)), path))
    return sorted(found)


def prune(
    directory: Path, *, keep_days: int = KEEP_DAYS, today: date | None = None, dry_run: bool = False
) -> list[Path]:
    """Delete day files more than `keep_days` old (by the date in the name); the files removed, or due."""
    cutoff = (today or datetime.now(UTC).date()) - timedelta(days=keep_days)
    due = [found.path for found in trace_files(directory) if found.day < cutoff]
    if dry_run:
        return due
    removed: list[Path] = []
    for path in due:
        with suppress(OSError):
            path.unlink()
            removed.append(path)
    return removed


def prune_daily(directory: Path, *, today: date | None = None) -> list[Path] | None:
    """`prune`, at most once a day: None when today's prune has already run. Never raises."""
    today = today or datetime.now(UTC).date()
    marker = directory / _PRUNE_MARKER
    try:
        if marker.is_file() and marker.read_text(encoding="utf-8").strip() == today.isoformat():
            return None
        directory.mkdir(parents=True, exist_ok=True)
        marker.write_text(today.isoformat(), encoding="utf-8")
    except OSError:
        return None
    return prune(directory, today=today)


# -- reading -------------------------------------------------------------------


@dataclass(frozen=True, slots=True)
class _CheckAttrs:
    """The attributes of a wait span's `check` event."""

    reason: Lenient[StrictStr] = None
    blocked: Lenient[StrictBool] = None


@dataclass(frozen=True, slots=True)
class _SpanFlags:
    incomplete: Annotated[Lenient[StrictBool], Field(alias="span.incomplete")] = None


@dataclass(frozen=True, slots=True)
class RunAttrs:
    """The attributes an organise run's root span carries, decoded once."""

    trigger: Annotated[Lenient[StrictStr], Field(alias="chrome.trigger")] = None
    unattended: Annotated[Lenient[StrictBool], Field(alias="chrome.unattended")] = None
    outcome: Annotated[Lenient[StrictStr], Field(alias="run.outcome")] = None
    grouping: Annotated[Lenient[StrictStr], Field(alias="group.outcome")] = None
    tabs_closed: Annotated[Lenient[StrictInt], Field(alias="tabs.closed")] = None
    group_planned: Annotated[Lenient[StrictInt], Field(alias="group.planned")] = None
    group_moved: Annotated[Lenient[StrictInt], Field(alias="group.moved")] = None
    wait_seconds: Annotated[Lenient[StrictFloat], Field(alias="wait.seconds")] = None
    wait_checks: Annotated[Lenient[StrictInt], Field(alias="wait.checks")] = None
    last_block: Annotated[Lenient[StrictStr], Field(alias="wait.last_block")] = None


@dataclass(frozen=True)
class SpanEvent:
    name: str
    at_ns: int
    attributes: dict[str, JsonValue]

    @property
    def check(self) -> _CheckAttrs:
        return decode(_CheckAttrs, self.attributes)


@dataclass(frozen=True)
class SpanRecord:
    trace_id: str
    span_id: str
    parent_id: str
    name: str
    start_ns: int
    end_ns: int
    error: str
    attributes: dict[str, JsonValue]
    events: tuple[SpanEvent, ...] = ()

    @property
    def seconds(self) -> float:
        return max(self.end_ns - self.start_ns, 0) / 1e9

    @property
    def incomplete(self) -> bool:
        return bool(decode(_SpanFlags, self.attributes).incomplete)


@dataclass(frozen=True, slots=True)
class _OtlpAnyValue:
    """An OTLP ``AnyValue``: exactly one of these is set. ``intValue`` arrives as a decimal string."""

    string_value: Annotated[Lenient[StrictStr], Field(alias="stringValue")] = None
    bool_value: Annotated[Lenient[StrictBool], Field(alias="boolValue")] = None
    int_value: Annotated[Lenient[int], Field(alias="intValue")] = None
    double_value: Annotated[Lenient[StrictFloat], Field(alias="doubleValue")] = None
    array_value: Annotated[Lenient[_OtlpArray], Field(alias="arrayValue")] = None


@dataclass(frozen=True, slots=True)
class _OtlpArray:
    values: list[_OtlpAnyValue] = field(default_factory=list[_OtlpAnyValue])


@dataclass(frozen=True, slots=True)
class _OtlpAttribute:
    key: StrictStr
    value: _OtlpAnyValue = field(default_factory=_OtlpAnyValue)


@dataclass(frozen=True, slots=True)
class _OtlpStatus:
    code: JsonValue = None
    message: JsonValue = ""


@dataclass(frozen=True, slots=True)
class _OtlpEvent:
    name: StrictStr = ""
    time_ns: Annotated[int, Field(alias="timeUnixNano")] = 0
    attributes: list[_OtlpAttribute] = field(default_factory=list[_OtlpAttribute])


@dataclass(frozen=True, slots=True)
class _OtlpSpan:
    span_id: Annotated[StrictStr, Field(alias="spanId")]
    trace_id: Annotated[StrictStr, Field(alias="traceId")] = ""
    parent_id: Annotated[StrictStr, Field(alias="parentSpanId")] = ""
    name: StrictStr = ""
    start_ns: Annotated[int, Field(alias="startTimeUnixNano")] = 0
    end_ns: Annotated[int, Field(alias="endTimeUnixNano")] = 0
    status: _OtlpStatus = field(default_factory=_OtlpStatus)
    attributes: list[_OtlpAttribute] = field(default_factory=list[_OtlpAttribute])
    events: list[_OtlpEvent] = field(default_factory=list[_OtlpEvent])


@dataclass(frozen=True, slots=True)
class _OtlpScope:
    spans: list[object] = field(default_factory=list[object])


@dataclass(frozen=True, slots=True)
class _OtlpResource:
    scope_spans: Annotated[list[_OtlpScope], Field(alias="scopeSpans")] = field(default_factory=list[_OtlpScope])


@dataclass(frozen=True, slots=True)
class _OtlpEnvelope:
    resource_spans: Annotated[list[_OtlpResource], Field(alias="resourceSpans")] = field(
        default_factory=list[_OtlpResource]
    )


def _value(raw: _OtlpAnyValue) -> JsonValue:
    """An OTLP ``AnyValue`` as plain JSON: arrays become lists, ``intValue`` strings become ints."""
    if raw.array_value is not None:
        return [_value(item) for item in raw.array_value.values]
    scalars = (raw.string_value, raw.bool_value, raw.int_value, raw.double_value)
    return next((scalar for scalar in scalars if scalar is not None), None)


def _attributes(items: Iterable[_OtlpAttribute]) -> dict[str, JsonValue]:
    return {item.key: _value(item.value) for item in items}


def _span(raw: object) -> SpanRecord:
    span = decode(_OtlpSpan, raw)
    return SpanRecord(
        trace_id=span.trace_id,
        span_id=span.span_id,
        parent_id=span.parent_id,
        name=span.name,
        start_ns=span.start_ns,
        end_ns=span.end_ns,
        error=str(span.status.message) if span.status.code == _OTLP_STATUS_ERROR else "",
        attributes=_attributes(span.attributes),
        events=tuple(SpanEvent(event.name, event.time_ns, _attributes(event.attributes)) for event in span.events),
    )


@dataclass(frozen=True, slots=True)
class _SpanKey:
    """A span id within its trace: `span_id` names the span itself, or its parent."""

    trace_id: str
    span_id: str


def parse_lines(lines: Iterable[str]) -> list[SpanRecord]:
    """Spans from OTLP-JSON lines. A complete span replaces an earlier incomplete copy; bad lines are skipped."""
    spans: dict[_SpanKey, SpanRecord] = {}
    for line in lines:
        try:
            envelope = try_decode(_OtlpEnvelope, load_json(line))
        except ValueError:
            continue
        if envelope is None:
            continue
        raws = [raw for resource in envelope.resource_spans for scope in resource.scope_spans for raw in scope.spans]
        for raw in raws:
            try:
                record = _span(raw)
            except ValueError:
                continue
            key = _SpanKey(record.trace_id, record.span_id)
            seen = spans.get(key)
            if seen is None or seen.incomplete or not record.incomplete:
                spans[key] = record
    return list(spans.values())


def load_spans(directory: Path, *, days: int | None = None, today: date | None = None) -> list[SpanRecord]:
    """Every span in the day files of the last `days` days (all files when None)."""
    since = (today or datetime.now(UTC).date()) - timedelta(days=days) if days is not None else date.min
    lines: list[str] = []
    for found in trace_files(directory):
        if found.day >= since:
            with suppress(OSError):
                lines.extend(found.path.read_text(encoding="utf-8", errors="replace").splitlines())
    return parse_lines(lines)


# -- runs ----------------------------------------------------------------------


@dataclass(frozen=True)
class Run:
    """One organise run: its root span and every span beneath it."""

    root: SpanRecord
    spans: tuple[SpanRecord, ...] = field(default=())

    @cached_property
    def attrs(self) -> RunAttrs:
        return decode(RunAttrs, self.root.attributes)

    @property
    def id(self) -> str:
        return self.root.span_id[:8]

    @property
    def started(self) -> datetime:
        return datetime.fromtimestamp(self.root.start_ns / 1e9).astimezone()

    @property
    def outcome(self) -> str:
        if self.root.incomplete:
            return "incomplete"
        if self.root.error:
            return "error"
        return self.attrs.outcome or "ok"

    @property
    def grouping(self) -> str:
        return self.attrs.grouping or "-"

    @property
    def waited_seconds(self) -> float | None:
        if "wait.seconds" not in self.root.attributes:
            wait = next((span for span in self.spans if span.name == "chrome_tabs.wait_for_idle"), None)
            return wait.seconds if wait is not None else None
        return self.attrs.wait_seconds

    @property
    def checks(self) -> int:
        return self.attrs.wait_checks or 0

    @property
    def blocks(self) -> list[str]:
        """Every block reason the wait saw, in order."""
        return [
            event.check.reason or ""
            for span in self.spans
            if span.name == "chrome_tabs.wait_for_idle"
            for event in span.events
            if event.name == "check" and event.check.blocked
        ]

    def children(self, parent: SpanRecord) -> list[SpanRecord]:
        return sorted((span for span in self.spans if span.parent_id == parent.span_id), key=lambda s: s.start_ns)


def runs(spans: Sequence[SpanRecord]) -> list[Run]:
    """Organise runs, oldest first, each with its descendant spans."""
    by_parent: dict[_SpanKey, list[SpanRecord]] = {}
    for span in spans:
        by_parent.setdefault(_SpanKey(span.trace_id, span.parent_id), []).append(span)
    found: list[Run] = []
    for root in (span for span in spans if span.name == RUN_SPAN):
        below: list[SpanRecord] = []
        stack = [root]
        while stack:
            for child in by_parent.get(_SpanKey(root.trace_id, stack.pop().span_id), []):
                below.append(child)
                stack.append(child)
        found.append(Run(root, tuple(below)))
    return sorted(found, key=lambda run: run.root.start_ns)


def run_dict(run: Run) -> JsonObject:
    """A run's headline facts, for `--json`."""
    return {
        "id": run.id,
        "trace_id": run.root.trace_id,
        "span_id": run.root.span_id,
        "started": run.started.isoformat(timespec="seconds"),
        "seconds": round(run.root.seconds, 2),
        "trigger": run.attrs.trigger,
        "unattended": run.attrs.unattended,
        "outcome": run.outcome,
        "grouping": run.grouping,
        "tabs_closed": run.attrs.tabs_closed,
        "group_planned": run.attrs.group_planned,
        "group_moved": run.attrs.group_moved,
        "waited_seconds": run.waited_seconds,
        "checks": run.checks,
        "last_block": run.attrs.last_block,
        "blocks": [*run.blocks],
        "error": run.root.error or None,
    }


def find_run(found: Sequence[Run], wanted: str | None) -> Run | None:
    """The run whose id starts with `wanted`, or the latest when None."""
    if wanted is None:
        return found[-1] if found else None
    return next((run for run in reversed(found) if run.root.span_id.startswith(wanted.lower())), None)


# -- rendering -----------------------------------------------------------------


def _duration(seconds: float | None) -> str:
    if seconds is None:
        return "-"
    if seconds < 1:
        return f"{seconds * 1000:.0f}ms"
    if seconds < 120:
        return f"{seconds:.1f}s"
    return f"{seconds / 60:.1f}m"


def _short(text: str, width: int) -> str:
    return text if len(text) <= width else text[: width - 1] + "…"


def _grouped(run: Run) -> str:
    if run.grouping in {"grouped", "stopped"} and run.attrs.group_planned is not None:
        return f"{run.grouping} {run.attrs.group_moved}/{run.attrs.group_planned}"
    return run.grouping


RUNS_HEADER = (
    f"{'run':<8}  {'started':<16}  {'took':>6}  {'trigger':<9}  {'outcome':<10}  {'closed':>6}  "
    f"{'grouping':<15}  {'waited':>6}  {'checks':>6}  last block"
)


def render_runs(found: Sequence[Run]) -> list[str]:
    """One line per run, newest first."""
    lines: list[str] = []
    for run in reversed(found):
        closed = run.attrs.tabs_closed
        last = run.attrs.last_block or ""
        lines.append(
            f"{run.id:<8}  {run.started:%Y-%m-%d %H:%M}  {_duration(run.root.seconds):>6}  "
            f"{run.attrs.trigger or '?':<9}  {run.outcome:<10}  {'-' if closed is None else closed:>6}  "
            f"{_short(_grouped(run), 15):<15}  {_duration(run.waited_seconds):>6}  {run.checks or '-':>6}  "
            f"{_short(last, 60)}"
        )
    return lines


def _attr_text(attributes: dict[str, JsonValue], width: int = 60) -> str:
    shown = {key: value for key, value in attributes.items() if key not in {"cli.argv", "span.incomplete"}}
    return " ".join(f"{key}={_short(str(value), width)}" for key, value in shown.items())


def render_tree(run: Run) -> list[str]:
    """The run's spans as an indented tree, with durations, attributes and events."""
    lines: list[str] = []

    def visit(span: SpanRecord, depth: int) -> None:
        indent = "  " * depth
        flag = " [incomplete]" if span.incomplete else f" [error: {_short(span.error, 80)}]" if span.error else ""
        offset = (span.start_ns - run.root.start_ns) / 1e9
        lines.append(f"{indent}{span.name}  {_duration(span.seconds)} at +{offset:.1f}s{flag}")
        if attributes := _attr_text(span.attributes):
            lines.append(f"{indent}    {attributes}")
        for event in span.events:
            at = (event.at_ns - run.root.start_ns) / 1e9
            detail = _attr_text({k: v for k, v in event.attributes.items() if k != "exception.stacktrace"}, 100)
            lines.append(f"{indent}    - +{at:.1f}s {event.name} {detail}".rstrip())
        for child in run.children(span):
            visit(child, depth + 1)

    visit(run.root, 0)
    return lines


@dataclass(frozen=True, slots=True)
class BlockCount:
    """How many runs one (number-folded) block reason stopped."""

    reason: str
    count: int


@dataclass(frozen=True)
class Summary:
    runs: int
    completed: int
    ok: int
    incomplete: int
    grouping: dict[str, int]
    waits: list[float]
    durations: list[float]
    closed: int
    blocks: list[BlockCount]

    @property
    def success_rate(self) -> float | None:
        return self.ok / self.completed if self.completed else None


def summarise(found: Sequence[Run]) -> Summary:
    """Aggregate runs: outcomes, grouping verdicts, waits, closes and the commonest block reasons."""
    completed = [run for run in found if run.outcome != "incomplete"]
    waits = [waited for run in found if run.checks and (waited := run.waited_seconds) is not None]
    # Reasons quote live numbers ("used 12s ago"); fold them so like counts with like.
    reasons = Counter(re.sub(r"\d+", "N", block) for run in found for block in run.blocks)
    return Summary(
        runs=len(found),
        completed=len(completed),
        ok=sum(1 for run in completed if run.outcome in {"ok", "dry_run"}),
        incomplete=len(found) - len(completed),
        grouping=dict(Counter(run.grouping for run in found)),
        waits=waits,
        durations=[run.root.seconds for run in completed],
        closed=sum(run.attrs.tabs_closed or 0 for run in found),
        blocks=[BlockCount(reason, count) for reason, count in reasons.most_common(5)],
    )


def _quantile(values: Sequence[float], q: float) -> float | None:
    if not values:
        return None
    ordered = sorted(values)
    return ordered[min(len(ordered) - 1, round(q * (len(ordered) - 1)))]


def render_summary(summary: Summary, days: int) -> list[str]:
    rate = f"{summary.success_rate:.0%}" if summary.success_rate is not None else "-"
    grouping = ", ".join(f"{name} {count}" for name, count in sorted(summary.grouping.items(), key=lambda i: -i[1]))
    lines = [
        f"Last {days} days: {summary.runs} runs, {summary.completed} completed, {summary.incomplete} incomplete",
        f"  success rate        {rate} ({summary.ok}/{summary.completed})",
        f"  grouping            {grouping or '-'}",
        f"  waited for idle     {len(summary.waits)} runs, median {_duration(_quantile(summary.waits, 0.5))}, "
        f"p90 {_duration(_quantile(summary.waits, 0.9))}",
        f"  run duration        median {_duration(statistics.median(summary.durations) if summary.durations else None)}"
        f", p90 {_duration(_quantile(summary.durations, 0.9))}",
        f"  tabs closed         {summary.closed}",
    ]
    if summary.blocks:
        lines.append("  commonest blocks")
        lines.extend(f"    {block.count:>4}  {block.reason}" for block in summary.blocks)
    return lines
