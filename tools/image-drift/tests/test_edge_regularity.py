"""Tests for axis-conformance: the deterministic edge-quality metric.

Unlike drift, this needs no renderer, so it runs everywhere the suite runs and
can serve as a CI ratchet. These tests pin the properties that make it
trustworthy as a target metric: determinism, invariance to things that should
not matter, and sensitivity to the thing it measures.
"""

from __future__ import annotations

import re

import pytest

from image_drift import cli_base
from image_drift.edge_regularity import (
    ISOMETRIC_AXES,
    analyze_edges,
    has_curves,
    parse_subpaths,
    regularize_svg,
)
from image_drift.hex_geometry import Point

pytestmark = pytest.mark.ci

MASTER = cli_base.DEFAULT_LOGO.read_text(encoding="utf-8")

# Current measured conformance of the committed master. This is a ratchet: it
# may be raised when the geometry improves, never lowered to make a change pass.
MASTER_CONFORMANCE_FLOOR = 36.0

_SQUARE_D = "M 10,10 L 90,10 L 90,90 L 10,90 Z"
SQUARE = f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100"><path d="{_SQUARE_D}" fill="#abc"/></svg>'


def test_metric_is_deterministic() -> None:
    """The same input yields a bit-identical score every time."""
    scores = {analyze_edges(MASTER).axis_conformance_pct for _ in range(5)}
    assert len(scores) == 1


def test_metric_is_invariant_to_path_order() -> None:
    """Score is length-weighted over all edges, so path order cannot matter."""
    paths = re.findall(r"<path [^>]*/>", MASTER)
    assert len(paths) > 1
    forward = "<svg>" + "".join(paths) + "</svg>"
    reverse = "<svg>" + "".join(reversed(paths)) + "</svg>"
    assert analyze_edges(forward).axis_conformance_pct == analyze_edges(reverse).axis_conformance_pct


def test_metric_is_invariant_to_translation() -> None:
    """Edge angles do not depend on where the mark sits on the canvas."""
    shifted = re.sub(r"(-?[\d.]+),(-?[\d.]+)", lambda m: f"{float(m[1]) + 7:g},{float(m[2]) + 13:g}", MASTER)
    assert analyze_edges(shifted).axis_conformance_pct == pytest.approx(analyze_edges(MASTER).axis_conformance_pct)


def test_metric_needs_no_renderer() -> None:
    """A perfectly axis-aligned square scores 100% with no browser involved."""
    # 0 and 90 degrees: a square's edges lie exactly on those axes.
    assert analyze_edges(SQUARE, axes=(0.0, 90.0)).axis_conformance_pct == pytest.approx(100.0)


def test_off_axis_edge_lowers_conformance() -> None:
    """Tilting one edge off its axis is visible in the conformance score."""
    tilted = SQUARE.replace("L 90,10", "L 90,14")
    assert analyze_edges(tilted, axes=(0.0, 90.0)).axis_conformance_pct < 100.0


def test_axis_aligned_staircase_is_caught_by_defect_counts_not_conformance() -> None:
    """Conformance alone is not sufficient, and the counters cover the gap.

    A staircase built from exactly-on-axis segments is 100% conformant while
    still reading as jagged. That is why the report carries micro-segment and
    staircase counts alongside the percentage, and why the ratchet gates on
    both rather than on conformance alone.
    """
    jagged = SQUARE.replace("M 10,10 L 90,10", "M 10,10 L 50,10 L 50,12 L 90,12")
    report = analyze_edges(jagged, axes=(0.0, 90.0))
    assert report.axis_conformance_pct == pytest.approx(100.0)
    assert report.count("micro_segment") >= 1


def test_master_meets_the_conformance_floor() -> None:
    """Ratchet: the committed master's edge quality must not regress."""
    score = analyze_edges(MASTER).axis_conformance_pct
    assert score >= MASTER_CONFORMANCE_FLOOR, (
        f"axis conformance fell to {score:.2f}% (floor {MASTER_CONFORMANCE_FLOOR}%). "
        "Inspect with: uv run image-reconcile edges -p rtkelly-labs"
    )


def test_regularization_improves_conformance() -> None:
    """The repair must actually move the metric it targets."""
    before = analyze_edges(MASTER).axis_conformance_pct
    after = analyze_edges(regularize_svg(MASTER)).axis_conformance_pct
    assert after > before


def test_regularization_is_deterministic() -> None:
    """Repair is pure: same input, byte-identical output."""
    assert regularize_svg(MASTER) == regularize_svg(MASTER)


def test_regularization_is_idempotent_on_conformance() -> None:
    """A second pass must not undo the first."""
    once = regularize_svg(MASTER)
    twice = regularize_svg(once)
    assert analyze_edges(twice).axis_conformance_pct >= analyze_edges(once).axis_conformance_pct - 0.01


def test_regularization_preserves_path_and_ring_counts() -> None:
    """Repair adjusts vertices; it must not drop or add whole shapes."""
    fixed = regularize_svg(MASTER)
    assert len(re.findall(r"<path ", fixed)) == len(re.findall(r"<path ", MASTER))
    assert fixed.count("<linearGradient") == MASTER.count("<linearGradient")


def test_master_uses_the_isometric_axes() -> None:
    """Conformance is far higher on the isometric axes than on arbitrary ones.

    This is what justifies 30/90/150 as the canonical set rather than a guess.
    """
    iso = analyze_edges(MASTER, axes=ISOMETRIC_AXES, exact_tol=3.0).axis_conformance_pct
    off = analyze_edges(MASTER, axes=(15.0, 75.0, 135.0), exact_tol=3.0).axis_conformance_pct
    assert iso > off * 2


def test_curve_paths_are_skipped_not_mangled() -> None:
    """The parser only understands polygons, so curves must be left alone."""
    curved = SQUARE.replace("L 90,10", "C 50,0 70,0 90,10")
    assert has_curves("M 0,0 C 1,1 2,2 3,3")
    assert regularize_svg(curved) == curved


def test_parse_subpaths_splits_on_moveto() -> None:
    rings = parse_subpaths("M 0,0 L 10,0 L 10,10 Z M 20,20 L 30,20 L 30,30 Z")
    assert len(rings) == 2
    assert rings[0][0] == Point(0.0, 0.0)
    assert rings[1][0] == Point(20.0, 20.0)
