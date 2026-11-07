"""The /ops pages of the rig SPA, driven against canned API answers.

The real routes come from the otel, estate and jobs features; here one fake route
answers the same JSON contract, so the pages are tested without live state.
"""

from __future__ import annotations

import json
import threading
from collections.abc import Iterator
from dataclasses import dataclass, field
from http.server import BaseHTTPRequestHandler
from pathlib import Path

import pytest
from playwright.sync_api import Page, expect, sync_playwright

from rig.image_drift.server import StudioServer
from rig.web_routes import Request, json_body, send_json

pytestmark = pytest.mark.ci

TRACE = "0af7651916cd43dd8448eb211c80319c"
EXPLORERS = {
    "hyperdx_url": "http://localhost:8080",
    "hyperdx_reachable": True,
    "hyperdx_trace_url": "http://localhost:8080/trace/{trace_id}",
    "aspire_url": "",
    "aspire_reachable": False,
    "aspire_trace_url": "",
}
JOBS = [
    {
        "name": "sync",
        "description": "Fetch every managed repo",
        "cadence": "hourly",
        "triggers": ["tick"],
        "timeout_minutes": 10,
        "last": {
            "started": "2026-11-05T09:00:00+00:00",
            "seconds": 4.2,
            "returncode": 1,
            "trigger": "tick",
            "error": None,
            "ok": False,
        },
    },
    {
        "name": "prune",
        "description": "Prune merged branches",
        "cadence": "daily",
        "triggers": ["tick", "manual"],
        "timeout_minutes": 5,
        "last": None,
    },
]
RUNS = [
    {
        "job": "sync",
        "run_id": "r2",
        "started": "2026-11-05T09:00:00+00:00",
        "seconds": 4.2,
        "returncode": 1,
        "trigger": "tick",
        "error": None,
        "ok": False,
        "has_output": True,
        "trace_id": TRACE,
    }
]
DECISIONS = {
    "decisions": [
        {
            "kind": "unpushed",
            "repo": "rig",
            "path": "C:/code/personal/rig/.worktrees/old",
            "reason": "idle with unpushed commits",
            "size_bytes": 2048,
            "suggested_action": "push or delete",
            "command": "git -C C:/code/personal/rig/.worktrees/old push",
        }
    ],
    "written": "2026-11-05T08:00:00+00:00",
    "note": "",
}
TRIM = {
    "scanned_at": "2026-11-05T09:30:00+00:00",
    "candidates": [
        {
            "worktree": "C:/code/personal/blog/.worktrees/a",
            "path": "C:/code/personal/blog/.worktrees/a/node_modules",
            "category": "node_modules",
            "freeable": 300_000_000,
            "refused": "",
        },
        {
            "worktree": "C:/code/personal/blog/.worktrees/b",
            "path": "C:/code/personal/blog/.worktrees/b/node_modules",
            "category": "node_modules",
            "freeable": 100_000_000,
            "refused": "dirty worktree",
        },
    ],
    "scanning": False,
    "error": "",
}


@dataclass
class FakeOps:
    applied: list[object] = field(default_factory=list)
    started: list[object] = field(default_factory=list)

    def __call__(self, handler: BaseHTTPRequestHandler, request: Request, /) -> bool:
        answers: dict[str, object] = {
            "/api/ops/jobs": JOBS,
            "/api/ops/runs": RUNS,
            "/api/ops/runs/sync/r2/output": {"job": "sync", "run_id": "r2", "output": "fatal: remote hung up"},
            "/api/ops/telemetry/status": {
                "store": {"source": "clickhouse", "clickhouse_reachable": True, "detail": "ClickHouse answered."},
                "explorers": EXPLORERS,
            },
            "/api/ops/telemetry/traces": {
                "source": "clickhouse",
                "note": "",
                "traces": [
                    {
                        "trace_id": TRACE,
                        "service": "rig",
                        "root": "job.sync",
                        "start_ns": 1_793_000_000_000_000_000,
                        "duration_ms": 4200,
                        "spans": 7,
                        "errors": 1,
                    }
                ],
            },
            "/api/ops/decisions": DECISIONS,
            "/api/ops/estate/disk": {"path": "C:\\", "total": 1000, "used": 950, "free": 50},
            "/api/ops/estate/trim": TRIM,
        }
        if request.method == "POST" and request.path == "/api/ops/jobs/run":
            body = json.loads(json_body(handler))
            self.started.append(body)
            send_json(handler, json.dumps({"job": body["job"], "pid": 4242}))
            return True
        if request.method == "POST" and request.path == "/api/ops/estate/trim/apply":
            body = json.loads(json_body(handler))
            self.applied.append(body)
            send_json(
                handler,
                json.dumps([{"path": path, "status": "removed", "freeable": 300_000_000} for path in body["paths"]]),
            )
            return True
        if request.method != "GET" or request.path not in answers:
            return False
        send_json(handler, json.dumps(answers[request.path]))
        return True


