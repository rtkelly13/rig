"""Tests for Geometric SVG Decimation (Shapely + Bézier Decimation)."""

from __future__ import annotations

import pytest

from rig.image_drift.simplifier.geometric import (
    simplify_path_geometry,
)

pytestmark = pytest.mark.ci


def test_polyline_closed_polygon_decimation() -> None:
    # A rectangle with redundant intermediate collinear points
    # (0,0) -> (50,0) [redundant] -> (100,0) -> (100,100) -> (0,100) -> Z
    d = "M 0 0 L 50 0 L 100 0 L 100 100 L 0 100 Z"
    simplified = simplify_path_geometry(d, tolerance=1.0)
    # The redundant (50, 0) should be pruned by Douglas-Peucker
    assert "50" not in simplified
    assert simplified.startswith("M")
    assert simplified.endswith("Z")


def test_open_stroke_never_forces_z() -> None:
    # Open zigzag line with a redundant point
    # (0,0) -> (25, 25) [redundant] -> (50, 50) -> (100, 0)
    d = "M 0 0 L 25 25 L 50 50 L 100 0"
    simplified = simplify_path_geometry(d, tolerance=0.5)
    # Open stroke must NOT terminate with Z
    assert not simplified.endswith("Z")
    assert not simplified.endswith("z")
    # Redundant collinear midpoint (25, 25) should be removed
    assert "25" not in simplified


def test_bezier_curve_preservation() -> None:
    # A true smooth cubic curve must retain 'C' command and control points
    d = "M 0 0 C 10 50, 40 50, 50 0 Z"
    simplified = simplify_path_geometry(d, tolerance=0.5)
    assert "C" in simplified
    assert simplified.endswith("Z")


def test_near_linear_bezier_collapsed_to_line() -> None:
    # A flat cubic bezier whose control points lie directly on the baseline (0,0) -> (100,0)
    d = "M 0 0 C 33 0, 66 0, 100 0 Z"
    simplified = simplify_path_geometry(d, tolerance=0.5)
    # Near-linear cubic should collapse to L
    assert "C" not in simplified
    assert "L" in simplified or "Z" in simplified


@pytest.mark.parametrize(
    ("d", "expected"),
    [
        # A flat relative cubic collapses to a relative line with the same end offset.
        ("M 0 0 c 10 0 20 0 30 0", "M 0 0 l 30 0"),
        # A real relative curve is kept.
        ("M 0 0 c 0 50 30 50 30 0", "M 0 0 c 0 50 30 50 30 0"),
        # A relative line moves the current point: the absolute cubic after it starts at (150, 100),
        # where it is flat. Read as an absolute point, (50, 0) made it look curved.
        ("M 100 100 l 50 0 C 160 100 190 100 200 100", "M 100 100 l 50 0 L 200 100"),
        # A relative cubic moves the current point too, so the flat relative one after it collapses.
        ("M 10 10 c 0 40 40 40 40 0 c 10 0 20 0 30 0", "M 10 10 c 0 40 40 40 40 0 l 30 0"),
        # A zero-length relative line is dropped; one that moves is kept.
        ("M 0 0 l 0 0 C 0 10 10 10 10 0", "M 0 0 C 0 10 10 10 10 0"),
        ("M 5 5 l 10 0 c 0 10 10 10 10 0", "M 5 5 l 10 0 c 0 10 10 10 10 0"),
        # A second subpath opened with a relative m is measured from the first one's end point.
        ("M 0 0 C 0 20 20 20 20 0 m 10 10 C 30 20 30 40 30 50", "M 0 0 C 0 20 20 20 20 0 m 10 10 L 30 50"),
        # The same holds for a straight-line subpath, which is rewritten in absolute coordinates.
        ("M 0 0 L 10 0 L 20 0 m 0 10 l 10 0 l 10 0", "M 0 0 L 20 0 M 20 10 L 40 10"),
    ],
)
def test_curve_branch_tracks_relative_commands(d: str, expected: str) -> None:
    assert simplify_path_geometry(d, tolerance=0.5) == expected
