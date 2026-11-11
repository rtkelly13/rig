"""Tests for the master-promotion gate.

Pure scoring logic: no rendering, no browser, so the rule that protects the
traced master is verifiable everywhere the suite runs.
"""

from __future__ import annotations

import pytest

from image_drift.metrics import CandidateScore, DriftResult
from image_drift.promotion import evaluate_promotion

pytestmark = pytest.mark.ci

REGIONS = ("crossover_core", "top_left_loop", "top_right_loop")


def score(composite: float, **features: float) -> CandidateScore:
    per_feature = {key: features.get(key, composite) for key in REGIONS}
    return CandidateScore(composite_pct=composite, full_frame_pct=composite, per_feature_pct=per_feature)


def drift(ssim: float) -> DriftResult:
    return DriftResult(rmse_pct=100.0 * (1.0 - ssim), ssim=ssim, psnr_db=30.0)


INCUMBENT = score(4.00)
INCUMBENT_DRIFT = drift(0.9699)


def test_gate_accepts_a_clear_win() -> None:
    """Better composite, no SSIM regression, no feature regression."""
    verdict = evaluate_promotion(INCUMBENT, INCUMBENT_DRIFT, score(3.00), drift(0.9750))
    assert verdict.promotable, verdict.reasons
    assert verdict.composite_delta == pytest.approx(1.00)


def test_gate_rejects_a_tie() -> None:
    """Beating the incumbent by less than the tie threshold is not a win."""
    verdict = evaluate_promotion(INCUMBENT, INCUMBENT_DRIFT, score(3.70), drift(0.9750))
    assert not verdict.promotable
    assert any("composite" in r for r in verdict.reasons)


def test_gate_rejects_ssim_regression() -> None:
    """A large composite win cannot buy a structural regression."""
    verdict = evaluate_promotion(INCUMBENT, INCUMBENT_DRIFT, score(2.00), drift(0.9500))
    assert not verdict.promotable
    assert any("SSIM regressed" in r for r in verdict.reasons)
    assert verdict.ssim_delta < 0


def test_gate_rejects_single_feature_regression() -> None:
    """A candidate cannot win on the frame while damaging the crossover core."""
    candidate = score(3.00, crossover_core=4.90)  # incumbent's core is 4.00
    verdict = evaluate_promotion(INCUMBENT, INCUMBENT_DRIFT, candidate, drift(0.9750))
    assert not verdict.promotable
    assert verdict.worst_feature_key == "crossover_core"
    assert any("crossover_core" in r for r in verdict.reasons)


def test_gate_tolerates_feature_regression_within_budget() -> None:
    """A regression inside the budget does not block an otherwise clear win."""
    candidate = score(3.00, crossover_core=4.20)  # +0.20pp, under the 0.25pp limit
    verdict = evaluate_promotion(INCUMBENT, INCUMBENT_DRIFT, candidate, drift(0.9750))
    assert verdict.promotable, verdict.reasons


def test_gate_is_not_reflexive() -> None:
    """The incumbent cannot promote over itself; otherwise promotion could loop."""
    verdict = evaluate_promotion(INCUMBENT, INCUMBENT_DRIFT, INCUMBENT, INCUMBENT_DRIFT)
    assert not verdict.promotable
    assert verdict.composite_delta == pytest.approx(0.0)


def test_gate_rejects_candidate_missing_a_feature_region() -> None:
    """A candidate scored over fewer regions is not comparable."""
    partial = CandidateScore(composite_pct=1.0, full_frame_pct=1.0, per_feature_pct={"crossover_core": 1.0})
    verdict = evaluate_promotion(INCUMBENT, INCUMBENT_DRIFT, partial, drift(0.99))
    assert not verdict.promotable
    assert any("missing feature region" in r for r in verdict.reasons)
