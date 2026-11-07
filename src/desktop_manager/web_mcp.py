"""The `rig web` ops data as a Model Context Protocol server for agents: `rig web mcp` serves it on stdio.

Each tool answers what one `/api/ops/...` route answers, built by the same view builders, so an agent and the ops
pages never disagree. Tools return JSON text.

| tool | same as |
|---|---|
| `attention` | the Attention page: jobs whose last run failed, and decisions waiting on a person |
| `list_jobs` | `GET /api/ops/jobs` |
| `list_runs` | `GET /api/ops/runs?job=&limit=` |
| `run_output` | `GET /api/ops/runs/<job>/<run_id>/output` |
| `start_run` | `POST /api/ops/jobs/run` |
| `decisions` | `GET /api/ops/decisions` |
| `disk_usage` | `GET /api/ops/estate/disk` |
| `trim_candidates` | `GET /api/ops/estate/trim?refresh=` |
| `apply_trim` | `POST /api/ops/estate/trim/apply` |
| `telemetry_status` | `GET /api/ops/telemetry/status` |
| `list_traces` | `GET /api/ops/telemetry/traces?days=&limit=&service=` |

The server owns one `TrimCache` for its life, as `rig web` does: `apply_trim` removes only paths the last finished
scan offered, so call `trim_candidates` first, and again until `scanning` is false.
"""

from __future__ import annotations

import threading
from collections.abc import Callable, Generator, Sequence
from contextlib import contextmanager
from dataclasses import dataclass, field
from pathlib import Path

from mcp.server.mcpserver import MCPServer
from mcp.server.mcpserver.exceptions import ToolError
from mcp_types import ToolAnnotations

from desktop_manager.jobs.registry import Job
from desktop_manager.jobs.state import jobs_dir
from desktop_manager.jobs.web import (
    MAX_LIMIT,
    JobView,
    RunOutput,
    RunStarted,
    RunView,
    job_views,
    registered_jobs,
    run_output_view,
    run_views,
    start_job,
)
from rig.estate.decisions import default_feed_path
from rig.estate.web import (
    DecisionFeed,
    DiskUsage,
    TrimCache,
    TrimResult,
    TrimScan,
    default_root,
    disk_usage,
    read_decisions,
)
from rig.otel_stack.trace_list import TraceQuery
from rig.otel_stack.web import Telemetry, TelemetryView, TraceListView
from rig.tracing import spawn_detached
from rig.typed_json import encode_json
from rig.web_routes import RouteNotFoundError

NAME = "rig-ops"
READ = ToolAnnotations(read_only_hint=True)
START = ToolAnnotations(read_only_hint=False, destructive_hint=False, idempotent_hint=False)
DELETE = ToolAnnotations(read_only_hint=False, destructive_hint=True, idempotent_hint=True)


@dataclass(frozen=True, slots=True)
class Attention:
    #: Jobs whose last run failed, as `list_jobs` shows them.
    failing_jobs: tuple[JobView, ...]
    decisions: DecisionFeed


@contextmanager
def _refusals() -> Generator[None]:
    """Hand an expected refusal to the agent as its message, as the routes answer it with a 404 or 400."""
    try:
        yield
    except (RouteNotFoundError, ValueError) as error:
        raise ToolError(str(error)) from error


def _clamp(value: int, low: int, high: int) -> int:
    return max(low, min(high, value))


