"""Legacy runtime spellings delegate every argument to the C# owner."""

from __future__ import annotations

import pytest
from typer.testing import CliRunner

from desktop_manager.cli import app
from rig import csharp_cli

pytestmark = pytest.mark.ci


@pytest.mark.parametrize(
    "arguments",
    [[], ["--help"], ["--unknown", "a b"], ["--ref", "HEAD", "--no-fetch"]],
)
@pytest.mark.parametrize("command", ["install", "rollback", "status"])
def test_runtime_passes_the_whole_line_and_exit_code(command: str, arguments: list[str]) -> None:
    calls: list[list[str]] = []

    def send(argv: list[str], _environment: dict[str, str]) -> int:
        calls.append(argv)
        return 3

    token = csharp_cli.RUNNER.set(send)
    try:
        result = CliRunner().invoke(app, ["runtime", command, *arguments])
    finally:
        csharp_cli.RUNNER.reset(token)

    assert result.exit_code == 3, result.output
    assert len(calls) == 1
    assert calls[0][calls[0].index("--") + 1 :] == ["runtime", command, *arguments]
