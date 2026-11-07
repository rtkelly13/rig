"""Git-Ignored Snapshot Manager for ephemeral asset variations and pipeline checkpoints."""

from __future__ import annotations

import json
import shutil
from datetime import UTC, datetime
from pathlib import Path

from pydantic import JsonValue

from rig.image_drift.indexer import (
    AssetIndexer,
    SnapshotStep,
    VariationRun,
)


class SnapshotManager:
    """Manages ephemeral step snapshots and variation runs in the git-ignored variations root."""

    def __init__(self, variations_root: Path) -> None:
        self.variations_root: Path = variations_root

    def capture_step(
        self,
        project: str,
        run_id: str,
        step_idx: int,
        step_name: str,
        svg_content: str | None = None,
        png_bytes: bytes | None = None,
        metrics: dict[str, JsonValue] | None = None,
        notes: str = "",
        timestamp: str | None = None,
    ) -> SnapshotStep:
        """Capture a pipeline step snapshot into the project's variation run folder."""
        ts = timestamp or datetime.now(UTC).strftime("%Y%m%d_%H%M%S")
        run_dir = self.variations_root / project / run_id
        run_dir.mkdir(parents=True, exist_ok=True)

        prefix = f"{project}__{run_id}__{step_idx:02d}_{step_name}__{ts}"

        svg_path: Path | None = None
        if svg_content is not None:
            svg_path = run_dir / f"{prefix}.svg"
            svg_path.write_text(svg_content, encoding="utf-8", newline="\n")

        png_path: Path | None = None
        if png_bytes is not None:
            png_path = run_dir / f"{prefix}.png"
            png_path.write_bytes(png_bytes)

        metadata_path = run_dir / f"{prefix}.json"
        meta_payload = {
            "project": project,
            "run_id": run_id,
            "step_idx": step_idx,
            "step_name": step_name,
            "timestamp": ts,
            "metrics": metrics or {},
            "notes": notes,
        }
        metadata_path.write_text(json.dumps(meta_payload, indent=2), encoding="utf-8")

        return SnapshotStep(
            project=project,
            run_id=run_id,
            step_idx=step_idx,
            step_name=step_name,
            timestamp=ts,
            svg_path=svg_path,
            png_path=png_path,
            metadata_path=metadata_path,
            metrics=metrics or {},
            notes=notes,
        )

    def list_runs(self, project: str) -> list[str]:
        """List all variation run identifiers for a given project."""
        proj_dir = self.variations_root / project
        if not proj_dir.exists():
            return []
        return [d.name for d in sorted(proj_dir.iterdir()) if d.is_dir() and not d.name.startswith(".")]

    def get_run(self, project: str, run_id: str) -> VariationRun | None:
        """Retrieve a specific variation run with all its parsed steps."""
        # Use AssetIndexer's project scanning capability for consistent structure
        indexer = AssetIndexer(
            assets_root=self.variations_root.parent / "assets",
            variations_root=self.variations_root,
        )
        catalog = indexer.index_project(project)
        for run in catalog.variation_runs:
            if run.run_id == run_id:
                return run
        return None

    def prune_run(self, project: str, run_id: str) -> bool:
        """Delete an entire variation run directory."""
        run_dir = self.variations_root / project / run_id
        if run_dir.exists() and run_dir.is_dir():
            shutil.rmtree(run_dir)
            return True
        return False
