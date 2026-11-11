"""Unit tests for the Git-Ignored Snapshot Manager."""

from __future__ import annotations

import json
from pathlib import Path

import pytest

from image_drift.snapshots import SnapshotManager

pytestmark = pytest.mark.ci


def test_capture_snapshot_step(tmp_path: Path) -> None:
    """Capturing a step should create svg, png, and metadata files with standard naming."""
    variations_root = tmp_path / "temp" / "variations"
    mgr = SnapshotManager(variations_root=variations_root)

    step = mgr.capture_step(
        project="rtkelly-labs",
        run_id="cad-experiment",
        step_idx=1,
        step_name="isometric-facets",
        svg_content="<svg viewBox='0 0 100 100'></svg>",
        png_bytes=b"FAKESTREAM",
        metrics={"ssim": 0.971, "psnr_db": 28.9},
        notes="First draft with 15 facets",
    )

    assert step.project == "rtkelly-labs"
    assert step.run_id == "cad-experiment"
    assert step.step_idx == 1
    assert step.step_name == "isometric-facets"
    assert step.svg_path is not None
    assert step.svg_path.exists()
    assert step.svg_path.suffix == ".svg"
    assert step.png_path is not None
    assert step.png_path.exists()
    assert step.metadata_path is not None
    assert step.metadata_path.exists()

    meta = json.loads(step.metadata_path.read_text(encoding="utf-8"))
    assert meta["metrics"]["ssim"] == 0.971
    assert meta["notes"] == "First draft with 15 facets"


def test_list_and_get_runs(tmp_path: Path) -> None:
    """Snapshot manager must list runs and retrieve all steps within a run."""
    variations_root = tmp_path / "temp" / "variations"
    mgr = SnapshotManager(variations_root=variations_root)

    mgr.capture_step(
        project="parquet-source-generator",
        run_id="git-triad",
        step_idx=1,
        step_name="blue-fork",
        svg_content="<svg></svg>",
    )
    mgr.capture_step(
        project="parquet-source-generator",
        run_id="git-triad",
        step_idx=2,
        step_name="amber-fork",
        svg_content="<svg></svg>",
    )

    runs = mgr.list_runs("parquet-source-generator")
    assert "git-triad" in runs

    run = mgr.get_run("parquet-source-generator", "git-triad")
    assert run is not None
    assert len(run.steps) == 2
    assert run.steps[0].step_name == "blue-fork"
    assert run.steps[1].step_name == "amber-fork"


def test_prune_run(tmp_path: Path) -> None:
    """Pruning a run deletes its directory and returns True."""
    variations_root = tmp_path / "temp" / "variations"
    mgr = SnapshotManager(variations_root=variations_root)

    mgr.capture_step(
        project="rtkelly-labs",
        run_id="trash-run",
        step_idx=1,
        step_name="junk",
        svg_content="<svg></svg>",
    )

    run_dir = variations_root / "rtkelly-labs" / "trash-run"
    assert run_dir.exists()

    assert mgr.prune_run("rtkelly-labs", "trash-run") is True
    assert not run_dir.exists()
    assert mgr.prune_run("rtkelly-labs", "non-existent") is False
