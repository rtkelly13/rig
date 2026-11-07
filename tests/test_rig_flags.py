from __future__ import annotations

from pathlib import Path

import pytest
from typer.testing import CliRunner

from rig.flags import FLAGS, UnknownFlagError, clear_override, effective_flags, flag_states, is_enabled, set_override
from rig.flags import cli as flags_cli

pytestmark = pytest.mark.ci


def test_flag_names_are_unique():
    names = [flag.name for flag in FLAGS]
    assert len(names) == len(set(names))


def test_a_fresh_database_gives_every_default(tmp_path: Path):
    db = tmp_path / "rig.db"

    assert effective_flags(db) == {flag.name: flag.default for flag in FLAGS}
    assert all(state.override is None for state in flag_states(db))


def test_override_wins_until_reset(tmp_path: Path):
    db = tmp_path / "rig.db"

    set_override("claude-session-restore", True, db)
    assert is_enabled("claude-session-restore", db)
    set_override("claude-session-restore", False, db)
    assert not is_enabled("claude-session-restore", db)

    assert clear_override("claude-session-restore", db)
    assert not clear_override("claude-session-restore", db)
    assert is_enabled("claude-session-restore", db) is False


def test_unknown_flag_is_refused(tmp_path: Path):
    with pytest.raises(UnknownFlagError):
        set_override("no-such-flag", True, tmp_path / "rig.db")
    with pytest.raises(UnknownFlagError):
        is_enabled("no-such-flag", tmp_path / "rig.db")


def test_cli_enable_list_reset(tmp_path: Path, monkeypatch: pytest.MonkeyPatch):
    db = tmp_path / "rig.db"
    monkeypatch.setattr("rig.flags.store.get_db_connection", lambda _path=None: _connect(db))
    runner = CliRunner()

    assert runner.invoke(flags_cli.app, ["enable", "work-fork"]).exit_code == 0
    listed = runner.invoke(flags_cli.app, ["list"])
    assert listed.exit_code == 0
    assert "work-fork" in listed.output
    assert runner.invoke(flags_cli.app, ["reset", "work-fork"]).exit_code == 0
    assert runner.invoke(flags_cli.app, ["enable", "nope"]).exit_code == 1


def _connect(db: Path):
    from rig.db.connection import get_db_connection

    return get_db_connection(db)
