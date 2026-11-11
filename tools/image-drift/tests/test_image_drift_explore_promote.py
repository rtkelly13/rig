"""Tests for the explore/promote safety boundary.

The governed master is a trace that nothing can recompute, so the load-bearing
property is that only `promote --confirm`, after a live gate win, can ever
rewrite it. Scoring is stubbed so these run without a browser.
"""

from __future__ import annotations

import hashlib
from pathlib import Path

import pytest
from typer.testing import CliRunner

from rig.image_drift import cli, cli_base, cli_explore, labs_builder
from rig.image_drift.builder_contract import FeatureRegion
from rig.image_drift.metrics import CandidateScore, DriftResult

pytestmark = pytest.mark.ci

REGION_KEYS = [r.key for r in labs_builder.FEATURE_REGIONS]


def _digests() -> tuple[str, str]:
    """(committed asset, packaged master) digests."""
    master = labs_builder.BUILDER_ASSETS / "master.svg"
    return (
        hashlib.sha256(cli_base.DEFAULT_LOGO.read_bytes()).hexdigest(),
        hashlib.sha256(master.read_bytes()).hexdigest(),
    )


def _stub_scoring(monkeypatch: pytest.MonkeyPatch, *, candidate_composite: float, candidate_ssim: float) -> None:
    """Score the incumbent as 4.0/0.97 and every candidate as given."""

    def fake_score_svg(
        svg_text: str, reference: Path, regions: list[FeatureRegion], scratch: Path, label: str
    ) -> cli_explore.SvgScore:
        is_incumbent = label == "incumbent"
        composite = 4.0 if is_incumbent else candidate_composite
        ssim = 0.97 if is_incumbent else candidate_ssim
        scratch.mkdir(parents=True, exist_ok=True)
        path = scratch / f"{label}.svg"
        path.write_text(svg_text, encoding="utf-8")
        return cli_explore.SvgScore(
            score=CandidateScore(
                composite_pct=composite,
                full_frame_pct=composite,
                per_feature_pct=dict.fromkeys(REGION_KEYS, composite),
            ),
            drift=DriftResult(rmse_pct=composite, ssim=ssim, psnr_db=30.0),
        )

    monkeypatch.setattr(cli_explore, "_score_svg", fake_score_svg)
    monkeypatch.setattr(cli_explore, "chromium_available", lambda: True)


def test_explore_never_touches_committed_assets(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    """Exploration is read-only: candidates land only in the variations tree."""
    _stub_scoring(monkeypatch, candidate_composite=1.0, candidate_ssim=0.99)
    before = _digests()

    result = CliRunner().invoke(
        cli.app,
        ["explore", "-p", "rtkelly-labs", "--variations-dir", str(tmp_path), "--sweep", "r_outer=150,158"],
    )

    assert result.exit_code == 0, result.output
    assert _digests() == before, "explore modified a committed asset"
    assert list(tmp_path.rglob("*.svg")), "explore wrote no snapshots"


def test_explore_exits_2_when_nothing_beats_the_incumbent(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    """A clean run that finds no winner is exit 2, distinct from an error."""
    _stub_scoring(monkeypatch, candidate_composite=9.0, candidate_ssim=0.50)
    result = CliRunner().invoke(cli.app, ["explore", "-p", "rtkelly-labs", "--variations-dir", str(tmp_path)])
    assert result.exit_code == 2, result.output


def test_explore_rejects_an_oversized_sweep(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    """The candidate cap is enforced before any rendering happens."""
    _stub_scoring(monkeypatch, candidate_composite=1.0, candidate_ssim=0.99)
    result = CliRunner().invoke(
        cli.app,
        [
            "explore",
            "-p",
            "rtkelly-labs",
            "--variations-dir",
            str(tmp_path),
            "--sweep",
            "r_outer=1,2,3,4,5",
            "--max-candidates",
            "2",
        ],
    )
    assert result.exit_code == 1


def test_promote_requires_confirm(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    """A winning candidate still needs an explicit --confirm to be written."""
    _stub_scoring(monkeypatch, candidate_composite=1.0, candidate_ssim=0.99)
    runner = CliRunner()
    runner.invoke(
        cli.app,
        ["explore", "-p", "rtkelly-labs", "--variations-dir", str(tmp_path), "--run-id", "r1"],
    )
    before = _digests()

    result = runner.invoke(
        cli.app,
        ["promote", "-p", "rtkelly-labs", "--run-id", "r1", "--candidate", "1", "--variations-dir", str(tmp_path)],
    )

    assert result.exit_code == 1, result.output
    assert "Dry run" in result.output
    assert _digests() == before, "promote wrote the master without --confirm"


def test_promote_refuses_a_losing_candidate_even_with_confirm(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    """The gate is re-run live, so a stale 'winner' is still refused."""
    _stub_scoring(monkeypatch, candidate_composite=1.0, candidate_ssim=0.99)
    runner = CliRunner()
    runner.invoke(
        cli.app,
        ["explore", "-p", "rtkelly-labs", "--variations-dir", str(tmp_path), "--run-id", "r2"],
    )
    before = _digests()

    # The master has "moved" since the run: the candidate now loses.
    _stub_scoring(monkeypatch, candidate_composite=9.0, candidate_ssim=0.50)
    result = runner.invoke(
        cli.app,
        [
            "promote",
            "-p",
            "rtkelly-labs",
            "--run-id",
            "r2",
            "--candidate",
            "1",
            "--variations-dir",
            str(tmp_path),
            "--confirm",
        ],
    )

    assert result.exit_code == 2, result.output
    assert _digests() == before, "promote wrote a losing candidate over the master"


def test_promote_rejects_an_unknown_run(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    """A missing run is an error, not a silent no-op."""
    _stub_scoring(monkeypatch, candidate_composite=1.0, candidate_ssim=0.99)
    result = CliRunner().invoke(
        cli.app,
        [
            "promote",
            "-p",
            "rtkelly-labs",
            "--run-id",
            "nope",
            "--candidate",
            "1",
            "--variations-dir",
            str(tmp_path),
            "--confirm",
        ],
    )
    assert result.exit_code == 1


@pytest.mark.parametrize("command", ["explore", "promote"])
def test_explore_and_promote_refuse_non_labs_projects(command: str, tmp_path: Path) -> None:
    """Both commands only drive labs_builder, so another project would overwrite the wrong assets."""
    result = CliRunner().invoke(
        cli.app, [command, "-p", "parquet", "--run-id", "r1", "--variations-dir", str(tmp_path)]
    )
    assert result.exit_code == 1
    assert "Only 'rtkelly-labs'" in result.output
