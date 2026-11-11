"""The rig scripts, which start the C# rig CLI, and the Python commands it forwards back."""

from __future__ import annotations

import importlib
import re
from pathlib import Path

import pytest
import typer
from typer.testing import CliRunner

from desktop_manager.cli import app as desktop_manager_app
from rig import csharp_cli

FORWARDED = Path(__file__).resolve().parents[1] / "src" / "Rig.Cli" / "Forwarding" / "Forwarded.cs"


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


def test_a_command_runs_on_dotnet_with_the_checkout_set(tmp_path: Path) -> None:
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
            {csharp_cli.HOME_VARIABLE: str(project.parents[1])},
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


def test_the_attached_runner_adds_its_environment_to_this_one(monkeypatch: pytest.MonkeyPatch) -> None:
    # git reads GIT_CONFIG_COUNT/KEY/VALUE: the count comes from this environment, the pair from the extra one.
    monkeypatch.setenv("GIT_CONFIG_COUNT", "1")
    extra = {"GIT_CONFIG_KEY_0": "rig.probe", "GIT_CONFIG_VALUE_0": "yes"}

    assert csharp_cli._attached(["git", "config", "--get", "rig.probe"], extra) == 0  # pyright: ignore[reportPrivateUsage]
    assert csharp_cli._attached(["git", "config", "--get", "rig.absent"], extra) == 1  # pyright: ignore[reportPrivateUsage]


@pytest.mark.parametrize("script", ["/venv/bin/latex", "C:/venv/Scripts/latex.exe"])
def test_a_ported_entry_point_hands_over_as_the_rig_command_of_its_name(script: str) -> None:
    handed: list[list[str]] = []

    def handover(args: list[str]) -> int:
        handed.append(args)
        return 1

    with pytest.raises(SystemExit) as exited:
        csharp_cli.entry([script, "build", "--no-sync-data"], handover)

    assert exited.value.code == 1
    assert handed == [["latex", "build", "--no-sync-data"]]


@pytest.mark.parametrize("script", ["/venv/bin/rig", "C:/venv/Scripts/rig.exe"])
def test_the_rig_script_hands_its_line_over_as_typed(script: str) -> None:
    handed: list[list[str]] = []

    def handover(args: list[str]) -> int:
        handed.append(args)
        return 0

    with pytest.raises(SystemExit):
        csharp_cli.entry([script, "gh", "threads", "list", "7"], handover)

    assert handed == [["gh", "threads", "list", "7"]]


def _launches() -> list[tuple[str, list[str]]]:
    rows = re.findall(r'\("(?P<name>[\w-]+)", "[^"]*", \[(?P<launch>[^\]]*)\]\)', FORWARDED.read_text(encoding="utf-8"))
    return [(name, re.findall(r'"([^"]+)"', launch)) for name, launch in rows]


def test_the_forwarded_table_is_read() -> None:
    assert {name for name, _ in _launches()} >= {"triage", "web"}


@pytest.mark.parametrize(("name", "launch"), _launches(), ids=[name for name, _ in _launches()])
def test_every_forwarded_command_reaches_a_python_program(name: str, launch: list[str]) -> None:
    # The C# CLI starts `uv run … <launch> <args>`: a desktop-manager command, or a module run with python -m.
    if launch[:2] == ["python", "-m"]:
        module = importlib.import_module(launch[2])
        source = Path(module.__file__ or "").read_text(encoding="utf-8")
        assert 'if __name__ == "__main__":' in source, f"{launch[2]} has no __main__ block"
        app: typer.Typer = module.app
        args = launch[3:]
    else:
        assert launch[0] == "desktop-manager", f"rig {name} launches {launch[0]}: not python -m or desktop-manager"
        app, args = desktop_manager_app, launch[1:]

    result = CliRunner().invoke(app, [*args, "--help"], prog_name=f"rig {name}")

    assert result.exit_code == 0, result.output


def test_a_python_command_runs_as_the_rig_command_of_its_name(capsys: pytest.CaptureFixture[str]) -> None:
    from rig.python_command import main

    app = typer.Typer()
    ran: list[str] = []
    _ = app.command("go")(lambda: ran.append("go"))
    _ = app.command("stop")(lambda: None)

    with pytest.raises(SystemExit) as exited:
        main(app, "triage", ["--help"])

    assert exited.value.code == 0
    assert "rig triage" in capsys.readouterr().out
    with pytest.raises(SystemExit):
        main(app, "triage", ["go"])
    assert ran == ["go"]
