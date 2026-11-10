"""The bridge that hands ported commands from the Python rig CLI to the C# one."""

from __future__ import annotations

from pathlib import Path

import pytest
from typer.testing import CliRunner

from rig import csharp_cli, tracing
from rig.rig_cli import app


def _project(tmp_path: Path) -> Path:
    project = tmp_path / "src" / "Rig.Cli"
    project.mkdir(parents=True)
    (project / "Rig.Cli.csproj").write_text("<Project />", encoding="utf-8")
    return project


def test_the_project_is_found_beside_the_module(tmp_path: Path) -> None:
    module = tmp_path / "src" / "rig" / "csharp_cli.py"

    assert csharp_cli.cli_project(module) == tmp_path.resolve() / "src" / "Rig.Cli"


def test_the_default_project_is_this_checkouts() -> None:
    assert (csharp_cli.cli_project() / "Rig.Cli.csproj").is_file()


def test_a_ported_command_runs_on_dotnet_with_the_bridge_variable_set(tmp_path: Path) -> None:
    project = _project(tmp_path)
    calls: list[tuple[list[str], dict[str, str]]] = []

    def runner(command: list[str], env: dict[str, str]) -> int:
        calls.append((command, env))
        return 1

    code = csharp_cli.run(["gh", "threads", "list", "7"], project=project, runner=runner)

    assert code == 1
    assert calls == [
        (
            ["dotnet", "run", "--project", str(project), "--", "gh", "threads", "list", "7"],
            {csharp_cli.BRIDGE_VARIABLE: "1"},
        )
    ]


def test_a_missing_project_fails_with_a_pointer(tmp_path: Path, capsys: pytest.CaptureFixture[str]) -> None:
    code = csharp_cli.run(["gh"], project=tmp_path, runner=lambda _c, _e: 0)

    assert code == 2
    assert "rig gh: this command now runs on the C# rig CLI" in capsys.readouterr().err


def test_missing_dotnet_fails_with_a_pointer(tmp_path: Path, capsys: pytest.CaptureFixture[str]) -> None:
    def runner(_command: list[str], _env: dict[str, str]) -> int:
        raise FileNotFoundError("dotnet")

    code = csharp_cli.run(["gh"], project=_project(tmp_path), runner=runner)

    assert code == 2
    assert "needs dotnet on PATH" in capsys.readouterr().err


def test_the_attached_runner_passes_the_environment_through(monkeypatch: pytest.MonkeyPatch) -> None:
    seen: dict[str, object] = {}

    class Done:
        returncode: int = 3

    def fake_run(command: list[str], *, scope: str, env: dict[str, str]) -> Done:
        seen.update(command=command, scope=scope, env=env)
        return Done()

    monkeypatch.setenv("RIG_TEST_KEPT", "yes")
    monkeypatch.setattr(tracing, "run", fake_run)

    assert csharp_cli._attached(["dotnet", "--info"], {"EXTRA": "1"}) == 3  # pyright: ignore[reportPrivateUsage]
    env = seen["env"]
    assert isinstance(env, dict)
    assert env["EXTRA"] == "1"
    assert env["RIG_TEST_KEPT"] == "yes"
    assert seen["scope"] == "rig.csharp_cli"


def test_the_python_cli_hands_the_whole_line_over(monkeypatch: pytest.MonkeyPatch) -> None:
    handed: list[list[str]] = []

    def fake(args: list[str]) -> int:
        handed.append(args)
        return 1

    monkeypatch.setattr(csharp_cli, "run", fake)

    result = CliRunner().invoke(app, ["gh", "threads", "list", "7", "--json", "--help"])

    assert result.exit_code == 1
    assert handed == [["gh", "threads", "list", "7", "--json", "--help"]]
