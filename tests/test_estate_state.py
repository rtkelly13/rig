"""What Python still reads of the estate's state: the wrapup file and Orca's profile folder."""

from __future__ import annotations

import json
import pathlib

from rig.estate import orca_runtime, wrapup_state


def _write_state(worktree: pathlib.Path, state: dict[str, object]) -> None:
    folder = worktree / "temp" / ".rig"
    folder.mkdir(parents=True)
    _ = (folder / "wrapup.json").write_text(json.dumps(state), encoding="utf-8")


def test_a_wrapup_file_in_the_current_schema_is_read(tmp_path: pathlib.Path) -> None:
    _write_state(tmp_path, {"schema": wrapup_state.SCHEMA, "worktree_id": "w1", "branch": "feature"})

    state = wrapup_state.read(tmp_path)

    assert state is not None
    assert (state.worktree_id, state.branch, state.asks) == ("w1", "feature", ())


def test_a_wrapup_file_in_another_schema_reads_as_absent(tmp_path: pathlib.Path) -> None:
    _write_state(tmp_path, {"schema": "rig.wrapup/0", "branch": "feature"})

    assert wrapup_state.read(tmp_path) is None


def test_a_missing_wrapup_file_reads_as_absent(tmp_path: pathlib.Path) -> None:
    assert wrapup_state.read(tmp_path) is None


def test_timestamps_parse_or_read_as_none() -> None:
    parsed = wrapup_state.parse_timestamp("2026-11-07T12:00:00+00:00")

    assert parsed is not None
    assert parsed.year == 2026
    assert wrapup_state.parse_timestamp("yesterday") is None


def test_the_estate_orca_profile_sits_beside_or_is_the_default_one() -> None:
    default = orca_runtime.default_user_data()
    estate = orca_runtime.estate_user_data()

    assert default.is_relative_to(pathlib.Path.home())
    assert estate.parent == default.parent
    assert estate.name in {default.name, orca_runtime.ESTATE_PROFILE_FOLDER}
