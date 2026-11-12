"""The jobs coordinator's `/api/ops/jobs` and `/api/ops/runs` routes."""

from __future__ import annotations

import json
import sys
import threading
import urllib.error
import urllib.request
from collections.abc import Iterator, Sequence
from dataclasses import dataclass
from datetime import datetime
from pathlib import Path

import pytest

from desktop_manager.jobs.history import JobRun, append_run, save_output
from desktop_manager.jobs.registry import Cadence, Job, Trigger
from desktop_manager.jobs.state import STATE_NAME, Outcome, jobs_dir, write_state
from desktop_manager.jobs.web import JobsRoute, cadence_text, job_views, run_command, run_output_view, run_views
from desktop_manager.web_cli import mounted_routes
from rig.web_host.server import StudioServer
from rig.web_routes import RouteNotFoundError

pytestmark = pytest.mark.ci

STARTED = datetime(2026, 10, 10, 9, 0).astimezone()


def _job(name: str, cadence: Cadence = Cadence.EVERY_RUN, *, idle: int = 0, feature: str | None = None) -> Job:
    return Job(
        name=name,
        description=f"{name} things",
        argv=("tool", name),
        triggers=frozenset({Trigger.TICK, Trigger.LOGON}),
        cadence=cadence,
        timeout_minutes=5,
        idle_minutes=idle,
        feature=feature,
    )


JOBS = (_job("align", idle=3), _job("estate-sync", Cadence.DAILY))


def _stamp(minutes: int) -> str:
    return STARTED.replace(minute=minutes).isoformat(timespec="seconds")


def _seed(directory: Path) -> None:
    """Two recorded runs of align, and an older estate-sync outcome from before the history existed."""
    output = save_output(directory, "align", "tick-20261010-091500", b"align: 0 moved\n")
    append_run(directory, JobRun("align", "tick-20261010-090000", _stamp(0), 1.0, 0, "tick"))
    append_run(
        directory,
        JobRun("align", "tick-20261010-091500", _stamp(15), 2.0, 3, "tick", output=output, trace_id="ab" * 16),
    )
    log = directory / "logs" / "estate-sync.log"
    log.parent.mkdir(parents=True, exist_ok=True)
    log.write_text("--- 2026-10-10 08:30:00 [logon-20261010-083000] estate sync\nsynced 12\n", encoding="utf-8")
    write_state(
        directory / STATE_NAME,
        {
            "align": Outcome(_stamp(15), 2.0, 3, "tick", run_id="tick-20261010-091500"),
            "estate-sync": Outcome(
                _stamp(0).replace("09:00", "08:30"), 4.0, 0, "logon", run_id="logon-20261010-083000"
            ),
        },
    )


def test_cadence_reads_as_the_docs_table_does() -> None:
    assert cadence_text(_job("a", idle=3)) == "every run, idle 3 min"
    assert cadence_text(_job("b", Cadence.ONCE_PER_BOOT, feature="restore")) == "once per boot, flag restore"
    assert cadence_text(_job("c", Cadence.DAILY)) == "daily"


def test_jobs_join_the_registry_with_each_last_outcome(tmp_path: Path) -> None:
    _seed(tmp_path)
    align, sync = job_views([*JOBS, _job("never")], tmp_path)[:2]
    assert align.triggers == ("logon", "tick")
    assert align.last is not None
    assert (align.last.returncode, align.last.ok) == (3, False)
    assert sync.last is not None
    assert sync.last.ok
    assert job_views([_job("never")], tmp_path)[0].last is None


def test_runs_merge_the_history_with_outcomes_it_does_not_hold(tmp_path: Path) -> None:
    _seed(tmp_path)
    views = run_views(tmp_path)
    assert [(view.job, view.run_id) for view in views] == [
        ("align", "tick-20261010-091500"),
        ("align", "tick-20261010-090000"),
        ("estate-sync", "logon-20261010-083000"),
    ]
    assert [view.has_output for view in views] == [True, False, True]
    assert views[0].trace_id == "ab" * 16
    assert views[2].trace_id == ""
    assert [view.run_id for view in run_views(tmp_path, job="align", limit=1)] == ["tick-20261010-091500"]


def test_output_comes_from_the_history_or_the_job_log(tmp_path: Path) -> None:
    _seed(tmp_path)
    assert run_output_view(JOBS, tmp_path, "align", "tick-20261010-091500").output == "align: 0 moved\n"
    assert run_output_view(JOBS, tmp_path, "align", "tick-20261010-090000").output is None
    assert run_output_view(JOBS, tmp_path, "estate-sync", "logon-20261010-083000").output == "synced 12\n"


@pytest.mark.parametrize(
    ("job", "run_id"),
    [
        ("nope", "tick-20261010-091500"),
        ("align", "tick-20261010-101500"),
        ("align", "..%2F..%2Fstate"),
        ("..", "tick-20261010-091500"),
    ],
)
def test_an_unknown_job_or_run_is_not_found(tmp_path: Path, job: str, run_id: str) -> None:
    _seed(tmp_path)
    with pytest.raises(RouteNotFoundError):
        run_output_view(JOBS, tmp_path, job, run_id)


def test_the_route_is_mounted_into_rig_web() -> None:
    assert any(isinstance(route, JobsRoute) for route in mounted_routes())


def test_the_route_reads_the_coordinators_own_directory() -> None:
    assert JobsRoute().directory() == jobs_dir()


