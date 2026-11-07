from __future__ import annotations

import socket

import pytest
from typer.testing import CliRunner

from desktop_manager import service_installer
from desktop_manager.web_cli import web_app

pytestmark = pytest.mark.ci


def test_a_listening_port_answers() -> None:
    with socket.socket() as server:
        server.bind(("127.0.0.1", 0))
        server.listen()
        port = server.getsockname()[1]
        assert service_installer.answering(port)
    assert not service_installer.answering(port)


def test_a_running_dashboard_is_left_alone(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setattr(service_installer, "answering", lambda port, host="127.0.0.1": True)
    monkeypatch.setattr(service_installer, "install_web_service", lambda **_: pytest.fail("restarted it"))

    assert service_installer.ensure_web_service(port=8090)


def test_a_stopped_dashboard_is_started_and_awaited(monkeypatch: pytest.MonkeyPatch) -> None:
    answers = iter([False, False, True])
    installed: list[int | None] = []
    monkeypatch.setattr(service_installer, "answering", lambda port, host="127.0.0.1": next(answers))
    monkeypatch.setattr(service_installer, "install_web_service", lambda **kw: installed.append(kw["port"]) or "ok")
    monkeypatch.setattr(service_installer.time, "sleep", lambda _s: None)

    assert service_installer.ensure_web_service(port=8095)
    assert installed == [8095]


def test_ensure_exits_1_when_nothing_answers(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setattr(service_installer, "ensure_web_service", lambda port, timeout_s: False)

    result = CliRunner().invoke(web_app, ["ensure", "--timeout", "0"])

    assert result.exit_code == 1
    assert "Nothing answered on port 8090" in result.output
