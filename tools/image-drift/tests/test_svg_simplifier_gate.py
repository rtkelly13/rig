"""Tests for the simplification drift gate's refusal to ship a failing candidate.

The gate used to write its output unconditionally. Because `output_path`
defaults to the input path, a run that never met its budget would overwrite the
source SVG in place and still print a size-reduction report.
"""

from __future__ import annotations

from pathlib import Path
from typing import Any

import pytest

from rig.image_drift.metrics import DriftResult
from rig.image_drift.simplifier import pipeline
from rig.image_drift.simplifier.pipeline import (
    DriftGateNotMetError,
    optimize_svg_with_drift_gate,
)

pytestmark = pytest.mark.ci

SVG = '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 10 10"><path d="M 0,0 L 10,0 L 10,10 Z" fill="#abc"/></svg>'


def _stub_render(monkeypatch: pytest.MonkeyPatch) -> None:
    """Render to a real file without a browser."""

    def fake_render(svg_path: Path, png_path: Path, *args: Any, **kwargs: Any) -> Path:
        png_path.parent.mkdir(parents=True, exist_ok=True)
        png_path.write_bytes(b"\x89PNG\r\n\x1a\n")
        return png_path

    monkeypatch.setattr(pipeline, "render_svg", fake_render)


def _stub_drift(monkeypatch: pytest.MonkeyPatch, ssim: float, rmse: float) -> None:
    def fake_drift(*args: Any, **kwargs: Any) -> DriftResult:
        return DriftResult(rmse_pct=rmse, ssim=ssim, psnr_db=30.0)

    monkeypatch.setattr(pipeline, "compute_drift", fake_drift)


def test_failing_gate_refuses_to_overwrite_input(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    """An unmet budget must not overwrite the source SVG in place."""
    _stub_render(monkeypatch)
    _stub_drift(monkeypatch, ssim=0.50, rmse=40.0)

    svg = tmp_path / "logo.svg"
    svg.write_text(SVG, encoding="utf-8")

    with pytest.raises(DriftGateNotMetError):
        optimize_svg_with_drift_gate(svg_path=svg, min_ssim=0.995, max_rmse=1.0)

    assert svg.read_text(encoding="utf-8") == SVG, "input SVG was modified despite a failed gate"


def test_failing_gate_reports_not_passed_for_explicit_output(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    """With an explicit output the candidate is kept, but flagged as not passing."""
    _stub_render(monkeypatch)
    _stub_drift(monkeypatch, ssim=0.50, rmse=40.0)

    svg = tmp_path / "logo.svg"
    svg.write_text(SVG, encoding="utf-8")
    out = tmp_path / "candidate.svg"

    report = optimize_svg_with_drift_gate(svg_path=svg, output_path=out, min_ssim=0.995, max_rmse=1.0).report

    assert report.gate_passed is False
    assert out.exists()
    assert svg.read_text(encoding="utf-8") == SVG


def test_passing_gate_writes_and_reports_passed(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    """A met budget writes the candidate and records the pass."""
    _stub_render(monkeypatch)
    _stub_drift(monkeypatch, ssim=0.999, rmse=0.10)

    svg = tmp_path / "logo.svg"
    svg.write_text(SVG, encoding="utf-8")
    out = tmp_path / "candidate.svg"

    gated = optimize_svg_with_drift_gate(svg_path=svg, output_path=out, min_ssim=0.995, max_rmse=1.0)

    assert gated.report.gate_passed is True
    assert gated.path == out
    assert out.exists()
