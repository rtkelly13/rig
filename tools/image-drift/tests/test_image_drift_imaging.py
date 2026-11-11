"""Tests for the shared image composition helpers.

These are the pieces that previously got rewritten by hand for every
comparison, so they are worth pinning down.
"""

from __future__ import annotations

from pathlib import Path

import pytest
from PIL import Image

from image_drift.geometry import CropBox
from image_drift.imaging import (
    NORMALISED_SIZE,
    Panel,
    comparison_sheet,
    contact_sheet,
    crop,
    data_uri,
    feature_sheet,
    image_data_uri,
    load_normalised,
    mime_for,
    size_ladder,
    thumbnail,
    zoom,
)

pytestmark = pytest.mark.ci


def _solid(size: int = 500, colour: tuple[int, int, int] = (120, 90, 60)) -> Image.Image:
    return Image.new("RGB", (size, size), colour)


# ---------------------------------------------------------------------------
# Normalisation
# ---------------------------------------------------------------------------


def test_load_normalised_forces_the_shared_canvas(tmp_path: Path) -> None:
    """A feature box only means the same thing in both images at one size."""
    path = tmp_path / "odd.png"
    Image.new("RGB", (321, 654), (1, 2, 3)).save(path)
    image = load_normalised(path)
    assert image.size == (NORMALISED_SIZE, NORMALISED_SIZE)
    assert image.mode == "RGB"


def test_load_normalised_converts_greyscale(tmp_path: Path) -> None:
    path = tmp_path / "grey.png"
    Image.new("L", (100, 100), 128).save(path)
    assert load_normalised(path).mode == "RGB"


def test_crop_uses_pil_box_semantics() -> None:
    assert crop(_solid(), CropBox(10, 20, 60, 90)).size == (50, 70)


# ---------------------------------------------------------------------------
# Scaling
# ---------------------------------------------------------------------------


@pytest.mark.parametrize("size", [128, 64, 32])
def test_thumbnail_is_square_at_the_requested_size(size: int) -> None:
    assert thumbnail(_solid(), size).size == (size, size)


def test_zoom_multiplies_both_dimensions() -> None:
    assert zoom(_solid(40), factor=3).size == (120, 120)


def test_zoom_defaults_to_nearest_neighbour() -> None:
    """Pixel boundaries must stay legible when inspecting a crop."""
    checker = Image.new("RGB", (2, 2))
    checker.putpixel((0, 0), (255, 255, 255))
    enlarged = zoom(checker, factor=8)
    # Nearest neighbour keeps hard edges: the corner block is still pure white.
    assert enlarged.getpixel((3, 3)) == (255, 255, 255)
    assert enlarged.getpixel((12, 3)) == (0, 0, 0)


# ---------------------------------------------------------------------------
# Encoding
# ---------------------------------------------------------------------------


def test_mime_for_distinguishes_jpeg_from_png(tmp_path: Path) -> None:
    assert mime_for(tmp_path / "a.jpg") == "image/jpeg"
    assert mime_for(tmp_path / "a.JPEG") == "image/jpeg"
    assert mime_for(tmp_path / "a.png") == "image/png"


def test_data_uri_is_self_contained(tmp_path: Path) -> None:
    path = tmp_path / "x.png"
    _solid(8).save(path)
    uri = data_uri(path)
    assert uri.startswith("data:image/png;base64,")
    assert "http" not in uri


def test_image_data_uri_round_trips_in_memory_images(tmp_path: Path) -> None:
    uri = image_data_uri(_solid(16), tmp_path, "panel")
    assert uri.startswith("data:image/png;base64,")
    assert (tmp_path / "panel.png").exists()


# ---------------------------------------------------------------------------
# Composition
# ---------------------------------------------------------------------------


def test_contact_sheet_lays_panels_in_one_row_by_default() -> None:
    panels = [Panel(_solid(50), f"p{i}") for i in range(4)]
    sheet = contact_sheet(panels, cell=100, gap=10)
    # 4 cells across, 5 gaps.
    assert sheet.width == 4 * 100 + 5 * 10
    assert sheet.height == 100 + 22 + 2 * 10


def test_contact_sheet_wraps_to_multiple_rows() -> None:
    panels = [Panel(_solid(50)) for _ in range(5)]
    sheet = contact_sheet(panels, cell=100, columns=2, gap=10)
    assert sheet.width == 2 * 100 + 3 * 10
    assert sheet.height == 3 * (100 + 22) + 4 * 10


def test_contact_sheet_handles_no_panels() -> None:
    assert contact_sheet([], cell=64).size == (64, 64)


def test_feature_sheet_puts_the_reference_first() -> None:
    """Reference first, then candidates, all cropped identically."""
    reference = _solid(colour=(255, 0, 0))
    candidates = {"A": _solid(colour=(0, 255, 0)), "B": _solid(colour=(0, 0, 255))}
    sheet = feature_sheet(reference, candidates, CropBox(0, 0, 100, 100), cell=80)
    assert sheet.width == 3 * 80 + 4 * 12
    # The highlighted reference cell sits leftmost.
    assert sheet.getpixel((12 + 40, 12 + 40)) == (255, 0, 0)


def test_size_ladder_shows_every_requested_size() -> None:
    ladder = size_ladder(_solid(), sizes=(128, 64, 32))
    assert ladder.width == 3 * 128 + 4 * 12


def test_comparison_sheet_creates_three_panel_layout() -> None:
    ref = _solid(100, (200, 0, 0))
    rnd = _solid(100, (0, 200, 0))
    diff = _solid(100, (0, 0, 200))
    sheet = comparison_sheet(ref, rnd, diff, rmse_pct=4.5, ssim=0.98, cell=100)
    # 3 panels across
    assert sheet.width == 3 * 100 + 4 * 16
    assert sheet.height == 100 + 26 + 2 * 16
