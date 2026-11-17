"""The runtime copy's locations, as the Python code the scheduled tasks run finds them."""

from __future__ import annotations

from pathlib import Path

import pytest

from desktop_manager import runtime

pytestmark = pytest.mark.ci


def test_running_copy_is_true_only_under_the_runtime_root(tmp_path: Path):
    root = tmp_path / "runtime"
    assert runtime.running_copy(str(root / "abc" / "src" / "desktop_manager" / "plan.py"), root)
    assert not runtime.running_copy(str(tmp_path / "repo" / "src" / "desktop_manager" / "plan.py"), root)


def test_state_is_empty_until_an_install_records_one(tmp_path: Path):
    assert runtime.read_state(tmp_path) == runtime.State()

    (tmp_path / runtime.STATE_NAME).write_text(
        '{"current": "abc", "previous": null, "repo": "/src/rig"}\n', encoding="utf-8"
    )

    assert runtime.read_state(tmp_path) == runtime.State(current="abc", repo="/src/rig")
