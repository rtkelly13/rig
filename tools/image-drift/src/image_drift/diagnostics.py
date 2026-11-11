"""Decomposed Visual Drift Diagnostics & Attribution Engine.

Bridges SVG generation simplification and PNG raster comparison by decomposing
visual differences into orthogonal physical channels:
1. Structural / Geometric Error (edge gradient displacement, contour alignment)
2. Photometric / Color Error (luminance delta vs chrominance/palette drift)
3. Spatial Hotspot Localization (pinpointing exact pixel bounding boxes for SVG generator adjustment)
4. Automated Reasoning Diagnostics (actionable guidance for refining .py builder scripts)
"""

from __future__ import annotations

import math
from dataclasses import dataclass, field
from pathlib import Path

import numpy as np

from image_drift.geometry import FloatArray
from image_drift.imaging import NORMALISED_SIZE, load_normalised
from image_drift.metrics import compute_psnr, compute_ssim
from image_drift.numeric import axis_length, correlation_pct, gradient_magnitude, rms


@dataclass(frozen=True)
class HotspotRegion:
    """A localized spatial region with concentrated visual drift."""

    rank: int
    x0: int
    y0: int
    x1: int
    y1: int
    error_pct: float
    dominant_mode: str  # "GEOMETRY_EDGE", "CHROMATIC_COLOR", or "MIXED"
    summary: str


@dataclass(frozen=True)
class DecomposedDriftReport:
    """Multi-channel visual error decomposition between raster reference and vector render."""

    # Full frame composite
    total_rmse_pct: float
    ssim: float
    psnr_db: float

    # Orthogonal channel decomposition
    luminance_rmse_pct: float
    chrominance_rmse_pct: float
    color_to_geometry_ratio: float

    # Edge and contour fidelity
    edge_correlation: float
    boundary_dilation_bias: float

    # Spatial hotspot localization
    hotspots: list[HotspotRegion] = field(default_factory=list)

    # Actionable reasoning guidance
    recommendations: list[str] = field(default_factory=list)


@dataclass(frozen=True, slots=True)
class YCbCr:
    """An image split into luminance (`y`, HxW) and chrominance (`cbcr`, HxWx2)."""

    y: FloatArray
    cbcr: FloatArray


@dataclass(frozen=True, slots=True)
class _CellDrift:
    x0: int
    y0: int
    x1: int
    y1: int
    error_pct: float
    lum_err: float
    chrom_err: float


def rgb_to_ycbcr(arr: FloatArray) -> YCbCr:
    """Convert RGB float image [0..255] into Y (luminance) and CbCr (chrominance) arrays."""
    r = arr[:, :, 0]
    g = arr[:, :, 1]
    b = arr[:, :, 2]

    # BT.601 standard conversion
    y = 0.299 * r + 0.587 * g + 0.114 * b
    cb = -0.168736 * r - 0.331264 * g + 0.5 * b + 128.0
    cr = 0.5 * r - 0.418688 * g - 0.081312 * b + 128.0

    cbcr = np.stack([cb, cr], axis=-1)
    return YCbCr(y=y, cbcr=cbcr)


def compute_boundary_bias(
    y_ref: FloatArray,
    y_ren: FloatArray,
    grad_ref: FloatArray,
    grad_ren: FloatArray,
) -> float:
    """Estimate whether vector edges are dilated (oversized) or eroded (contracted).

    Returns a normalized value where:
      > 0.1: Dilation bias (rendered edges extend further into background)
      < -0.1: Erosion bias (rendered edges are thinner / contracted)
    """
    # Active edge transition zone: high gradient in either reference or render
    edge_mask = (grad_ref > 20.0) | (grad_ren > 20.0)
    if not np.any(edge_mask):
        return 0.0

    # Inverted contrast: difference in luminance mass across transition zone
    diff_lum = (y_ren[edge_mask] - y_ref[edge_mask]) / 255.0
    bias = float(np.mean(diff_lum))

    # Scale to typical [-1.0 .. +1.0] range
    return min(1.0, max(-1.0, bias * 10.0))


