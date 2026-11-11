"""Drift scoring, independent of the CLI and the templates.

Kept separate from `imaging` so a script can compose comparison sheets without
pulling in numpy, and score without pulling in Playwright.
"""

from __future__ import annotations

import math
from collections.abc import Mapping
from dataclasses import dataclass, field
from pathlib import Path

import numpy as np
from PIL import Image

from image_drift.geometry import CropBox, FloatArray
from image_drift.imaging import NORMALISED_SIZE, load_normalised
from image_drift.numeric import axis_length, rms

# How much of the composite comes from the full frame versus the mean of the
# feature regions. Full-frame RMSE alone is dominated by the large flat areas,
# so small features cannot move it — which is what made candidate ranking
# meaningless before the split.
FRAME_WEIGHT = 0.5

# Composite scores closer together than this (percentage points) are a tie, not
# a ranking.
TIE_THRESHOLD_PCT = 0.5

GRID_SIZE = 5


@dataclass(frozen=True, slots=True)
class GridBounds:
    """Pixel slice of one grid cell: rows `row_start:row_end`, columns `col_start:col_end`."""

    row_start: int
    row_end: int
    col_start: int
    col_end: int


@dataclass(frozen=True)
class GridCell:
    """Mean absolute error over one cell of the spatial grid."""

    row: int
    column: int
    error: float
    bounds: GridBounds


@dataclass(frozen=True)
class DriftResult:
    """Outcome of comparing one render against the reference."""

    rmse_pct: float
    ssim: float = 1.0
    psnr_db: float = float("inf")
    grid: list[GridCell] = field(default_factory=list)

    @property
    def worst_cells(self) -> list[GridCell]:
        return sorted(self.grid, key=lambda cell: cell.error, reverse=True)


@dataclass(frozen=True)
class CandidateScore:
    """Composite score plus the parts it was built from."""

    composite_pct: float
    full_frame_pct: float
    per_feature_pct: dict[str, float]


def _as_array(image: Image.Image) -> FloatArray:
    return np.array(image, dtype=np.float32)


def compute_ssim(arr1: FloatArray, arr2: FloatArray) -> float:
    """Compute mean Structural Similarity Index (SSIM) between two normalized images [0..255]."""
    if arr1.ndim == 3 and arr1.shape[2] == 3:
        g1 = 0.299 * arr1[:, :, 0] + 0.587 * arr1[:, :, 1] + 0.114 * arr1[:, :, 2]
        g2 = 0.299 * arr2[:, :, 0] + 0.587 * arr2[:, :, 1] + 0.114 * arr2[:, :, 2]
    else:
        g1, g2 = arr1, arr2

    c1 = (0.01 * 255.0) ** 2
    c2 = (0.03 * 255.0) ** 2

    h, w = axis_length(g1, 0), axis_length(g1, 1)
    bh, bw = max(1, h // 16), max(1, w // 16)

    scores: list[float] = []
    for y in range(0, h - bh + 1, bh):
        for x in range(0, w - bw + 1, bw):
            p1 = g1[y : y + bh, x : x + bw]
            p2 = g2[y : y + bh, x : x + bw]
            m1, m2 = float(np.mean(p1)), float(np.mean(p2))
            var1, var2 = float(np.var(p1)), float(np.var(p2))
            cov = float(np.mean((p1 - m1) * (p2 - m2)))
            denom = (m1**2 + m2**2 + c1) * (var1 + var2 + c2)
            if denom > 0:
                ssim_block = ((2 * m1 * m2 + c1) * (2 * cov + c2)) / denom
                scores.append(ssim_block)

    return float(np.mean(scores)) if scores else 1.0


def compute_psnr(arr1: FloatArray, arr2: FloatArray) -> float:
    """Compute Peak Signal-to-Noise Ratio (PSNR) in decibels."""
    mse = float(np.mean((arr1 - arr2) ** 2))
    if mse <= 1e-10:
        return float("inf")
    return 20.0 * math.log10(255.0 / math.sqrt(mse))


def compute_drift(
    reference_path: Path,
    render_path: Path,
    diff_output_path: Path | None = None,
    crop_bbox: CropBox | None = None,
) -> DriftResult:
    """Pixel-by-pixel RMSE, SSIM, PSNR plus a spatial error grid.

    A diff heatmap is written only when `diff_output_path` is given, so callers
    cannot leave stray artefacts in the working directory by accident.
    """
    reference = load_normalised(reference_path, NORMALISED_SIZE)
    render = load_normalised(render_path, NORMALISED_SIZE)
    if crop_bbox:
        reference = crop_bbox.crop(reference)
        render = crop_bbox.crop(render)

    arr_ref, arr_render = _as_array(reference), _as_array(render)
    abs_diff = np.abs(arr_ref - arr_render)
    mean_diff = np.mean(abs_diff, axis=2)
    rmse_pct = rms(abs_diff) / 255.0 * 100.0
    ssim = compute_ssim(arr_ref, arr_render)
    psnr_db = compute_psnr(arr_ref, arr_render)

    height, width = axis_length(arr_ref, 0), axis_length(arr_ref, 1)
    cell_h, cell_w = max(1, height // GRID_SIZE), max(1, width // GRID_SIZE)
    grid: list[GridCell] = []
    for row in range(GRID_SIZE):
        for column in range(GRID_SIZE):
            r0, r1 = row * cell_h, min(height, (row + 1) * cell_h)
            c0, c1 = column * cell_w, min(width, (column + 1) * cell_w)
            if r0 < r1 and c0 < c1:
                grid.append(
                    GridCell(
                        row=row,
                        column=column,
                        error=float(np.mean(mean_diff[r0:r1, c0:c1])),
                        bounds=GridBounds(r0, r1, c0, c1),
                    )
                )

    if diff_output_path:
        visual = np.zeros((height, width, 3), dtype=np.uint8)
        visual[:, :, 0] = np.clip(mean_diff * 3, 0, 255).astype(np.uint8)
        visual[:, :, 1] = (arr_render[:, :, 1] * 0.3).astype(np.uint8)
        visual[:, :, 2] = (arr_render[:, :, 2] * 0.3).astype(np.uint8)
        diff_output_path.parent.mkdir(parents=True, exist_ok=True)
        Image.fromarray(visual).save(diff_output_path)

    return DriftResult(rmse_pct=rmse_pct, ssim=ssim, psnr_db=psnr_db, grid=grid)


def score_candidate(
    reference_path: Path,
    render_path: Path,
    feature_boxes: Mapping[str, CropBox],
) -> CandidateScore:
    """Weighted composite of full-frame drift and per-feature drift."""
    full = compute_drift(reference_path, render_path).rmse_pct
    per_feature = {
        key: compute_drift(reference_path, render_path, crop_bbox=box).rmse_pct for key, box in feature_boxes.items()
    }
    if per_feature:
        feature_mean = sum(per_feature.values()) / len(per_feature)
        composite = FRAME_WEIGHT * full + (1.0 - FRAME_WEIGHT) * feature_mean
    else:
        composite = full
    return CandidateScore(composite_pct=composite, full_frame_pct=full, per_feature_pct=per_feature)


@dataclass(frozen=True, slots=True)
class Tie:
    """Whether a set of composites is too close to rank, and how far apart they are."""

    tied: bool
    spread: float


def is_tie(scores: list[float], threshold: float = TIE_THRESHOLD_PCT) -> Tie:
    """Whether a set of composites is too close to rank."""
    if not scores:
        return Tie(tied=True, spread=0.0)
    spread = max(scores) - min(scores)
    return Tie(tied=spread < threshold, spread=spread)
