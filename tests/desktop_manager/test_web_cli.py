"""``rig web``: the bare command runs the C# host, which serves the Python routes through ``rig web upstream``."""

from __future__ import annotations

import pytest
from typer.testing import CliRunner

from desktop_manager.web_cli import web_app

pytestmark = pytest.mark.ci


def test_the_bare_command_hands_over_to_the_csharp_host(csharp_calls: list[list[str]]) -> None:
    result = CliRunner().invoke(web_app, ["--port", "8095", "--host", "0.0.0.0", "--no-open"])

    assert result.exit_code == 0
    assert csharp_calls[0][-6:] == ["web", "--port", "8095", "--host", "0.0.0.0", "--no-open"]


def test_preview_is_handed_over_too(csharp_calls: list[list[str]]) -> None:
    result = CliRunner().invoke(web_app, ["--preview"])

    assert result.exit_code == 0
    # The bare line goes over whole; C# applies its own defaults (port 8090 becomes 8091 in preview mode).
    assert csharp_calls[0][-2:] == ["web", "--preview"]


def test_a_child_command_does_not_run_the_bare_forwarder(csharp_calls: list[list[str]]) -> None:
    # The group callback is a thin C# dispatcher; it must not run in front of a Python child.
    result = CliRunner().invoke(web_app, ["install", "--help"])

    assert result.exit_code == 0
    assert "Install preview background service" in result.output
    assert csharp_calls == []


def test_upstream_stops_when_its_standard_input_closes(csharp_calls: list[list[str]]) -> None:
    # Port 0 is ephemeral; empty input is an immediate EOF, the C# host's stop signal.
    result = CliRunner().invoke(web_app, ["upstream", "--port", "0"], input="")

    assert result.exit_code == 0, result.output
    assert csharp_calls == []
