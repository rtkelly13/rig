"""Unit tests for the Asset Studio directory indexer and naming regularizer."""

from __future__ import annotations

import json
from pathlib import Path

import pytest

from rig.image_drift.indexer import (
    AssetIndexer,
    ProjectAssetCatalog,
    parse_snapshot_filename,
)

pytestmark = pytest.mark.ci


def test_parse_snapshot_filename_valid() -> None:
    """Standardized snapshot filename tokens must be parsed accurately."""
    fn = "rtkelly-labs__cad-v2__03_crossover-shadow__20260919_100000.svg"
    parsed = parse_snapshot_filename(fn)

    assert parsed is not None
    assert parsed.project == "rtkelly-labs"
    assert parsed.run_id == "cad-v2"
    assert parsed.step_idx == 3
    assert parsed.step_name == "crossover-shadow"
    assert parsed.timestamp == "20260919_100000"
    assert parsed.extension == "svg"


def test_parse_snapshot_filename_invalid() -> None:
    """Non-matching filenames should return None."""
    assert parse_snapshot_filename("random_file.png") is None
    assert parse_snapshot_filename("logo.svg") is None


def test_index_project_empty_dir(tmp_path: Path) -> None:
    """Indexing a project with minimal files should return a valid catalog."""
    assets_root = tmp_path / "assets"
    variations_root = tmp_path / "temp" / "variations"

    proj_dir = assets_root / "test-brand"
    proj_dir.mkdir(parents=True)

    indexer = AssetIndexer(assets_root=assets_root, variations_root=variations_root)
    catalog = indexer.index_project("test-brand")

    assert isinstance(catalog, ProjectAssetCatalog)
    assert catalog.name == "test-brand"
    assert catalog.master_svg is None
    assert catalog.master_png is None
    assert len(catalog.references) == 0
    assert len(catalog.production_cuts) == 0
    assert len(catalog.variation_runs) == 0


def test_index_project_full_structure(tmp_path: Path) -> None:
    """Indexing a full project structure with references, cuts, and variation snapshots."""
    assets_root = tmp_path / "assets"
    variations_root = tmp_path / "temp" / "variations"

    proj_dir = assets_root / "acme-corp"
    proj_dir.mkdir(parents=True)

    # Master files
    (proj_dir / "acme-corp.svg").write_text("<svg></svg>", encoding="utf-8")
    (proj_dir / "acme-corp-logo-2000x2000.png").write_bytes(b"PNG_DATA_2000")
    (proj_dir / "acme-corp-logo-1000x1000.png").write_bytes(b"PNG_DATA")
    (proj_dir / "avatar-500x500.png").write_bytes(b"PNG_DATA")
    (proj_dir / "favicon-32x32.png").write_bytes(b"PNG_DATA")

    # Reference sequence
    ref_dir = proj_dir / "reference"
    ref_dir.mkdir()
    (ref_dir / "01-initial-concept.jpg").write_bytes(b"JPG_DATA")
    (ref_dir / "02-approved-baseline.jpg").write_bytes(b"JPG_DATA")
    (ref_dir / "acme-corp.png").write_bytes(b"PNG_DATA")

    # Variations under temp/variations
    run_dir = variations_root / "acme-corp" / "run-alpha"
    run_dir.mkdir(parents=True)

    (run_dir / "acme-corp__run-alpha__01_wireframe__20260919_100000.svg").write_text("<svg></svg>", encoding="utf-8")
    (run_dir / "acme-corp__run-alpha__01_wireframe__20260919_100000.png").write_bytes(b"PNG_DATA")
    (run_dir / "acme-corp__run-alpha__01_wireframe__20260919_100000.json").write_text(
        json.dumps({"ssim": 0.95, "notes": "initial draft"}), encoding="utf-8"
    )

    indexer = AssetIndexer(assets_root=assets_root, variations_root=variations_root)
    catalog = indexer.index_project("acme-corp")

    assert catalog.master_svg == proj_dir / "acme-corp.svg"
    assert catalog.master_png == proj_dir / "acme-corp-logo-1000x1000.png"

    # Reference sequence ordering
    assert len(catalog.references) == 3
    assert catalog.references[0].sequence_number == 1
    assert catalog.references[0].filename == "01-initial-concept.jpg"
    assert catalog.references[1].sequence_number == 2
    assert catalog.references[1].filename == "02-approved-baseline.jpg"

    # Production cuts prioritized by resolution
    assert catalog.production_cuts[0].filename == "acme-corp-logo-2000x2000.png"
    assert catalog.production_cuts[0].cut_type == "cut_2000"
    assert catalog.production_cuts[1].filename == "acme-corp-logo-1000x1000.png"
    assert catalog.production_cuts[1].cut_type == "master_png"
    assert catalog.production_cuts[2].filename == "acme-corp.svg"
    assert catalog.production_cuts[2].cut_type == "svg"

    cut_names = [c.filename for c in catalog.production_cuts]
    assert "avatar-500x500.png" in cut_names
    assert "favicon-32x32.png" in cut_names

    # Variation runs
    assert len(catalog.variation_runs) == 1
    run = catalog.variation_runs[0]
    assert run.run_id == "run-alpha"
    assert len(run.steps) == 1
    step = run.steps[0]
    assert step.step_name == "wireframe"
    assert step.step_idx == 1
    assert step.metrics == {"ssim": 0.95, "notes": "initial draft"}
    assert step.svg_path is not None
    assert step.png_path is not None


