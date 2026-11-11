"""Tests that the committed brand assets match their declared manifests.

These guard the invariants in AGENTS.md ("Brand Asset & Production Cuts"):
production cuts derive strictly from `production-cuts.csv`, undeclared files in
that folder are rogue, and no second unsynchronised copy of a master may exist.
"""

from __future__ import annotations

import csv
from pathlib import Path

import pytest

from rig.image_drift import cli_base, diamond_parquet_builder

pytestmark = pytest.mark.ci

PROJECT_KEYS = sorted(cli_base.PROJECTS)


def _declared_cuts(cuts_dir: Path) -> dict[str, dict[str, str]]:
    with (cuts_dir / "production-cuts.csv").open(newline="", encoding="utf-8") as handle:
        return {row["filename"]: row for row in csv.DictReader(handle)}


@pytest.mark.parametrize("project", PROJECT_KEYS)
def test_every_declared_cut_exists(project: str) -> None:
    """Every row in production-cuts.csv resolves to a real file."""
    cuts_dir = cli_base.PROJECTS[project].asset_dir / "production-cuts"
    missing = [name for name in _declared_cuts(cuts_dir) if not (cuts_dir / name).is_file()]
    assert not missing, f"{project}: declared cuts missing from disk: {missing}"


@pytest.mark.parametrize("project", PROJECT_KEYS)
def test_no_rogue_files_in_production_cuts(project: str) -> None:
    """Every file in production-cuts/ is declared; undeclared files are rogue."""
    cuts_dir = cli_base.PROJECTS[project].asset_dir / "production-cuts"
    declared = set(_declared_cuts(cuts_dir)) | {"production-cuts.csv"}
    present = {p.name for p in cuts_dir.iterdir() if p.is_file() and not p.name.startswith(".")}
    assert not (present - declared), f"{project}: undeclared files in production-cuts/: {sorted(present - declared)}"


@pytest.mark.parametrize("project", PROJECT_KEYS)
def test_master_svg_cut_matches_canonical_master(project: str) -> None:
    """The master SVG cut must be byte-identical to the canonical master.

    It is a copy, so nothing but a test stops the two drifting apart.
    """
    cfg = cli_base.PROJECTS[project]
    cut = cfg.asset_dir / "production-cuts" / cfg.svg_path.name
    if not cut.exists():
        pytest.skip(f"{project} declares no master_svg cut")
    assert cut.read_bytes() == cfg.svg_path.read_bytes(), (
        f"{project}: production-cuts/{cut.name} has drifted from the canonical master. "
        f"Regenerate with: uv run image-reconcile export-avatar -p {project}"
    )


@pytest.mark.parametrize("project", PROJECT_KEYS)
def test_no_optimized_sidecar_is_committed(project: str) -> None:
    """Simplifier output is a regenerable intermediate, not a committed asset.

    A lossy derivative sitting beside the master under a near-identical name is
    an accident waiting to be copied over it.
    """
    strays = sorted(p.name for p in cli_base.PROJECTS[project].asset_dir.glob("*.optimized.svg"))
    assert not strays, f"{project}: committed simplifier sidecars: {strays} (regenerate via `image-reconcile simplify`)"


@pytest.mark.parametrize("project", PROJECT_KEYS)
def test_reference_manifest_primary_exists(project: str) -> None:
    """The reference manifest's declared primary baseline resolves to a real file."""
    cfg = cli_base.PROJECTS[project]
    manifest = cfg.asset_dir / "reference" / "manifest.json"
    if not manifest.exists():
        pytest.skip(f"{project} has no reference manifest")
    assert cfg.reference_path.is_file()
    assert cfg.reference_path.parent.name == "reference"


def test_parquet_builder_master_matches_canonical_master() -> None:
    """The builder's packaged master is what `build` writes, so it must match the committed asset."""
    canonical = cli_base.PROJECTS["parquet-source-generator"].svg_path
    assert diamond_parquet_builder.generate_svg("master") == canonical.read_text(encoding="utf-8"), (
        "builder_assets/parquet-source-generator/master.svg has drifted from "
        f"{canonical.name}; a later `image-reconcile build` would overwrite the canonical asset."
    )
