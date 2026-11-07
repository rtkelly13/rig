"""`rig web mcp`: the ops data as MCP tools, answering what the `/api/ops` routes answer."""

from __future__ import annotations

import asyncio
import json
from collections.abc import Sequence
from datetime import datetime
from pathlib import Path

import pytest
from mcp.server.mcpserver.exceptions import ToolError
from mcp_types import CallToolResult, TextContent

from desktop_manager.jobs.history import JobRun, append_run
from desktop_manager.jobs.registry import Cadence, Job, Trigger
from desktop_manager.jobs.state import STATE_NAME, Outcome, write_state
from desktop_manager.web_mcp import OpsTools, create_server
from rig.estate.web import TrimCache

pytestmark = pytest.mark.ci

STARTED = datetime(2026, 10, 10, 9, 0).astimezone().isoformat(timespec="seconds")
READ_ONLY = {
    "attention",
    "list_jobs",
    "list_runs",
    "run_output",
    "decisions",
    "disk_usage",
    "trim_candidates",
    "telemetry_status",
    "list_traces",
}


def _job(name: str) -> Job:
    return Job(
        name=name,
        description=f"{name} things",
        argv=("tool", name),
        triggers=frozenset({Trigger.TICK}),
        cadence=Cadence.EVERY_RUN,
        timeout_minutes=5,
    )


JOBS = (_job("align"), _job("sync"))


def _tools(tmp_path: Path, started: list[list[str]]) -> OpsTools:
    """align failed its last run and sync passed; the decisions feed has not been written yet."""
    write_state(
        tmp_path / STATE_NAME,
        {
            "align": Outcome(STARTED, 2.0, 3, "tick", run_id="tick-20261010-090000"),
            "sync": Outcome(STARTED, 1.0, 0, "tick", run_id="tick-20261010-090000"),
        },
    )
    append_run(tmp_path, JobRun("align", "tick-20261010-090000", STARTED, 2.0, 3, "tick"))

    def start(command: Sequence[str]) -> int:
        started.append(list(command))
        return 4242

    return OpsTools(
        jobs=lambda: JOBS,
        directory=lambda: tmp_path,
        start=start,
        feed_path=lambda: tmp_path / "decisions.json",
        root=lambda: tmp_path,
        trims=TrimCache(root=lambda: tmp_path, clones=lambda _root: [], clock=lambda: STARTED),
    )


def _text(result: object) -> str:
    assert isinstance(result, CallToolResult)
    assert not result.is_error
    [content] = result.content
    assert isinstance(content, TextContent)
    return content.text


def test_every_tool_says_whether_it_changes_anything(tmp_path: Path) -> None:
    tools = asyncio.run(create_server(_tools(tmp_path, [])).list_tools())
    hints = {tool.name: tool.annotations for tool in tools}
    assert set(hints) == {*READ_ONLY, "start_run", "apply_trim"}
    assert all(hint is not None and hint.read_only_hint for name, hint in hints.items() if name in READ_ONLY)
    start, trim = hints["start_run"], hints["apply_trim"]
    assert start is not None and not start.read_only_hint and not start.destructive_hint
    assert trim is not None and trim.destructive_hint


def test_attention_lists_failing_jobs_and_why_there_are_no_decisions(tmp_path: Path) -> None:
    server = create_server(_tools(tmp_path, []))
    answer = json.loads(_text(asyncio.run(server.call_tool("attention", {}))))
    assert [job["name"] for job in answer["failing_jobs"]] == ["align"]
    assert answer["decisions"]["decisions"] == []
    assert "No decisions feed yet" in answer["decisions"]["note"]


def test_runs_and_jobs_match_the_routes(tmp_path: Path) -> None:
    tools = _tools(tmp_path, [])
    assert [job["name"] for job in json.loads(tools.list_jobs())] == ["align", "sync"]
    runs = json.loads(tools.list_runs(job="align", limit=0))
    assert [run["run_id"] for run in runs] == ["tick-20261010-090000"]
    assert json.loads(tools.run_output("align", "tick-20261010-090000"))["output"] is None


def test_start_run_starts_one_job_or_every_due_one_and_refuses_unknown_jobs(tmp_path: Path) -> None:
    started: list[list[str]] = []
    tools = _tools(tmp_path, started)
    assert json.loads(tools.start_run("align")) == {"job": "align", "pid": 4242}
    assert json.loads(tools.start_run()) == {"job": None, "pid": 4242}
    assert started[0][-2:] == ["--only", "align"]
    assert started[1][-2:] == ["--trigger", "tick"]
    with pytest.raises(ToolError, match="No job named"):
        _ = tools.start_run("nope")
    assert len(started) == 2


def test_apply_trim_removes_only_what_the_last_scan_offered(tmp_path: Path) -> None:
    tools = _tools(tmp_path, [])
    stray = str(tmp_path / "node_modules")
    assert json.loads(tools.apply_trim([stray])) == [{"path": stray, "status": "not-in-scan", "freeable": 0}]


def test_a_refusal_reaches_the_agent_as_its_message(tmp_path: Path) -> None:
    """An expected refusal is a `ToolError`, whose message the server sends the agent; a crash would send none."""
    server = create_server(_tools(tmp_path, []))
    with pytest.raises(ToolError, match="Error executing tool run_output: No run '../../etc' of align"):
        _ = asyncio.run(server.call_tool("run_output", {"job": "align", "run_id": "../../etc"}))
