"""Data models for SVG simplification and drift gating reports."""

from __future__ import annotations

from dataclasses import dataclass, field


@dataclass
class SimplificationPassReport:
    """Report for an individual optimization pass."""

    pass_name: str
    size_bytes: int
    reduction_pct: float


def _default_passes() -> list[SimplificationPassReport]:
    return []


@dataclass
class OptimizationReport:
    """Consolidated report for full multi-pass SVG optimization."""

    original_size_bytes: int
    final_size_bytes: int
    size_reduction_pct: float

    original_node_count: int
    final_node_count: int
    node_reduction_pct: float

    tolerance: float
    ssim: float | None = None
    psnr_db: float | None = None
    rmse_pct: float | None = None

    passes: list[SimplificationPassReport] = field(default_factory=_default_passes)
    output_svg: str = ""

    gate_passed: bool = False
    """Whether a candidate met the ssim/rmse budget within the iteration limit."""