def locate_spatial_hotspots(
    arr_ref: FloatArray,
    arr_ren: FloatArray,
    grid_divisions: int = 8,
    top_k: int = 4,
) -> list[HotspotRegion]:
    """Find concentrated spatial drift clusters across the image grid."""
    height, width = axis_length(arr_ref, 0), axis_length(arr_ref, 1)
    cell_h = max(1, height // grid_divisions)
    cell_w = max(1, width // grid_divisions)

    ref, ren = rgb_to_ycbcr(arr_ref), rgb_to_ycbcr(arr_ren)

    pixel_diff = np.sqrt(np.mean((arr_ref - arr_ren) ** 2, axis=2))
    lum_diff = np.abs(ref.y - ren.y)
    chrom_diff = np.sqrt(np.mean((ref.cbcr - ren.cbcr) ** 2, axis=2))

    cells: list[_CellDrift] = []
    for r in range(grid_divisions):
        for c in range(grid_divisions):
            y0, y1 = r * cell_h, min(height, (r + 1) * cell_h)
            x0, x1 = c * cell_w, min(width, (c + 1) * cell_w)
            if y0 >= y1 or x0 >= x1:
                continue

            cell_err = float(np.mean(pixel_diff[y0:y1, x0:x1]) / 255.0 * 100.0)
            cell_lum = float(np.mean(lum_diff[y0:y1, x0:x1]) / 255.0 * 100.0)
            cell_chrom = float(np.mean(chrom_diff[y0:y1, x0:x1]) / 255.0 * 100.0)

            cells.append(_CellDrift(x0, y0, x1, y1, error_pct=cell_err, lum_err=cell_lum, chrom_err=cell_chrom))

    # Sort descending by total error
    cells.sort(key=lambda item: item.error_pct, reverse=True)

    hotspots: list[HotspotRegion] = []
    for rank, cell in enumerate(cells[:top_k], start=1):
        if cell.error_pct < 0.2:
            break

        lum = cell.lum_err
        chrom = cell.chrom_err
        if lum > 1.4 * chrom:
            mode = "GEOMETRY_EDGE"
            desc = "Edge / contour misalignment dominates error in this block"
        elif chrom > 1.4 * lum:
            mode = "CHROMATIC_COLOR"
            desc = "Color gradient or fill shade drift dominates error in this block"
        else:
            mode = "MIXED"
            desc = "Combined geometric boundary and shading mismatch"

        hotspots.append(
            HotspotRegion(
                rank=rank,
                x0=cell.x0,
                y0=cell.y0,
                x1=cell.x1,
                y1=cell.y1,
                error_pct=round(cell.error_pct, 2),
                dominant_mode=mode,
                summary=desc,
            )
        )

    return hotspots


def generate_reasoning_guidance(
    total_rmse: float,
    ssim: float,
    lum_rmse: float,
    chrom_rmse: float,
    edge_corr: float,
    boundary_bias: float,
    hotspots: list[HotspotRegion],
) -> list[str]:
    """Synthesize plain-language engineering advice for refining the SVG generator script."""
    recs: list[str] = []

    # 1. Geometry vs Color divergence
    if chrom_rmse > 1.8 * lum_rmse and chrom_rmse > 1.0:
        recs.append(
            f"Color/Palette Drift Dominates (Chrominance: {chrom_rmse:.2f}% vs Luminance: {lum_rmse:.2f}%): "
            "The geometric silhouette is structurally accurate. Focus on tuning <linearGradient> stop colors, "
            "lighting specular highlights, or fill opacity in the .py builder."
        )
    elif lum_rmse > 1.8 * chrom_rmse and lum_rmse > 1.0:
        recs.append(
            f"Geometric / Edge Displacement Dominates (Luminance: {lum_rmse:.2f}% vs Chrominance: {chrom_rmse:.2f}%): "
            "Color shades match well, but path contours or vertex positions drift from the raster. "
            "Inspect polyline knots and curve tangents around the top spatial hotspots."
        )

    # 2. Boundary Dilation / Erosion
    if boundary_bias > 0.12:
        recs.append(
            f"Positive Boundary Dilation (+{boundary_bias:.2f}): Rendered vector features extend slightly beyond "
            "ground-truth boundaries. Consider decreasing stroke-width by ~0.5px or slightly contracting coordinates."
        )
    elif boundary_bias < -0.12:
        recs.append(
            f"Negative Boundary Erosion ({boundary_bias:.2f}): Rendered vector features are slightly thinner than the "
            "source raster. Consider increasing stroke-width by ~0.5px or expanding coordinate bounds."
        )

    # 3. High-Frequency Edge Alignment
    if edge_corr < 90.0:
        recs.append(
            f"Edge Correlation Warning ({edge_corr:.1f}%): Gradient contours deviate from source raster. "
            "Verify Bézier tangent smoothness and facet intersections."
        )

    # 4. Localized hotspot guidance
    if hotspots:
        top = hotspots[0]
        recs.append(
            f"Primary Hotspot #{top.rank} at [X:{top.x0}..{top.x1}, Y:{top.y0}..{top.y1}] "
            f"({top.error_pct}% error, {top.dominant_mode}): {top.summary}. "
            "Cross-reference this coordinate box with your generator's path definitions."
        )

    if not recs:
        recs.append(
            f"Superb Fidelity (SSIM: {ssim * 100:.2f}%, RMSE: {total_rmse:.2f}%): Rendered SVG and Source PNG match "
            "within tight sub-pixel tolerance across both geometric and chromatic channels."
        )

    return recs


def analyze_decomposed_drift(
    reference_path: Path,
    render_path: Path,
    canvas_size: int = NORMALISED_SIZE,
) -> DecomposedDriftReport:
    """Analyze decomposed visual drift between reference raster and rendered vector."""
    ref_img = load_normalised(reference_path, canvas_size)
    ren_img = load_normalised(render_path, canvas_size)

    arr_ref = np.array(ref_img.convert("RGBA"), dtype=np.float32)[:, :, :3]
    arr_ren = np.array(ren_img.convert("RGBA"), dtype=np.float32)[:, :, :3]

    # Full composite metrics
    abs_diff = np.abs(arr_ref - arr_ren)
    total_rmse = rms(abs_diff) / 255.0 * 100.0
    ssim = compute_ssim(arr_ref, arr_ren)
    psnr_db = compute_psnr(arr_ref, arr_ren)

    # Orthogonal YCbCr decomposition
    ycc_ref, ycc_ren = rgb_to_ycbcr(arr_ref), rgb_to_ycbcr(arr_ren)
    y_ref, y_ren = ycc_ref.y, ycc_ren.y

    lum_diff = np.abs(y_ref - y_ren)
    chrom_diff = np.sqrt(np.sum((ycc_ref.cbcr - ycc_ren.cbcr) ** 2, axis=2))

    lum_rmse = rms(lum_diff) / 255.0 * 100.0
    # Divide chrominance by sqrt(2) to normalize against dual CbCr channels
    chrom_rmse = rms(chrom_diff) / (255.0 * math.sqrt(2.0)) * 100.0
    color_ratio = float(chrom_rmse / max(0.01, lum_rmse))

    # High-frequency gradient field correlation
    grad_ref = gradient_magnitude(y_ref)
    grad_ren = gradient_magnitude(y_ren)
    edge_corr = correlation_pct(grad_ref, grad_ren)

    # Boundary dilation / erosion
    boundary_bias = compute_boundary_bias(y_ref, y_ren, grad_ref, grad_ren)

    # Hotspot localization
    hotspots = locate_spatial_hotspots(arr_ref, arr_ren, grid_divisions=8, top_k=4)

    # Plain-language actionable reasoning
    recommendations = generate_reasoning_guidance(
        total_rmse=total_rmse,
        ssim=ssim,
        lum_rmse=lum_rmse,
        chrom_rmse=chrom_rmse,
        edge_corr=edge_corr,
        boundary_bias=boundary_bias,
        hotspots=hotspots,
    )

    return DecomposedDriftReport(
        total_rmse_pct=total_rmse,
        ssim=ssim,
        psnr_db=psnr_db,
        luminance_rmse_pct=lum_rmse,
        chrominance_rmse_pct=chrom_rmse,
        color_to_geometry_ratio=color_ratio,
        edge_correlation=edge_corr,
        boundary_dilation_bias=boundary_bias,
        hotspots=hotspots,
        recommendations=recommendations,
    )
