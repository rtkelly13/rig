"""Tests for drift scoring, independent of the CLI and the builder."""

from __future__ import annotations

from pathlib import Path

import pytest
from PIL import Image

from image_drift import metrics
from image_drift.geometry import CropBox
from image_drift.metrics import (
    FRAME_WEIGHT,
    Tie,
    compute_drift,
    is_tie,
    score_candidate,
)

pytestmark = pytest.mark.ci

BOXES = {"alpha": CropBox(0, 0, 100, 100), "beta": CropBox(200, 200, 300, 300)}


def _solid(path: Path, colour: tuple[int, int, int]) -> Path:
    Image.new("RGB", (500, 500), colour).save(path)
    return path


# ---------------------------------------------------------------------------
# compute_drift
# ---------------------------------------------------------------------------


def test_identical_images_score_zero(tmp_path: Path) -> None:
    same = _solid(tmp_path / "a.png", (77, 88, 99))
    result = compute_drift(same, same)
    assert result.rmse_pct == pytest.approx(0.0)
    assert len(result.grid) == 25


def test_black_against_white_scores_one_hundred(tmp_path: Path) -> None:
    black = _solid(tmp_path / "black.png", (0, 0, 0))
    white = _solid(tmp_path / "white.png", (255, 255, 255))
    assert compute_drift(black, white).rmse_pct == pytest.approx(100.0)


def test_no_heatmap_is_written_by_default(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    """The old default wrote diff_heatmap.png into the working directory.

    Runs in a private working directory: under pytest-xdist other tests share
    the repo cwd and may create files there while this one is running.
    """
    same = _solid(tmp_path / "a.png", (10, 20, 30))
    cwd = tmp_path / "cwd"
    cwd.mkdir()
    monkeypatch.chdir(cwd)
    compute_drift(same, same)
    assert list(cwd.iterdir()) == []


def test_heatmap_is_written_when_requested(tmp_path: Path) -> None:
    black = _solid(tmp_path / "black.png", (0, 0, 0))
    white = _solid(tmp_path / "white.png", (255, 255, 255))
    heatmap = tmp_path / "nested" / "diff.png"
    compute_drift(black, white, diff_output_path=heatmap)
    assert heatmap.exists()


def test_crop_box_limits_the_comparison(tmp_path: Path) -> None:
    """Half black, half white: a box inside one half must score zero."""
    left = Image.new("RGB", (500, 500), (0, 0, 0))
    right = Image.new("RGB", (500, 500), (0, 0, 0))
    right.paste(Image.new("RGB", (250, 500), (255, 255, 255)), (250, 0))
    a, b = tmp_path / "a.png", tmp_path / "b.png"
    left.save(a)
    right.save(b)

    assert compute_drift(a, b, crop_bbox=CropBox(0, 0, 200, 500)).rmse_pct == pytest.approx(0.0)
    assert compute_drift(a, b, crop_bbox=CropBox(300, 0, 500, 500)).rmse_pct == pytest.approx(100.0)


def test_worst_cells_are_sorted_by_error(tmp_path: Path) -> None:
    left = Image.new("RGB", (500, 500), (0, 0, 0))
    right = Image.new("RGB", (500, 500), (0, 0, 0))
    right.paste(Image.new("RGB", (100, 100), (255, 255, 255)), (0, 0))
    a, b = tmp_path / "a.png", tmp_path / "b.png"
    left.save(a)
    right.save(b)

    worst = compute_drift(a, b).worst_cells
    assert worst[0].error > worst[-1].error
    assert (worst[0].row, worst[0].column) == (0, 0)


# ---------------------------------------------------------------------------
# score_candidate
# ---------------------------------------------------------------------------


def test_composite_weights_frame_and_features(monkeypatch: pytest.MonkeyPatch) -> None:
    """Full-frame RMSE alone cannot see small features; the composite must."""
    seen: list[CropBox | None] = []

    def fake(
        _ref: Path,
        _render: Path,
        diff_output_path: Path | None = None,
        crop_bbox: CropBox | None = None,
    ) -> metrics.DriftResult:
        seen.append(crop_bbox)
        return metrics.DriftResult(rmse_pct=10.0 if crop_bbox is None else 30.0)

    monkeypatch.setattr(metrics, "compute_drift", fake)
    score = score_candidate(Path("ref.jpg"), Path("out.png"), BOXES)

    assert score.full_frame_pct == pytest.approx(10.0)
    assert score.per_feature_pct == {"alpha": 30.0, "beta": 30.0}
    assert score.composite_pct == pytest.approx(FRAME_WEIGHT * 10.0 + (1 - FRAME_WEIGHT) * 30.0)
    assert seen[0] is None
    assert len(seen) == 1 + len(BOXES)


def test_composite_falls_back_to_full_frame_without_features(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    monkeypatch.setattr(
        metrics,
        "compute_drift",
        lambda *_a, **_k: metrics.DriftResult(rmse_pct=17.0),
    )
    score = score_candidate(Path("r"), Path("p"), {})
    assert score.composite_pct == pytest.approx(17.0)
    assert score.full_frame_pct == pytest.approx(17.0)
    assert score.per_feature_pct == {}


# ---------------------------------------------------------------------------
# is_tie
# ---------------------------------------------------------------------------


def test_close_scores_are_a_tie() -> None:
    """Sub-half-point gaps are noise, not a ranking."""
    tie = is_tie([28.01, 27.96, 28.09])
    assert tie.tied
    assert tie.spread == pytest.approx(0.13, abs=0.01)


def test_separated_scores_are_rankable() -> None:
    tie = is_tie([26.4, 29.3])
    assert not tie.tied
    assert tie.spread == pytest.approx(2.9, abs=0.01)


def test_empty_scores_are_treated_as_tied() -> None:
    assert is_tie([]) == Tie(tied=True, spread=0.0)


# ---------------------------------------------------------------------------
# SSIM & PSNR
# ---------------------------------------------------------------------------


def test_identical_images_have_unit_ssim_and_infinite_psnr(tmp_path: Path) -> None:
    same = _solid(tmp_path / "a.png", (100, 150, 200))
    result = compute_drift(same, same)
    assert result.ssim == pytest.approx(1.0)
    assert result.psnr_db == float("inf")


def test_different_images_have_lower_ssim_and_finite_psnr(tmp_path: Path) -> None:
    img1 = _solid(tmp_path / "img1.png", (0, 0, 0))
    img2 = _solid(tmp_path / "img2.png", (255, 255, 255))
    result = compute_drift(img1, img2)
    assert result.ssim < 0.1
    assert result.psnr_db == pytest.approx(0.0, abs=0.1)
