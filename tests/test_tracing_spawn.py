"""`rig.tracing.spawn_detached`: start a process and return without waiting for it."""

from __future__ import annotations

import sys
import time
from pathlib import Path

import pytest

from rig.tracing import spawn_detached

pytestmark = pytest.mark.ci


def test_the_spawned_process_runs_on_after_the_call_returns(tmp_path: Path) -> None:
    marker = tmp_path / "ran.txt"
    script = f"import pathlib, time; time.sleep(0.5); pathlib.Path({str(marker)!r}).write_text('ran')"
    started = time.monotonic()
    pid = spawn_detached([sys.executable, "-c", script])
    assert pid > 0
    assert time.monotonic() - started < 0.5
    assert not marker.exists()
    deadline = time.monotonic() + 30
    while not marker.exists() and time.monotonic() < deadline:
        time.sleep(0.05)
    assert marker.read_text(encoding="utf-8") == "ran"


def test_the_spawned_process_writes_both_streams_to_the_log(tmp_path: Path) -> None:
    log = tmp_path / "out.log"
    _ = log.write_text("before\n", encoding="utf-8")
    script = "import sys; print('out', flush=True); print('err', file=sys.stderr)"
    _ = spawn_detached([sys.executable, "-c", script], log=log)
    deadline = time.monotonic() + 30
    while "err" not in log.read_text(encoding="utf-8") and time.monotonic() < deadline:
        time.sleep(0.05)
    assert log.read_text(encoding="utf-8").split() == ["before", "out", "err"]
