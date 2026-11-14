"""The legacy uninstall spelling delegates every argument to the C# owner."""

from __future__ import annotations

import pytest
from typer.testing import CliRunner

from desktop_manager.cli import app
from rig import csharp_cli

pytestmark = pytest.mark.ci


@pytest.mark.parametrize("arguments", [[], ["--help"], ["--unknown", "a b"]])
def test_jobs_uninstall_passes_the_whole_line_and_exit_code(arguments: list[str]) -> None:
    calls: list[list[str]] = []

    def send(command: list[str], _environment: dict[str, str]) -> int:
        calls.append(command)
        return 3

    token = csharp_cli.RUNNER.set(send)
    try:
        result = CliRunner().invoke(app, ["jobs", "uninstall", *arguments])
    finally:
        csharp_cli.RUNNER.reset(token)

    assert result.exit_code == 3, result.output
    assert len(calls) == 1
    assert calls[0][calls[0].index("--") + 1 :] == ["jobs", "uninstall", *arguments]
