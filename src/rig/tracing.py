"""Always-on, file-backed tracing for the shared-utilities CLIs.

Mirrors `common.tracing` in the workspace repo, on the stdlib alone: the same
OTLP-JSON NDJSON envelope, one file per tool per day, so the same readers work
on both. There is no OpenTelemetry dependency here, and none is needed to answer
the question tracing exists for — *where did the time go?*

Output lands in the repo's gitignored `temp/`:

    temp/traces/sdk/<tool>/<YYYY-MM-DD>.ndjson

`SHARED_UTILITIES_TRACE_DIR` overrides the `temp/traces/sdk` part, and
`SHARED_UTILITIES_TRACING=0` turns it off. A tool installed outside the checkout
names its own directory with `set_trace_dir`; files then land straight in it as
`<YYYY-MM-DD>.ndjson`. The environment override still wins, so tests stay isolated.
`SHARED_UTILITIES_TRACE_HOME` replaces only the `temp/traces/sdk` default, below
both: an installed copy points it outside itself so its traces outlive it.

Spans are buffered in memory and appended on exit, or once the buffer passes
`_FLUSH_AT`, so an estate-wide sweep costs a handful of writes, not thousands.
A long-running process can also flush on a timer (`flush_periodically`), and
`flush(include_open=True)` writes the spans still running too, marked
`span.incomplete`, for a process about to be killed. Readers keep the complete
copy of a span when both reach the file.

Parentage rides a `ContextVar`. A thread pool does **not** inherit one, so
fan-out goes through `submit`, which carries the caller's context into the
worker; a span opened there parents correctly under the span that submitted it.

Public API
----------
- ``init_tracing(tool)``       -- call once at CLI startup
- ``span(name, **attrs)``      -- context manager for an inline span
- ``traced(name)``             -- decorator for a top-level operation
- ``run(args, **kwargs)``      -- `subprocess.run`, traced as its own span
- ``submit(pool, fn, *args)``  -- `pool.submit` that keeps span parentage
- ``current_trace_id()``       -- the trace this process's spans belong to
- ``trace_file()``             -- where this process is writing
- ``set_trace_dir(path)``      -- write to this directory instead
- ``flush_periodically(s)``    -- also flush every ``s`` seconds from a daemon thread
- ``flush(include_open)``      -- write what is buffered now
- ``shutdown_tracing()``       -- flush (auto-registered via atexit)

`Span.event(name, **attrs)` adds a timestamped event; an exception leaving a
span is recorded as an OTel `exception` event as well as the error status.
"""

from __future__ import annotations

import atexit
import contextlib
import contextvars
import datetime as dt
import functools
import json
import os
import pathlib
import secrets
import subprocess
import sys
import threading
import time
import traceback
from collections.abc import Callable, Generator, Mapping, Sequence
from concurrent.futures import Executor, Future
from contextlib import contextmanager
from dataclasses import dataclass, field
from typing import Literal, TypeIs

from rig.typed_json import JsonObject, JsonValue

_ENV_DIR = "SHARED_UTILITIES_TRACE_DIR"
TRACE_HOME_ENV = "SHARED_UTILITIES_TRACE_HOME"
_ENV_ENABLED = "SHARED_UTILITIES_TRACING"
_FLUSH_AT = 2000
_SNIPPET = 200

_REPO_ROOT = pathlib.Path(__file__).resolve().parents[2]

type AttributeScalar = str | int | float | bool
#: What a span or event attribute may hold; anything else is not an OTLP attribute.
type AttributeValue = AttributeScalar | Sequence[AttributeScalar]


@dataclass
class _State:
    tool: str = "shared-utilities"
    trace_id: str = field(default_factory=lambda: secrets.token_hex(16))
    registered: bool = False
    directory: pathlib.Path | None = None
    flusher: threading.Event | None = None


_state = _State()
_lock = threading.Lock()
_buffer: list[_Buffered] = []
_open: dict[str, Span] = {}
_current: contextvars.ContextVar[Span | None] = contextvars.ContextVar("shared_utilities_span", default=None)


