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
    assert csharp_calls[0][-7:] == ["web", "--port", "8090", "--host", "127.0.0.1", "--open", "--preview"]


def test_upstream_stops_when_its_standard_input_closes(csharp_calls: list[list[str]]) -> None:
    # Port 0 is ephemeral; empty input is an immediate EOF, the C# host's stop signal.
    result = CliRunner().invoke(web_app, ["upstream", "--port", "0"], input="")

    assert result.exit_code == 0, result.output
    assert csharp_calls == []
