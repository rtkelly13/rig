"""The internal Python web child: its route mount and its ``--port`` entry point."""

from __future__ import annotations

import io
from collections.abc import Sequence

import pytest

from desktop_manager import web_upstream
from desktop_manager.jobs.web import JobsRoute
from rig.web_routes import Route


def test_the_child_mounts_only_the_jobs_route() -> None:
    routes = web_upstream.mounted_routes()

    assert len(routes) == 1
    assert isinstance(routes[0], JobsRoute)


def test_main_reads_the_port() -> None:
    served: list[int] = []

    web_upstream.main(["--port", "8123"], runner=served.append)

    assert served == [8123]


def test_main_refuses_a_missing_port() -> None:
    stderr = io.StringIO()

    with pytest.raises(SystemExit) as exited:
        web_upstream.main([], runner=lambda _port: pytest.fail("served without a port"), stderr=stderr)

    assert exited.value.code == 2
    assert "usage: python -m desktop_manager.web_upstream --port <port>" in stderr.getvalue()


class _Server:
    def __init__(self, host: str, port: int, routes: Sequence[Route]) -> None:
        self.host = host
        self.port = port
        self.routes = routes
        self.closed = False

    def serve_forever(self) -> None:
        raise KeyboardInterrupt

    def shutdown(self) -> None:
        pass

    def server_close(self) -> None:
        self.closed = True


def test_serve_opens_the_server_and_closes_it_on_interrupt() -> None:
    instances: list[_Server] = []

    def make_server(host: str, port: int, routes: Sequence[Route]) -> _Server:
        server = _Server(host, port, routes)
        instances.append(server)
        return server

    stdin = io.BytesIO(b"")
    web_upstream.serve(8123, server_factory=make_server, stdin=stdin)

    assert [server.port for server in instances] == [8123]
    assert instances[-1].closed
