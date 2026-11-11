"""Unit tests for Decomposed Visual Drift Diagnostics Engine."""

from __future__ import annotations

from pathlib import Path

import numpy as np
import pytest
from PIL import Image

from image_drift.diagnostics import (
    analyze_decomposed_drift,
    compute_boundary_bias,
    generate_reasoning_guidance,
    locate_spatial_hotspots,
    rgb_to_ycbcr,
)

pytestmark = pytest.mark.ci


def test_rgb_to_ycbcr_conversion() -> None:
    """Verify RGB to YCbCr conversion produces correct channel shapes and bounds."""
    arr = np.full((100, 100, 3), 128.0, dtype=np.float32)
    ycc = rgb_to_ycbcr(arr)

    assert ycc.y.shape == (100, 100)
    assert ycc.cbcr.shape == (100, 100, 2)
    # Neutral gray has Cb ~ 128 and Cr ~ 128
    assert np.allclose(ycc.cbcr[:, :, 0], 128.0, atol=1.0)
    assert np.allclose(ycc.cbcr[:, :, 1], 128.0, atol=1.0)


def test_boundary_bias_neutral_and_dilation() -> None:
    """Test boundary dilation bias calculation."""
    # Identical images -> zero bias
    y = np.full((100, 100), 100.0, dtype=np.float32)
    grad = np.zeros((100, 100), dtype=np.float32)
    assert compute_boundary_bias(y, y, grad, grad) == 0.0

    # Dilated candidate: render has higher luminance around active edge
    y_ref = np.zeros((100, 100), dtype=np.float32)
    y_ren = np.zeros((100, 100), dtype=np.float32)
    y_ren[40:60, 40:60] = 200.0  # extra ink/mass
    grad_edge = np.full((100, 100), 50.0, dtype=np.float32)

    bias = compute_boundary_bias(y_ref, y_ren, grad_edge, grad_edge)
    assert bias > 0.0


def test_locate_spatial_hotspots_mode_attribution() -> None:
    """Verify spatial hotspots correctly identify chromatic vs geometric errors."""
    arr_ref = np.zeros((128, 128, 3), dtype=np.float32)
    arr_ren = np.zeros((128, 128, 3), dtype=np.float32)

    # Introduce high chromatic error in top-left block (shift green/blue without changing luminance much)
    arr_ren[0:32, 0:32, 1] = 200.0
    arr_ren[0:32, 0:32, 2] = 50.0

    hotspots = locate_spatial_hotspots(arr_ref, arr_ren, grid_divisions=4, top_k=2)
    assert len(hotspots) > 0
    top = hotspots[0]
    assert top.rank == 1
    assert top.x0 == 0 and top.y0 == 0
    assert top.error_pct > 0.0


def test_generate_reasoning_guidance() -> None:
    """Verify reasoning synthesizer generates appropriate diagnostic suggestions."""
    # Chrominance dominance
    recs = generate_reasoning_guidance(
        total_rmse=3.5,
        ssim=0.98,
        lum_rmse=0.8,
        chrom_rmse=3.2,
        edge_corr=95.0,
        boundary_bias=0.0,
        hotspots=[],
    )
    assert any("Color/Palette Drift Dominates" in r for r in recs)

    # Geometry dominance & dilation
    recs_geo = generate_reasoning_guidance(
        total_rmse=4.0,
        ssim=0.93,
        lum_rmse=3.5,
        chrom_rmse=1.0,
        edge_corr=78.0,
        boundary_bias=0.25,
        hotspots=[],
    )
    assert any("Geometric / Edge Displacement Dominates" in r for r in recs_geo)
    assert any("Positive Boundary Dilation" in r for r in recs_geo)
    assert any("Edge Correlation Warning" in r for r in recs_geo)


def test_analyze_decomposed_drift_end_to_end(tmp_path: Path) -> None:
    """End-to-end verification of decomposed drift analysis on synthetic test images."""
    ref_file = tmp_path / "ref.png"
    ren_file = tmp_path / "ren.png"

    # Create synthetic reference image (circle on dark background)
    img_ref = Image.new("RGB", (200, 200), (20, 20, 30))
    img_ren = Image.new("RGB", (200, 200), (20, 20, 30))

    # Add a colored square to both, with slight color shift in render
    for x in range(50, 150):
        for y in range(50, 150):
            img_ref.putpixel((x, y), (0, 180, 255))
            img_ren.putpixel((x, y), (0, 170, 245))

    img_ref.save(ref_file)
    img_ren.save(ren_file)

    report = analyze_decomposed_drift(ref_file, ren_file, canvas_size=200)
    assert report.total_rmse_pct >= 0.0
    assert report.ssim > 0.99
    assert report.psnr_db > 30.0
    assert report.luminance_rmse_pct >= 0.0
    assert report.chrominance_rmse_pct >= 0.0
    assert len(report.recommendations) > 0
