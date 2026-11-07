"""Closed-Loop SVG Simplification Pipeline with Visual Drift Verification."""

from __future__ import annotations

import tempfile
from dataclasses import dataclass
from pathlib import Path
from xml.etree import ElementTree

# `simplify` can be pointed at any SVG on disk, so parsing goes through
# defusedxml: entity expansion on an SVG is a cheap denial of service.
from defusedxml.ElementTree import fromstring as _safe_fromstring

from rig.image_drift.imaging import render_svg
from rig.image_drift.metrics import compute_drift
from rig.image_drift.simplifier.ast_parser import parse_path_d
from rig.image_drift.simplifier.geometric import simplify_path_geometry
from rig.image_drift.simplifier.report import (
    OptimizationReport,
)
from rig.image_drift.simplifier.svg_attrs import path_data
from rig.image_drift.simplifier.syntactic import (
    quantize_svg_coordinates,
    run_scour_pass,
)


class DriftGateNotMetError(RuntimeError):
    """Raised when simplification cannot meet its drift budget and would overwrite its input."""


def count_svg_path_nodes(svg_text: str) -> int:
    """Count total path nodes/commands across all <path> elements in an SVG document."""
    try:
        root = _safe_fromstring(svg_text)
    except ElementTree.ParseError:
        return 0

    total_nodes = 0
    for elem in root.iter():
        d = path_data(elem)
        if d is not None:
            total_nodes += len(parse_path_d(d))

    return total_nodes


def simplify_svg_document(
    svg_text: str,
    tolerance: float = 0.5,
    use_geometric: bool = True,
    use_scour: bool = True,
    precision: int | None = None,
) -> str:
    """Execute multi-pass SVG simplification across geometric and syntactic tiers."""
    current = svg_text

    # Pass 1: Geometric & topological decimation on raw clean coordinates
    if use_geometric and tolerance > 0.0:
        try:
            ElementTree.register_namespace("", "http://www.w3.org/2000/svg")
            root = _safe_fromstring(current)
            for elem in root.iter():
                original_d = path_data(elem)
                if original_d is not None:
                    simplified_d = simplify_path_geometry(original_d, tolerance=tolerance)
                    elem.attrib["d"] = simplified_d

            current = ElementTree.tostring(root, encoding="unicode")
        except ElementTree.ParseError:
            pass

    # Pass 2: Scour document normalization & scrubbing
    if use_scour:
        current = run_scour_pass(current)

    # Pass 3: Viewbox-aware coordinate quantization
    return quantize_svg_coordinates(current, precision=precision)


@dataclass(frozen=True, slots=True)
class GatedOutput:
    """Where the kept candidate was written, and how it scored."""

    path: Path
    report: OptimizationReport


def optimize_svg_with_drift_gate(
    svg_path: Path,
    output_path: Path | None = None,
    tolerance: float = 0.5,
    min_ssim: float = 0.995,
    max_rmse: float = 1.0,
    max_iterations: int = 4,
    use_scour: bool = True,
    precision: int | None = None,
) -> GatedOutput:
    """Optimize an SVG with closed-loop visual regression gating via Playwright & SSIM."""
    original_text = svg_path.read_text(encoding="utf-8")
    original_size = len(original_text.encode("utf-8"))
    original_nodes = count_svg_path_nodes(original_text)

    # Render baseline reference raster
    with tempfile.TemporaryDirectory() as tmp_dir:
        tmp_path = Path(tmp_dir)
        baseline_png = tmp_path / "baseline.png"
        render_svg(svg_path, baseline_png, 1000, 1000)

        current_tolerance = tolerance
        best_tolerance = tolerance
        best_candidate_svg = original_text
        best_ssim = -1.0
        best_rmse = 0.0
        best_psnr = 0.0
        gate_passed = False

        for iteration in range(max_iterations):
            candidate_svg = simplify_svg_document(
                original_text,
                tolerance=current_tolerance,
                use_geometric=True,
                use_scour=use_scour,
                precision=precision,
            )

            candidate_svg_path = tmp_path / f"candidate_{iteration}.svg"
            candidate_svg_path.write_text(candidate_svg, encoding="utf-8", newline="\n")
            candidate_png = tmp_path / f"candidate_{iteration}.png"
            render_svg(candidate_svg_path, candidate_png, 1000, 1000)

            drift = compute_drift(baseline_png, candidate_png)

            # Keep the highest-fidelity candidate seen, not merely the last one:
            # tolerance backoff usually improves fidelity, but nothing guarantees
            # it monotonically, and on exhaustion the last is not always the best.
            if drift.ssim >= best_ssim:
                best_candidate_svg = candidate_svg
                best_ssim = drift.ssim
                best_rmse = drift.rmse_pct
                best_psnr = drift.psnr_db
                best_tolerance = current_tolerance

            # If within error budget, accept candidate
            if drift.ssim >= min_ssim and drift.rmse_pct <= max_rmse:
                gate_passed = True
                break

            # Drift too high: halve tolerance and retry
            current_tolerance *= 0.5

    out_file = output_path or svg_path

    # A failed gate must not silently overwrite the input. `output_path` defaults
    # to `svg_path`, so an ungated write here would degrade the source in place
    # while the report still printed a size reduction.
    if not gate_passed and out_file == svg_path:
        raise DriftGateNotMetError(
            f"Simplification of {svg_path} did not meet the drift budget "
            f"(ssim {best_ssim:.4f} < {min_ssim}, rmse {best_rmse:.3f}% > {max_rmse}%). "
            "Refusing to overwrite the input in place; pass --output to keep the candidate."
        )

    out_file.parent.mkdir(parents=True, exist_ok=True)
    out_file.write_text(best_candidate_svg, encoding="utf-8", newline="\n")

    final_size = len(best_candidate_svg.encode("utf-8"))
    final_nodes = count_svg_path_nodes(best_candidate_svg)

    size_red_pct = float((original_size - final_size) / original_size * 100.0) if original_size > 0 else 0.0
    node_red_pct = float((original_nodes - final_nodes) / original_nodes * 100.0) if original_nodes > 0 else 0.0

    report = OptimizationReport(
        original_size_bytes=original_size,
        final_size_bytes=final_size,
        size_reduction_pct=round(size_red_pct, 2),
        original_node_count=original_nodes,
        final_node_count=final_nodes,
        node_reduction_pct=round(node_red_pct, 2),
        tolerance=best_tolerance,
        ssim=round(best_ssim, 4),
        psnr_db=round(best_psnr, 2),
        rmse_pct=round(best_rmse, 3),
        output_svg=best_candidate_svg,
        gate_passed=gate_passed,
    )

    return GatedOutput(out_file, report)