def _enabled() -> bool:
    return os.environ.get(_ENV_ENABLED, "1").strip().lower() not in {"0", "false", "no", "off"}


def _traces_root() -> pathlib.Path:
    override = os.environ.get(_ENV_DIR) or os.environ.get(TRACE_HOME_ENV)
    return pathlib.Path(override).expanduser() if override else _REPO_ROOT / "temp" / "traces" / "sdk"


def checkout_traces_root() -> pathlib.Path:
    """Where a tool run from this checkout writes by default: its gitignored `temp/traces/sdk`."""
    return _REPO_ROOT / "temp" / "traces" / "sdk"


def trace_file() -> pathlib.Path:
    """Return the NDJSON file this process appends to."""
    today = dt.datetime.now(dt.UTC).strftime("%Y-%m-%d")
    if _state.directory is not None and not os.environ.get(_ENV_DIR):
        return _state.directory / f"{today}.ndjson"
    return _traces_root() / _state.tool / f"{today}.ndjson"


def set_trace_dir(directory: pathlib.Path | None) -> None:
    """Write `<directory>/<YYYY-MM-DD>.ndjson` from the next flush on; None restores the default."""
    with _lock:
        _flush_locked()
        _state.directory = directory


def init_tracing(tool: str) -> None:
    """Name the tool, start a fresh trace, and register the exit flush."""
    _state.tool = tool
    _state.trace_id = secrets.token_hex(16)
    if not _state.registered:
        atexit.register(shutdown_tracing)
        _state.registered = True


def current_trace_id() -> str:
    """The id of the trace this process's spans belong to, so a record can link to them."""
    return _state.trace_id


@dataclass(frozen=True, slots=True)
class SpanEvent:
    """Something that happened at one moment inside a span."""

    name: str
    at_ns: int
    attributes: dict[str, AttributeValue]


@dataclass(frozen=True, slots=True)
class _Buffered:
    """A finished span's OTLP record, waiting to be written under its scope."""

    scope: str
    span: JsonObject


@dataclass
class Span:
    """One timed operation. Attributes may be added until it ends."""

    name: str
    scope: str
    parent_id: str = ""
    span_id: str = field(default_factory=lambda: secrets.token_hex(8))
    start_ns: int = field(default_factory=time.time_ns)
    end_ns: int = 0
    attributes: dict[str, AttributeValue] = field(default_factory=dict[str, AttributeValue])
    events: list[SpanEvent] = field(default_factory=list[SpanEvent])
    error: str = ""

    def set(self, **attrs: AttributeValue) -> None:
        """Attach attributes. Dots are allowed via a dict: `set(**{"git.args": ...})`."""
        self.attributes.update(attrs)

    def event(self, name: str, **attrs: AttributeValue) -> None:
        """Record something that happened at this moment inside the span."""
        self.events.append(SpanEvent(name=name, at_ns=time.time_ns(), attributes=attrs))

    def record(self) -> JsonObject:
        status: JsonObject = {"code": 2 if self.error else 1}
        if self.error:
            status["message"] = self.error
        out: JsonObject = {
            "traceId": _state.trace_id,
            "spanId": self.span_id,
            "parentSpanId": self.parent_id,
            "name": self.name,
            "startTimeUnixNano": str(self.start_ns),
            "endTimeUnixNano": str(self.end_ns or time.time_ns()),
            "status": status,
        }
        attributes = self.attributes if self.end_ns else {**self.attributes, "span.incomplete": True}
        if attributes:
            out["attributes"] = _attrs(attributes)
        if self.events:
            out["events"] = [_event_record(event) for event in list(self.events)]
        return out


def _event_record(event: SpanEvent) -> JsonValue:
    record: JsonObject = {"name": event.name, "timeUnixNano": str(event.at_ns)}
    if event.attributes:
        record["attributes"] = _attrs(event.attributes)
    return record


def _is_sequence(value: object) -> TypeIs[list[object] | tuple[object, ...]]:
    return isinstance(value, (list, tuple))


