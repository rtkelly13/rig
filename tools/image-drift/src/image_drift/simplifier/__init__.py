"""SVG Simplification & Vector Reverse-Engineering Package."""

from image_drift.simplifier.ast_parser import (
    PathCommand,
    SubPath,
    SVGPathTokenizer,
    parse_path_d,
    serialize_path_d,
)
from image_drift.simplifier.geometric import simplify_path_geometry
from image_drift.simplifier.pipeline import (
    DriftGateNotMetError,
    count_svg_path_nodes,
    optimize_svg_with_drift_gate,
    simplify_svg_document,
)
from image_drift.simplifier.report import (
    OptimizationReport,
    SimplificationPassReport,
)
from image_drift.simplifier.syntactic import (
    quantize_svg_coordinates,
    run_scour_pass,
    syntactic_minify_svg,
)

__all__ = [
    "DriftGateNotMetError",
    "OptimizationReport",
    "PathCommand",
    "SVGPathTokenizer",
    "SimplificationPassReport",
    "SubPath",
    "count_svg_path_nodes",
    "optimize_svg_with_drift_gate",
    "parse_path_d",
    "quantize_svg_coordinates",
    "run_scour_pass",
    "serialize_path_d",
    "simplify_path_geometry",
    "simplify_svg_document",
    "syntactic_minify_svg",
]
