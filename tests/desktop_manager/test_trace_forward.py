"""The old spelling reaches the single canonical parser, including help and refusals."""

from __future__ import annotations

import pytest
from typer.testing import CliRunner

from desktop_manager.cli import app
from rig import csharp_cli

pytestmark = pytest.mark.ci


@pytest.mark.parametrize(
    "arguments",
    [
        ["runs", "--json", "--dir", "a folder"],
        ["show", "missing"],
        ["summary", "--days", "7"],
        ["prune", "--dry-run"],
        ["--help"],
        ["new-command", "--unknown"],
        [],
    ],
)
def test_trace_alias_passes_the_whole_line_and_exit_code(arguments: list[str]) -> None:
    calls: list[list[str]] = []

    def send(command: list[str], _environment: dict[str, str]) -> int:
        calls.append(command)
        return 2

    token = csharp_cli.RUNNER.set(send)
    try:
        result = CliRunner().invoke(app, ["chrome-tabs", "traces", *arguments])
    finally:
        csharp_cli.RUNNER.reset(token)

    assert result.exit_code == 2, result.output
    assert len(calls) == 1
    assert calls[0][calls[0].index("--") + 1 :] == ["chrome-tabs", "traces", *arguments]
