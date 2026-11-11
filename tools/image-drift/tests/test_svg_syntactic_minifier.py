"""Tests for Syntactic SVG Minifier (Scour, Coordinate Quantization)."""

from __future__ import annotations

import pytest

from image_drift.simplifier.syntactic import (
    calculate_viewbox_precision,
    run_scour_pass,
    syntactic_minify_svg,
)

pytestmark = pytest.mark.ci


def test_viewbox_precision_calculation() -> None:
    # 1000x1000 standard canvas -> 1 decimal place is sufficient (0.1px = 0.01%)
    assert calculate_viewbox_precision(1000.0, 1000.0) == 1
    # 24x24 icon canvas -> 3 decimal places needed to avoid visual stroke shifting
    assert calculate_viewbox_precision(24.0, 24.0) == 3


def test_scour_pass_strips_metadata_and_comments() -> None:
    raw_svg = """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100">
      <!-- Author: Antigravity -->
      <metadata><creator>Designer</creator></metadata>
      <g></g>
      <path d="M 10 10 L 90 90 Z" fill="#000" />
    </svg>"""
    scoured = run_scour_pass(raw_svg)
    assert "Author: Antigravity" not in scoured
    assert "<metadata>" not in scoured
    assert "<g></g>" not in scoured
    assert "d=" in scoured
    assert "10" in scoured


def test_minify_runs_no_external_tool(monkeypatch: pytest.MonkeyPatch) -> None:
    """The output depends only on the input: no Node, npx or network is consulted."""
    raw_svg = """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100">
      <path d="M 0 0 L 100 0 L 100 100 L 0 100 Z" fill="#ffffff" />
    </svg>"""
    expected = syntactic_minify_svg(raw_svg)

    def no_subprocess(*_args: object, **_kwargs: object) -> None:
        raise AssertionError("minification must not shell out")

    monkeypatch.setattr("subprocess.run", no_subprocess)
    monkeypatch.setenv("PATH", "")

    assert syntactic_minify_svg(raw_svg) == expected


def test_full_syntactic_minify_pipeline() -> None:
    raw_svg = """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1000 1000">
      <!-- Redundant Comment -->
      <desc>Description text</desc>
      <g id="empty-wrapper"></g>
      <path d="M 10.12345 20.67891 L 30.00000 40.50000 Z" fill="#22d3ee" />
    </svg>"""
    minified = syntactic_minify_svg(raw_svg, use_scour=True)
    assert "Redundant Comment" not in minified
    assert "Description text" not in minified
    assert len(minified) < len(raw_svg)