@dataclass(frozen=True, slots=True)
class Reply:
    status: int
    body: str


@pytest.fixture
def started() -> list[list[str]]:
    return []


@pytest.fixture
def base_url(tmp_path: Path, started: list[list[str]]) -> Iterator[str]:
    directory = tmp_path / "jobs"
    _seed(directory)
    server = StudioServer(
        port=0,
        db_path=tmp_path / "rig.db",
        routes=(
            JobsRoute(jobs=lambda: JOBS, directory=lambda: directory, start=lambda command: _record(started, command)),
        ),
    )
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    try:
        yield f"http://127.0.0.1:{server.server_address[1]}"
    finally:
        server.shutdown()
        server.server_close()


def call(url: str) -> Reply:
    try:
        with urllib.request.urlopen(url, timeout=5) as response:  # noqa: S310 - loopback test server
            return Reply(response.status, response.read().decode("utf-8"))
    except urllib.error.HTTPError as error:
        return Reply(error.code, error.read().decode("utf-8"))


def test_the_endpoints_answer_through_the_rig_web_server(base_url: str) -> None:
    jobs = call(f"{base_url}/api/ops/jobs")
    assert jobs.status == 200
    assert json.loads(jobs.body)[0] == {
        "name": "align",
        "description": "align things",
        "cadence": "every run, idle 3 min",
        "triggers": ["logon", "tick"],
        "timeout_minutes": 5,
        "last": {"started": _stamp(15), "seconds": 2.0, "returncode": 3, "trigger": "tick", "error": None, "ok": False},
    }

    runs = call(f"{base_url}/api/ops/runs?job=align&limit=1")
    assert runs.status == 200
    assert json.loads(runs.body) == [
        {
            "job": "align",
            "run_id": "tick-20261010-091500",
            "started": _stamp(15),
            "seconds": 2.0,
            "returncode": 3,
            "trigger": "tick",
            "error": None,
            "ok": False,
            "has_output": True,
            "trace_id": "ab" * 16,
        }
    ]

    output = call(f"{base_url}/api/ops/runs/align/tick-20261010-091500/output")
    assert json.loads(output.body) == {"job": "align", "run_id": "tick-20261010-091500", "output": "align: 0 moved\n"}


@pytest.mark.parametrize(
    ("path", "status"),
    [
        ("/api/ops/runs/align/tick-20261010-101500/output", 404),
        ("/api/ops/runs/nope/tick-20261010-091500/output", 404),
        ("/api/ops/runs/align/..%2F..%2Fstate.json/output", 404),
        ("/api/ops/runs/align", 404),
        ("/api/ops/runs?limit=0", 400),
    ],
)
def test_bad_requests_get_the_shared_error_envelope(base_url: str, path: str, status: int) -> None:
    reply = call(base_url + path)
    assert reply.status == status
    assert set(json.loads(reply.body)) == {"error"}


def _record(started: list[list[str]], command: Sequence[str]) -> int:
    started.append(list(command))
    return 4242


def post(url: str, body: str, *, content_type: str = "application/json", origin: str | None = None) -> Reply:
    headers = {"Content-Type": content_type}
    if origin is not None:
        headers["Origin"] = origin
    request = urllib.request.Request(url, data=body.encode("utf-8"), headers=headers, method="POST")
    try:
        with urllib.request.urlopen(request, timeout=5) as response:  # noqa: S310 - loopback test server
            return Reply(response.status, response.read().decode("utf-8"))
    except urllib.error.HTTPError as error:
        return Reply(error.code, error.read().decode("utf-8"))


def test_run_now_starts_the_tick_for_one_job_or_every_due_one() -> None:
    assert run_command("align")[-6:] == ["jobs", "run", "--trigger", "tick", "--only", "align"]
    assert run_command(None)[-4:] == ["jobs", "run", "--trigger", "tick"]
    assert run_command(None)[:3] == [sys.executable, "-m", "desktop_manager.cli"]


def test_run_now_answers_at_once_with_the_started_process(base_url: str, started: list[list[str]]) -> None:
    one = post(f"{base_url}/api/ops/jobs/run", '{"job": "align"}')
    assert (one.status, json.loads(one.body)) == (200, {"job": "align", "pid": 4242})
    due = post(f"{base_url}/api/ops/jobs/run", "{}")
    assert (due.status, json.loads(due.body)) == (200, {"job": None, "pid": 4242})
    assert [command[-2:] for command in started] == [["--only", "align"], ["--trigger", "tick"]]


@pytest.mark.parametrize(
    ("body", "content_type", "origin", "status"),
    [
        ('{"job": "nope"}', "application/json", None, 404),
        ('{"job": 3}', "application/json", None, 400),
        ("not json", "application/json", None, 400),
        ('{"job": "align"}', "text/plain", None, 400),
        ('{"job": "align"}', "application/json", "http://evil.example", 400),
    ],
)
def test_run_now_refuses_what_it_cannot_trust(
    base_url: str, started: list[list[str]], body: str, content_type: str, origin: str | None, status: int
) -> None:
    reply = post(f"{base_url}/api/ops/jobs/run", body, content_type=content_type, origin=origin)
    assert reply.status == status
    assert set(json.loads(reply.body)) == {"error"}
    assert started == []


def test_run_now_is_post_only(base_url: str, started: list[list[str]]) -> None:
    assert call(f"{base_url}/api/ops/jobs/run").status == 400
    assert started == []
