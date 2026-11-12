from __future__ import annotations

import json
from pathlib import Path

import pytest

from rig.flags import FLAGS, UnknownFlagError, clear_override, effective_flags, flag_states, is_enabled, set_override

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


def test_the_csharp_oracle_records_this_registry():
    """rig flags runs in C#; Rig.Core.Flags keeps the same list while desktop_manager still reads this one."""
    oracle = Path(__file__).parent / "Rig.Cli.Tests" / "Flags" / "flags-oracle.json"
    recorded = json.loads(oracle.read_text(encoding="utf-8"))["flags"]

    assert recorded == [{"name": f.name, "default": f.default, "description": f.description} for f in FLAGS]