def _attr_value(value: object) -> JsonObject:
    if isinstance(value, bool):
        return {"boolValue": value}
    if isinstance(value, int):
        return {"intValue": str(value)}
    if isinstance(value, float):
        return {"doubleValue": value}
    if _is_sequence(value):
        values: list[JsonValue] = [_attr_value(v) for v in value]
        return {"arrayValue": {"values": values}}
    return {"stringValue": str(value)}


def _attrs(attrs: Mapping[str, AttributeValue]) -> list[JsonValue]:
    return [{"key": key, "value": _attr_value(value)} for key, value in attrs.items()]


@contextmanager
def span(name: str, *, scope: str = "shared_utilities", **attrs: AttributeValue) -> Generator[Span]:
    """Time the enclosed block as a child of whatever span is current."""
    parent = _current.get()
    current = Span(name=name, scope=scope, parent_id=parent.span_id if parent else "", attributes=dict(attrs))
    token = _current.set(current)
    with _lock:
        _open[current.span_id] = current
    try:
        yield current
    except SystemExit as exc:
        if exc.code not in (0, None):
            current.error = f"SystemExit: {exc.code}"
        raise
    except BaseException as exc:
        current.error = f"{type(exc).__name__}: {exc}"
        current.event(
            "exception",
            **{
                "exception.type": type(exc).__name__,
                "exception.message": str(exc)[: _SNIPPET * 5],
                "exception.stacktrace": "".join(traceback.format_exception(exc))[-4000:],
            },
        )
        raise
    finally:
        _current.reset(token)
        current.end_ns = time.time_ns()
        with _lock:
            _open.pop(current.span_id, None)
        _emit(current)


def traced[**P, R](name: str, *, scope: str = "shared_utilities") -> Callable[[Callable[P, R]], Callable[P, R]]:
    """Decorate a function so each call is one span."""

    def wrap(fn: Callable[P, R]) -> Callable[P, R]:
        @functools.wraps(fn)
        def inner(*args: P.args, **kwargs: P.kwargs) -> R:
            with span(name, scope=scope):
                return fn(*args, **kwargs)

        return inner

    return wrap


def run(
    args: Sequence[str],
    *,
    scope: str = "rig.subprocess",
    text: Literal[True] = True,
    capture_output: bool = False,
    check: bool = False,
    timeout: float | None = None,
    cwd: str | os.PathLike[str] | None = None,
    env: Mapping[str, str] | None = None,
    stdin: int | None = None,
    input_text: str | None = None,
    encoding: str | None = None,
    errors: str | None = None,
    creationflags: int = 0,
) -> subprocess.CompletedProcess[str]:
    """`subprocess.run` in text mode, recorded as a span with the command, cwd, exit code and duration.

    Exceptions propagate unchanged, so callers keep their own error handling. `rig.proc` passes
    ``stdin=DEVNULL``, UTF-8 decoding and ``CREATE_NO_WINDOW``; other callers keep the defaults.
    """
    argv = [str(a) for a in args]
    label = pathlib.Path(argv[0]).stem if argv else "?"
    verb = next((a for a in argv[1:] if not a.startswith("-")), "")
    with span(f"subprocess: {label} {verb}".rstrip(), scope=scope) as s:
        s.set(
            **{
                "process.command": " ".join(argv)[:1000],
                "process.cwd": str(cwd or ""),
                "process.timeout_s": timeout or 0,
            }
        )
        start = time.perf_counter()
        try:
            done = subprocess.run(
                argv,
                text=text,
                capture_output=capture_output,
                check=check,
                timeout=timeout,
                cwd=cwd,
                env=env,
                stdin=stdin,
                input=input_text,
                encoding=encoding,
                errors=errors,
                creationflags=creationflags,
            )
        except subprocess.TimeoutExpired:
            s.set(**{"process.timed_out": True, "process.duration_s": round(time.perf_counter() - start, 4)})
            raise
        s.set(
            **{
                "process.exit_code": done.returncode,
                "process.duration_s": round(time.perf_counter() - start, 4),
                "process.timed_out": False,
            }
        )
        stderr: str | None = done.stderr
        if done.returncode != 0 and stderr:
            s.set(**{"process.stderr_snippet": stderr.strip()[:_SNIPPET]})
        return done


