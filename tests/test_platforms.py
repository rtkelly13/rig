"""Shared platform detection and the ``requires`` capability gate."""

from __future__ import annotations

import re

import pytest
import typer
from typer.testing import CliRunner

from rig import platforms
from rig.platforms import Platform, platform_label, requires, supported_platforms


@pytest.mark.parametrize(
    ("value", "expected"),
    [("win32", Platform.WINDOWS), ("darwin", Platform.MACOS), ("linux", Platform.LINUX), ("sunos5", Platform.UNKNOWN)],
)
def test_detection_reads_sys_platform(monkeypatch, value, expected):
    monkeypatch.setattr("sys.platform", value)
    assert platforms.get_current_platform() is expected


def test_platform_label_joins_values():
    assert platform_label((Platform.WINDOWS,)) == "Windows"
    assert platform_label((Platform.WINDOWS, Platform.MACOS)) == "Windows/macOS"


def test_requires_needs_a_platform():
    with pytest.raises(ValueError, match="at least one platform"):
        requires()


def _app() -> typer.Typer:
    app = typer.Typer()

    @app.command(name="status")
    @requires(Platform.WINDOWS)
    def status_cmd(verbose: bool = typer.Option(False, "--verbose")) -> None:
        """Show the desktops.

        More detail here.
        """
        typer.echo(f"ran verbose={verbose}")

    @app.command(name="portable")
    def portable_cmd() -> None:
        """Runs anywhere."""
        typer.echo("portable ran")

    return app


def _on(monkeypatch, current: Platform) -> None:
    monkeypatch.setattr(platforms, "get_current_platform", lambda: current)


def test_gate_runs_the_command_on_its_platform(monkeypatch):
    _on(monkeypatch, Platform.WINDOWS)
    result = CliRunner().invoke(_app(), ["status", "--verbose"])
    assert result.exit_code == 0
    assert "ran verbose=True" in result.output


def test_gate_refuses_cleanly_elsewhere(monkeypatch):
    _on(monkeypatch, Platform.LINUX)
    result = CliRunner().invoke(_app(), ["status", "--verbose"])
    assert result.exit_code == 1
    assert "status is only available on Windows." in result.output
    assert "ran" not in result.output
    assert result.exception is None or isinstance(result.exception, SystemExit)


def test_ungated_commands_still_run_elsewhere(monkeypatch):
    _on(monkeypatch, Platform.LINUX)
    result = CliRunner().invoke(_app(), ["portable"])
    assert result.exit_code == 0
    assert "portable ran" in result.output


def test_help_tags_gated_commands_only(monkeypatch):
    _on(monkeypatch, Platform.LINUX)
    output = CliRunner().invoke(_app(), ["--help"]).output
    assert "[Windows only]" in output
    assert output.count("[Windows only]") == 1


def test_help_works_off_platform(monkeypatch):
    _on(monkeypatch, Platform.LINUX)
    result = CliRunner().invoke(_app(), ["status", "--help"])
    assert result.exit_code == 0
    # CI forces colour, and Rich styles the leading "-" of an option apart from the rest.
    assert "--verbose" in re.sub(r"\x1b\[[0-9;]*m", "", result.output)


def test_declared_platforms_are_recorded():
    @requires(Platform.WINDOWS, Platform.MACOS)
    def gated() -> None:
        """Doc."""

    def plain() -> None:
        """Doc."""

    assert supported_platforms(gated) == (Platform.WINDOWS, Platform.MACOS)
    assert supported_platforms(plain) is None
    assert gated.__doc__ == "Doc. [Windows/macOS only]"


def test_docstring_less_command_still_gets_a_tag():
    @requires(Platform.WINDOWS)
    def bare() -> None:
        pass

    assert bare.__doc__ == "[Windows only]"
