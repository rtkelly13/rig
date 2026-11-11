"""Tests for Closed-Loop SVG Simplification Pipeline."""

from __future__ import annotations

import pytest

from rig.image_drift.simplifier.pipeline import (
    count_svg_path_nodes,
    simplify_svg_document,
)

pytestmark = pytest.mark.ci


def test_count_svg_path_nodes() -> None:
    svg = """<svg viewBox="0 0 100 100" xmlns="http://www.w3.org/2000/svg">
      <path d="M 0 0 L 50 0 L 100 0 L 100 100 Z" />
      <path d="M 10 10 L 20 20 Z" />
    </svg>"""
    count = count_svg_path_nodes(svg)
    assert count == 8  # 5 in first path (M, L, L, L, Z) + 3 in second path (M, L, Z)


def test_simplify_svg_document_reduces_nodes_and_bytes() -> None:
    # An SVG box with redundant collinear vertices and unnecessary metadata
    svg = """<svg viewBox="0 0 1000 1000" xmlns="http://www.w3.org/2000/svg">
      <!-- Comment -->
      <metadata><meta>Test</meta></metadata>
      <g id="redundant-g">
        <path d="M 0 0 L 250 0 L 500 0 L 750 0 L 1000 0 L 1000 1000 L 0 1000 Z" fill="#22d3ee" />
      </g>
    </svg>"""
    initial_nodes = count_svg_path_nodes(svg)
    initial_bytes = len(svg.encode("utf-8"))

    simplified = simplify_svg_document(svg, tolerance=1.0, use_geometric=True, use_scour=True)

    final_nodes = count_svg_path_nodes(simplified)
    final_bytes = len(simplified.encode("utf-8"))

    assert final_nodes < initial_nodes
    assert final_bytes < initial_bytes
    assert "<metadata>" not in simplified