def test_indexer_derives_cuts_strictly_from_production_cuts_csv(tmp_path: Path) -> None:
    """Requirement: Production cuts must be derived strictly from production-cuts.csv in production-cuts/."""
    assets_root = tmp_path / "assets"
    variations_root = tmp_path / "variations"
    proj_dir = assets_root / "quantum-brand"
    cuts_dir = proj_dir / "production-cuts"
    cuts_dir.mkdir(parents=True)

    # Master SVG at project root
    (proj_dir / "quantum-brand.svg").write_text("<svg>quantum</svg>", encoding="utf-8")

    # Files inside production-cuts folder
    (cuts_dir / "quantum-brand-2000x2000.png").write_bytes(b"PNG2000")
    (cuts_dir / "quantum-brand-1000x1000.png").write_bytes(b"PNG1000")
    (cuts_dir / "avatar-500x500.png").write_bytes(b"PNG500")
    (cuts_dir / "apple-touch-icon.png").write_bytes(b"PNG_TOUCH")

    # Rogue, unlisted files that should be IGNORED
    (cuts_dir / "rogue-test-cut.png").write_bytes(b"ROGUE")
    (cuts_dir / "favicon-16x16.png").write_bytes(b"ROGUE_DUP")
    (cuts_dir / "scratch-notes.txt").write_bytes(b"SCRATCH")

    # CSV specification
    csv_content = """filename,cut_type,width,height,format,purpose,description
quantum-brand-2000x2000.png,cut_2000,2000,2000,png,Ultra High-Res Cut,2000px master cut
quantum-brand-1000x1000.png,master_png,1000,1000,png,Master 1000px Cut,1000px master cut
quantum-brand.svg,master_svg,1024,1024,svg,Canonical Vector,Resolution-independent master
avatar-500x500.png,avatar,500,500,png,Profile Avatar,500px profile avatar
apple-touch-icon.png,apple_touch,180,180,png,iOS Touch Icon,iOS Safari touch icon
"""
    (cuts_dir / "production-cuts.csv").write_text(csv_content, encoding="utf-8")

    indexer = AssetIndexer(assets_root=assets_root, variations_root=variations_root)
    catalog = indexer.index_project("quantum-brand")

    # Verify only CSV entries are included
    indexed_filenames = [c.filename for c in catalog.production_cuts]
    assert "rogue-test-cut.png" not in indexed_filenames
    assert "favicon-16x16.png" not in indexed_filenames
    assert "scratch-notes.txt" not in indexed_filenames
    assert len(catalog.production_cuts) == 5

    # Verify attributes parsed correctly
    cut_2000 = next(c for c in catalog.production_cuts if c.cut_type == "cut_2000")
    assert cut_2000.width == 2000
    assert cut_2000.height == 2000
    assert cut_2000.purpose == "Ultra High-Res Cut"
    assert cut_2000.description == "2000px master cut"

    # Verify master resolution
    assert catalog.master_png == cuts_dir / "quantum-brand-1000x1000.png"
    assert catalog.master_svg == proj_dir / "quantum-brand.svg"
