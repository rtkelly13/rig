"""Routes mounted into the rig web server, and the shared JSON and write-guard helpers."""

from __future__ import annotations

import email.message
import io
import json
import threading
import urllib.error
import urllib.request
from collections.abc import Iterator
from dataclasses import dataclass
from http.server import BaseHTTPRequestHandler
from pathlib import Path

import pytest

from rig.image_drift.server import StudioServer
from rig.web_routes import Request, RouteNotFoundError, json_body, respond

pytestmark = pytest.mark.ci


@dataclass(frozen=True, slots=True)
class Reply:
    status: int
    body: str


def echo(handler: BaseHTTPRequestHandler, request: Request) -> bool:
    """A mounted route covering each answer `respond` can give."""
    if not request.path.startswith("/api/test/"):
        return False

    def produce() -> str:
        if request.path == "/api/test/missing":
            raise RouteNotFoundError("no such thing")
        if request.path == "/api/test/down":
            raise OSError("store is down")
        if request.method == "POST":
            return json_body(handler)
        limit = request.int_param("limit", default=5, low=1, high=10)
        return json.dumps({"method": request.method, "limit": limit, "name": request.param("name")})

    return respond(handler, produce)


@pytest.fixture
def base_url(tmp_path: Path) -> Iterator[str]:
    templates = tmp_path / "templates"
    templates.mkdir()
    (templates / "index.html").write_text("<!doctype html><title>app</title>", encoding="utf-8")
    server = StudioServer(
        port=0,
        templates_dir=templates,
        db_path=tmp_path / "rig.db",
        assets_root=tmp_path / "assets",
        variations_root=tmp_path / "variations",
        routes=(echo,),
    )
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    try:
        yield f"http://127.0.0.1:{server.server_address[1]}"
    finally:
        server.shutdown()
        server.server_close()


def call(url: str, *, body: bytes | None = None, headers: dict[str, str] | None = None) -> Reply:
    request = urllib.request.Request(url, data=body, headers=headers or {}, method="POST" if body else "GET")
    try:
        with urllib.request.urlopen(request, timeout=5) as response:  # noqa: S310 - loopback test server
            return Reply(response.status, response.read().decode("utf-8"))
    except urllib.error.HTTPError as error:
        return Reply(error.code, error.read().decode("utf-8"))


def test_a_mounted_route_answers_before_the_built_in_handlers(base_url: str) -> None:
    reply = call(f"{base_url}/api/test/echo?limit=7&name=jobs")
    assert reply.status == 200
    assert json.loads(reply.body) == {"method": "GET", "limit": 7, "name": "jobs"}


def test_a_route_that_declines_leaves_the_built_in_handlers_in_charge(base_url: str) -> None:
    assert call(f"{base_url}/api/no-such-endpoint").status == 404


@pytest.mark.parametrize(
    ("path", "status"),
    [
        ("/api/test/missing", 404),
        ("/api/test/down", 503),
        ("/api/test/echo?limit=99", 400),
        ("/api/test/echo?limit=x", 400),
    ],
)
def test_failures_share_one_error_envelope(base_url: str, path: str, status: int) -> None:
    reply = call(base_url + path)
    assert reply.status == status
    assert set(json.loads(reply.body)) == {"error"}


def test_a_json_write_from_the_dashboard_is_accepted(base_url: str) -> None:
    reply = call(f"{base_url}/api/test/write", body=b'{"ok": true}', headers={"Content-Type": "application/json"})
    assert reply == Reply(200, '{"ok": true}')


@pytest.mark.parametrize(
    "headers",
    [
        {"Content-Type": "application/x-www-form-urlencoded"},
        {"Content-Type": "application/json", "Origin": "http://evil.example"},
        {"Content-Type": "application/json", "Host": "evil.example"},
    ],
    ids=["form-post", "cross-origin", "rebound-host"],
)
def test_writes_from_elsewhere_are_refused(base_url: str, headers: dict[str, str]) -> None:
    assert call(f"{base_url}/api/test/write", body=b"{}", headers=headers).status == 400


@pytest.mark.parametrize("path", ["/ops", "/ops/jobs", "/ops/runs/backup"])
def test_ops_pages_are_app_deep_links(base_url: str, path: str) -> None:
    reply = call(base_url + path)
    assert reply.status == 200
    assert "<title>app</title>" in reply.body


def test_request_reads_the_last_non_empty_query_value() -> None:
    request = Request.parse("GET", "/api/x?a=1&a=2&empty=&on=true")
    assert request.param("a") == "2"
    assert request.param("empty") is None
    assert request.param("absent") is None
    assert request.flag("on")
    assert not request.flag("absent")


@pytest.mark.parametrize(
    "headers",
    [
        {"Content-Type": "text/plain"},
        {"Content-Type": "application/json", "Origin": "http://evil.example"},
        {"Content-Type": "application/json", "Host": "evil.example"},
    ],
    ids=["form-post", "cross-origin", "rebound-host"],
)
def test_a_refused_write_still_reads_its_body(headers: dict[str, str]) -> None:
    """An unread body makes Windows reset the socket on close, so the client never sees the 400."""
    body = b'{"paths": []}'
    handler = BaseHTTPRequestHandler.__new__(BaseHTTPRequestHandler)
    handler.headers = email.message.Message()
    for name, value in {"Host": "127.0.0.1:8090", "Content-Length": str(len(body)), **headers}.items():
        handler.headers[name] = value
    handler.rfile = io.BytesIO(body)
    with pytest.raises(ValueError, match="loopback|Cross-origin|application/json"):
        json_body(handler)
    assert handler.rfile.read() == b""
