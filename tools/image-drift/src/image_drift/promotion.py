"""The gate a parametric candidate must pass to replace the governed master.

The master is a trace scoring ~97% SSIM against its reference. It is the sole
source for every production cut, so replacing it is a one-way door: nothing can
recompute the trace if a worse candidate overwrites it.

This module is deliberately pure -- it takes already-computed scores and returns
a verdict -- so the rule is testable without a browser, and so the decision can
be re-run live at promotion time rather than trusted from a stored report.
"""

from __future__ import annotations

from dataclasses import dataclass, field

from image_drift.metrics import TIE_THRESHOLD_PCT, CandidateScore, DriftResult

MAX_FEATURE_REGRESSION_PCT = 0.25
"""How far one feature region may regress even when the composite improves.

`FRAME_WEIGHT` gives the full frame half the composite, so without this a
candidate could win by rendering the flat background well while damaging the
crossover core -- the region that carries the mark's identity.
"""


@dataclass(frozen=True)
class GateVerdict:
    """Whether a candidate may replace the incumbent, and why."""

    promotable: bool
    reasons: list[str] = field(default_factory=list[str])
    composite_delta: float = 0.0
    """Incumbent composite minus candidate composite; positive means better."""
    ssim_delta: float = 0.0
    """Candidate SSIM minus incumbent SSIM; positive means better."""
    worst_feature_delta: float = 0.0
    """Largest per-region regression; positive means a region got worse."""
    worst_feature_key: str | None = None


def evaluate_promotion(
    incumbent: CandidateScore,
    incumbent_drift: DriftResult,
    candidate: CandidateScore,
    candidate_drift: DriftResult,
    *,
    tie_threshold: float = TIE_THRESHOLD_PCT,
    max_feature_regression: float = MAX_FEATURE_REGRESSION_PCT,
) -> GateVerdict:
    """Decide whether `candidate` may replace `incumbent`.

    Promotable only if all three hold:

    1. The composite beats the incumbent by more than the tie threshold. A tie
       is not a win -- `TIE_THRESHOLD_PCT` is already this project's definition
       of "too close to rank", so the gate reuses it rather than inventing one.
    2. Structural similarity does not regress.
    3. No single feature region regresses by more than `max_feature_regression`.

    PSNR is reported elsewhere but not gated: it tracks SSIM closely, and
    gating it only over-constrains.

    Both scores must come from the same scoring pass -- `score_candidate`
    normalises through a fixed canvas, so figures from different runs are not
    comparable.
    """
    reasons: list[str] = []

    composite_delta = incumbent.composite_pct - candidate.composite_pct
    if composite_delta < tie_threshold:
        reasons.append(
            f"composite {candidate.composite_pct:.3f}% vs incumbent {incumbent.composite_pct:.3f}% "
            f"(needs to beat it by >{tie_threshold}pp, got {composite_delta:+.3f}pp)"
        )

    ssim_delta = candidate_drift.ssim - incumbent_drift.ssim
    if ssim_delta < 0:
        reasons.append(
            f"SSIM regressed: {candidate_drift.ssim:.4f} vs incumbent {incumbent_drift.ssim:.4f} ({ssim_delta:+.4f})"
        )

    worst_key: str | None = None
    worst_delta = 0.0
    for key, incumbent_pct in incumbent.per_feature_pct.items():
        if key not in candidate.per_feature_pct:
            reasons.append(f"candidate is missing feature region {key!r}")
            continue
        delta = candidate.per_feature_pct[key] - incumbent_pct
        if delta > worst_delta:
            worst_delta, worst_key = delta, key
    if worst_delta > max_feature_regression:
        reasons.append(f"feature {worst_key!r} regressed by {worst_delta:+.3f}pp (limit {max_feature_regression}pp)")

    return GateVerdict(
        promotable=not reasons,
        reasons=reasons,
        composite_delta=composite_delta,
        ssim_delta=ssim_delta,
        worst_feature_delta=worst_delta,
        worst_feature_key=worst_key,
    )