def spawn_detached(args: Sequence[str], *, scope: str = "rig.subprocess", log: pathlib.Path | None = None) -> int:
    """Start a process that outlives the caller, with no console and no stdin, and return its pid.

    Its stdout and stderr go to *log* (appended) when given, else nowhere. The span records the
    launch only; the process is never waited on, so it must record its own outcome. On Windows it
    gets no console window, and elsewhere its own session.
    """
    argv = [str(a) for a in args]
    label = pathlib.Path(argv[0]).stem if argv else "?"
    flags = (subprocess.CREATE_NO_WINDOW | subprocess.DETACHED_PROCESS) if sys.platform == "win32" else 0
    with span(f"spawn: {label}", scope=scope) as s, contextlib.ExitStack() as files:
        s.set(**{"process.command": " ".join(argv)[:1000]})
        out = files.enter_context(log.open("ab")) if log is not None else subprocess.DEVNULL
        process = subprocess.Popen(
            argv,
            stdin=subprocess.DEVNULL,
            stdout=out,
            stderr=subprocess.STDOUT if log is not None else subprocess.DEVNULL,
            creationflags=flags,
            start_new_session=sys.platform != "win32",
        )
        s.set(**{"process.pid": process.pid})
        return process.pid


def submit[**P, R](pool: Executor, fn: Callable[P, R], /, *args: P.args, **kwargs: P.kwargs) -> Future[R]:
    """`pool.submit`, carrying the caller's span context into the worker thread."""
    ctx = contextvars.copy_context()

    def _in_context() -> R:
        return ctx.run(fn, *args, **kwargs)

    return pool.submit(_in_context)


def _emit(finished: Span) -> None:
    if not _enabled():
        return
    record = finished.record()
    with _lock:
        _buffer.append(_Buffered(scope=finished.scope, span=record))
        if len(_buffer) >= _FLUSH_AT:
            _flush_locked()


def _flush_locked() -> None:
    if not _buffer or not _enabled():
        _buffer.clear()
        return
    by_scope: dict[str, list[JsonValue]] = {}
    for item in _buffer:
        by_scope.setdefault(item.scope, []).append(item.span)
    envelope = {
        "resourceSpans": [
            {
                "resource": {"attributes": _attrs({"service.name": _state.tool})},
                "scopeSpans": [{"scope": {"name": scope}, "spans": spans} for scope, spans in by_scope.items()],
            }
        ]
    }
    _buffer.clear()
    try:
        path = trace_file()
        path.parent.mkdir(parents=True, exist_ok=True)
        with path.open("a", encoding="utf-8") as handle:
            handle.write(json.dumps(envelope) + "\n")
    except (OSError, TypeError, ValueError):
        return  # tracing must never break the tool


def flush(*, include_open: bool = False) -> None:
    """Write what is buffered now; `include_open` also writes every running span as it stands."""
    with _lock:
        if include_open and _enabled():
            _buffer.extend(_Buffered(scope=span.scope, span=span.record()) for span in list(_open.values()))
        _flush_locked()


def flush_periodically(interval_s: float) -> None:
    """Flush every `interval_s` seconds from a daemon thread until `shutdown_tracing`."""
    if _state.flusher is not None:
        return
    stop = threading.Event()
    _state.flusher = stop

    def loop() -> None:
        while not stop.wait(interval_s):
            flush()

    threading.Thread(target=loop, name="tracing-flush", daemon=True).start()


def shutdown_tracing() -> None:
    """Flush whatever is buffered and stop the periodic flush. Safe to call more than once."""
    if _state.flusher is not None:
        _state.flusher.set()
        _state.flusher = None
    with _lock:
        _flush_locked()