@dataclass(frozen=True, slots=True)
class OpsApp:
    url: str
    page: Page
    ops: FakeOps


@pytest.fixture
def ops_app(tmp_path: Path) -> Iterator[OpsApp]:
    ops = FakeOps()
    server = StudioServer(
        port=0,
        db_path=tmp_path / "rig.db",
        assets_root=tmp_path / "assets",
        variations_root=tmp_path / "variations",
        routes=(ops,),
    )
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    with sync_playwright() as playwright:
        browser = playwright.chromium.launch()
        page = browser.new_page(viewport={"width": 1440, "height": 1000})
        try:
            yield OpsApp(f"http://127.0.0.1:{server.server_address[1]}", page, ops)
        finally:
            browser.close()
            server.shutdown()
            server.server_close()
            thread.join()


def test_attention_lists_failed_jobs_and_decisions_and_opens_the_run(ops_app: OpsApp) -> None:
    page = ops_app.page
    page.goto(f"{ops_app.url}/ops")
    expect(page.get_by_role("heading", name="Attention", level=1)).to_be_visible()
    expect(page).to_have_title("Rig / Operations / Attention")
    expect(page.get_by_text("sync failed")).to_be_visible()
    expect(page.get_by_text("unpushed: rig")).to_be_visible()
    page.get_by_role("button", name="Open the run's output").click()
    expect(page).to_have_url(f"{ops_app.url}/ops/runs?job=sync&run=r2")
    expect(page.get_by_text("fatal: remote hung up")).to_be_visible()


def test_jobs_link_to_their_runs_and_runs_link_to_traces(ops_app: OpsApp) -> None:
    page = ops_app.page
    page.goto(f"{ops_app.url}/ops/jobs")
    expect(page.get_by_role("table")).to_contain_text("Fetch every managed repo")
    expect(page.get_by_role("table")).to_contain_text("never")
    page.get_by_role("button", name="sync", exact=True).click()
    expect(page).to_have_url(f"{ops_app.url}/ops/runs?job=sync")
    trace = page.get_by_role("link", name="HyperDX")
    expect(trace).to_have_attribute("href", f"http://localhost:8080/trace/{TRACE}")
    page.get_by_role("button", name="View").click()
    expect(page.get_by_text("fatal: remote hung up")).to_be_visible()
    page.get_by_role("button", name="Close").click()
    expect(page).to_have_url(f"{ops_app.url}/ops/runs?job=sync")


def test_telemetry_lists_traces_with_explorer_links(ops_app: OpsApp) -> None:
    page = ops_app.page
    page.goto(f"{ops_app.url}/ops/telemetry")
    expect(page.get_by_text("Source: ClickHouse")).to_be_visible()
    expect(page.get_by_role("table")).to_contain_text("job.sync")
    expect(page.get_by_role("link", name="Open HyperDX")).to_have_attribute("href", "http://localhost:8080")


def test_decisions_show_the_suggested_command(ops_app: OpsApp) -> None:
    page = ops_app.page
    page.goto(f"{ops_app.url}/ops/decisions")
    expect(page.get_by_role("table")).to_contain_text("idle with unpushed commits")
    expect(page.get_by_text("git -C C:/code/personal/rig/.worktrees/old push")).to_be_visible()


def test_trim_applies_only_the_selected_safe_paths(ops_app: OpsApp) -> None:
    page = ops_app.page
    page.goto(f"{ops_app.url}/ops/trim")
    expect(page.get_by_role("table")).to_contain_text("dirty worktree")
    refused = page.get_by_role("checkbox", name="Trim C:/code/personal/blog/.worktrees/b/node_modules")
    expect(refused).to_be_disabled()
    page.get_by_role("checkbox", name="Trim C:/code/personal/blog/.worktrees/a/node_modules").check()
    page.get_by_role("button", name="Trim 1 selected").click()
    page.get_by_role("alertdialog").get_by_role("button", name="Trim").click()
    expect(page.get_by_text("Removed 1 of 1")).to_be_visible()
    assert ops_app.ops.applied == [{"paths": ["C:/code/personal/blog/.worktrees/a/node_modules"]}]


def test_unknown_ops_page_is_not_found(ops_app: OpsApp) -> None:
    page = ops_app.page
    page.goto(f"{ops_app.url}/ops/nope")
    expect(page.get_by_role("heading", name="Page not found")).to_be_visible()


def test_jobs_start_one_job_or_every_due_job_in_the_background(ops_app: OpsApp) -> None:
    page = ops_app.page
    page.goto(f"{ops_app.url}/ops/jobs")
    page.get_by_role("button", name="Run due jobs").click()
    expect(page.get_by_text("Started a run of the due jobs")).to_be_visible()
    page.get_by_role("button", name="Run sync now").click()
    expect(page.get_by_text("Started sync")).to_be_visible()
    assert ops_app.ops.started == [{"job": None}, {"job": "sync"}]