@dataclass(slots=True)
class OpsTools:
    """The tools' implementations; every source is injectable for tests."""

    jobs: Callable[[], Sequence[Job]] = registered_jobs
    directory: Callable[[], Path] = jobs_dir
    start: Callable[[Sequence[str]], int] = spawn_detached
    feed_path: Callable[[], Path] = default_feed_path
    root: Callable[[], Path] = default_root
    make_telemetry: Callable[[], Telemetry] = Telemetry
    trims: TrimCache | None = None
    _telemetry: Telemetry | None = None
    _lock: threading.Lock = field(default_factory=threading.Lock)

    def _trim_cache(self) -> TrimCache:
        with self._lock:
            if self.trims is None:
                self.trims = TrimCache(root=self.root)
            return self.trims

    def _telemetry_source(self) -> Telemetry:
        with self._lock:
            if self._telemetry is None:
                self._telemetry = self.make_telemetry()
            return self._telemetry

    def attention(self) -> str:
        """What needs a person: coordinator jobs whose last run failed, and estate decisions waiting on someone."""
        views = job_views(self.jobs(), self.directory())
        failing = tuple(view for view in views if view.last is not None and not view.last.ok)
        return encode_json(Attention, Attention(failing, read_decisions(self.feed_path())))

    def list_jobs(self) -> str:
        """Every registered coordinator job: what it does, when it runs, and how its last run went."""
        return encode_json(list[JobView], job_views(self.jobs(), self.directory()))

    def list_runs(self, job: str = "", limit: int = 50) -> str:
        """Recorded job runs, newest first, each with its trace id when it ran traced.

        Args:
            job: Only this job's runs; empty for every job.
            limit: How many runs, 1 to 1000.
        """
        found = run_views(self.directory(), job=job.strip() or None, limit=_clamp(limit, 1, MAX_LIMIT))
        return encode_json(list[RunView], found)

    def run_output(self, job: str, run_id: str) -> str:
        """What one job run printed; `output` is null when the run saved none or it has been pruned.

        Args:
            job: The job's name, as `list_jobs` gives it.
            run_id: The run's id, as `list_runs` gives it.
        """
        with _refusals():
            return encode_json(RunOutput, run_output_view(self.jobs(), self.directory(), job=job, run_id=run_id))

    def start_run(self, job: str = "") -> str:
        """Start a coordinator run in the background and return at once; it shows in `list_runs` when it finishes.

        If a scheduled run is in progress, this one skips.

        Args:
            job: One job to run whether or not it is due; empty to run every job that is due.
        """
        with _refusals():
            return encode_json(RunStarted, start_job(self.jobs(), job.strip() or None, self.start))

    def decisions(self) -> str:
        """Worktrees waiting on a person: unpushed commits, closed PRs, idle checkouts. `note` says why it is empty."""
        return encode_json(DecisionFeed, read_decisions(self.feed_path()))

    def disk_usage(self) -> str:
        """Total, used and free bytes on the drive holding the code root."""
        return encode_json(DiskUsage, disk_usage(self.root()))

    def trim_candidates(self, *, refresh: bool = False) -> str:
        """Regenerable caches (node_modules, .venv, bin, obj) in idle worktrees, from the last finished scan.

        A scan runs in the background: while `scanning` is true, call again for the finished list.

        Args:
            refresh: Start a new scan rather than returning the last one.
        """
        return encode_json(TrimScan, self._trim_cache().current(refresh=refresh))

    def apply_trim(self, paths: list[str]) -> str:
        """Delete caches the last finished scan offered; each is rechecked first and refused if it changed.

        Args:
            paths: Candidate paths exactly as `trim_candidates` gave them.
        """
        return encode_json(list[TrimResult], self._trim_cache().apply(paths))

    def telemetry_status(self) -> str:
        """Which trace store answers (ClickHouse or local NDJSON files) and which trace explorers are up."""
        return encode_json(TelemetryView, self._telemetry_source().status())

    def list_traces(self, days: int = 1, limit: int = 100, service: str = "") -> str:
        """Recent traces, newest first, from ClickHouse when it answers and local NDJSON files otherwise.

        Args:
            days: How far back, 1 to 30.
            limit: How many traces, 1 to 500.
            service: Only this service's traces; empty for all.
        """
        query = TraceQuery(days=_clamp(days, 1, 30), limit=_clamp(limit, 1, 500), service=service.strip() or None)
        return encode_json(TraceListView, self._telemetry_source().traces(query))


def create_server(tools: OpsTools | None = None) -> MCPServer:
    """The MCP server over *tools*; `run()` on it serves stdio."""
    ops = tools or OpsTools()
    server = MCPServer(name=NAME, instructions=__doc__)
    reads: tuple[Callable[..., str], ...] = (
        ops.attention,
        ops.list_jobs,
        ops.list_runs,
        ops.run_output,
        ops.decisions,
        ops.disk_usage,
        ops.trim_candidates,
        ops.telemetry_status,
        ops.list_traces,
    )
    for tool in reads:
        _ = server.tool(annotations=READ)(tool)
    _ = server.tool(annotations=START)(ops.start_run)
    _ = server.tool(annotations=DELETE)(ops.apply_trim)
    return server
