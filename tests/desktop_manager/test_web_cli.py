"""``rig web``: the bare command runs the C# host, which serves its own routes and forwards the rest upstream."""

from __future__ import annotations

from pathlib import Path

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


def test_web_service_commands_delegate_to_installer(monkeypatch: pytest.MonkeyPatch) -> None:
    from desktop_manager import service_installer

    def fake_install(preview: bool = False, port: int | None = None) -> str:
        return f"installed {preview} {port}"

    def fake_uninstall(preview: bool = False) -> str:
        return f"uninstalled {preview}"

    monkeypatch.setattr(service_installer, "install_web_service", fake_install)
    monkeypatch.setattr(service_installer, "uninstall_web_service", fake_uninstall)

    runner = CliRunner()
    result = runner.invoke(web_app, ["install", "--preview", "--port", "9090"])
    assert result.exit_code == 0
    assert "installed True 9090" in result.output

    result = runner.invoke(web_app, ["uninstall", "--preview"])
    assert result.exit_code == 0
    assert "uninstalled True" in result.output


def test_web_ensure_checks_service_and_exits_accordingly(monkeypatch: pytest.MonkeyPatch) -> None:
    from desktop_manager import service_installer

    runner = CliRunner()

    def ensure_ok(port: int = 8090, timeout_s: float = 30.0) -> bool:
        del port, timeout_s
        return True

    def ensure_fail(port: int = 8090, timeout_s: float = 30.0) -> bool:
        del port, timeout_s
        return False

    monkeypatch.setattr(service_installer, "ensure_web_service", ensure_ok)
    result = runner.invoke(web_app, ["ensure", "--port", "8090", "--timeout", "5"])
    assert result.exit_code == 0
    assert "Dashboard answering on http://127.0.0.1:8090" in result.output

    monkeypatch.setattr(service_installer, "ensure_web_service", ensure_fail)
    result = runner.invoke(web_app, ["ensure", "--port", "8090", "--timeout", "5"])
    assert result.exit_code == 1
    assert "Nothing answered on port 8090" in result.output


def test_web_db_commands(monkeypatch: pytest.MonkeyPatch, tmp_path: Path) -> None:
    import sqlite3

    from rig import db
    from rig.db import backup
    from rig.db.migrations import MigrationRecord

    fake_db = tmp_path / "test.db"

    def fake_backup() -> Path:
        return tmp_path / "test_backup.db"

    def fake_get_conn() -> sqlite3.Connection:
        return sqlite3.connect(fake_db)

    def fake_db_path() -> Path:
        return fake_db

    def fake_run_migrations(conn: sqlite3.Connection) -> int:
        del conn
        return 3

    def fake_version(conn: sqlite3.Connection) -> int:
        del conn
        return 3

    def fake_applied(conn: sqlite3.Connection) -> list[MigrationRecord]:
        del conn
        return []

    monkeypatch.setattr(backup, "backup_database", fake_backup)
    monkeypatch.setattr(db, "get_db_connection", fake_get_conn)
    monkeypatch.setattr(db, "default_db_path", fake_db_path)
    monkeypatch.setattr(db, "run_migrations", fake_run_migrations)
    monkeypatch.setattr(db, "current_schema_version", fake_version)
    monkeypatch.setattr(db, "get_applied_migrations", fake_applied)

    runner = CliRunner()
    result = runner.invoke(web_app, ["backup"])
    assert result.exit_code == 0
    assert "Database backup created:" in result.output

    result = runner.invoke(web_app, ["migrate"])
    assert result.exit_code == 0
    assert "Migrations applied: 3" in result.output

    result = runner.invoke(web_app, ["db-status"])
    assert result.exit_code == 0
    assert "Current Schema Version: 3" in result.output
    assert "No migrations have been recorded yet." in result.output
