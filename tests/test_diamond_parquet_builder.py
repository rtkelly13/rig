"""Tests for Parquet.SourceGenerator diamond parquet vector logo builder and drift calibration."""

from __future__ import annotations

from pathlib import Path

import pytest

from rig.image_drift import diamond_parquet_builder
from rig.image_drift.imaging import chromium_available, render_svg
from rig.image_drift.metrics import compute_drift

pytestmark = pytest.mark.ci


def test_diamond_parquet_builder_exports() -> None:
    """diamond_parquet_builder must satisfy the BuilderModule protocol."""
    assert len(diamond_parquet_builder.FEATURE_REGIONS) == 5
    assert diamond_parquet_builder.DEFAULT_VARIANT in diamond_parquet_builder.VARIANTS
    assert callable(diamond_parquet_builder.build_svg_logo)
    assert callable(diamond_parquet_builder.generate_svg)


def test_feature_region_bounds() -> None:
    """All feature regions must be within the 512x512 normalised canvas."""
    for fr in diamond_parquet_builder.FEATURE_REGIONS:
        assert 0 <= fr.x0 < fr.x1 <= 512
        assert 0 <= fr.y0 < fr.y1 <= 512


def test_feature_keys_match_expected() -> None:
    """Feature regions must include the 5 key semantic zones."""
    keys = {fr.key for fr in diamond_parquet_builder.FEATURE_REGIONS}
    expected = {"diamond_tile", "root_node", "center_trunk", "left_branch", "right_branch"}
    assert keys == expected


def test_build_svg_logo_produces_pure_vector(tmp_path: Path) -> None:
    """Built SVG must be pure vector with no base64 raster embedding and accessible attributes."""
    out_svg = tmp_path / "test_diamond_logo.svg"
    diamond_parquet_builder.build_svg_logo(out_svg)

    assert out_svg.exists()
    content = out_svg.read_text(encoding="utf-8")
    assert "<svg" in content
    assert 'viewBox="0 0 1024 1024"' in content
    assert "<path" in content
    assert "data:image" not in content  # No embedded base64 raster
    assert "<title>" in content
    assert "<desc>" in content


@pytest.mark.skipif(not chromium_available(), reason="Chromium not installed for rendering")
def test_drift_metrics_against_reference(tmp_path: Path) -> None:
    """Rendered diamond SVG must achieve high fidelity against the baseline raster."""
    repo_root = Path(__file__).resolve().parents[1]
    svg_path = repo_root / "assets" / "parquet-source-generator" / "parquet-diamond.svg"
    ref_path = repo_root / "assets" / "parquet-source-generator" / "reference" / "02-simplified-cad-baseline.jpg"
    if not ref_path.exists():
        ref_path = repo_root / "assets" / "parquet-source-generator" / "reference" / "parquet-diamond.png"

    assert svg_path.exists(), f"Missing SVG asset: {svg_path}"
    assert ref_path.exists(), f"Missing reference asset: {ref_path}"

    rendered_png = tmp_path / "rendered_diamond.png"
    render_svg(svg_path, rendered_png, 1024, 1024)
    result = compute_drift(ref_path, rendered_png)

    assert result.psnr_db >= 24.5, f"PSNR {result.psnr_db:.2f} dB is below 24.5 dB threshold"
    assert result.ssim >= 0.85, f"SSIM {result.ssim:.4f} is below 0.85 threshold"
    assert result.rmse_pct < 6.0, f"RMSE {result.rmse_pct:.2f}% is above 6.0% threshold"
