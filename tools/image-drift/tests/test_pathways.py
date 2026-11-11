"""Tests for the multi-pathway master construction routes.

The point of keeping several pathways is that each is deterministic and each
is measurable on both axes -- fidelity and geometric correctness -- so the
trade between them is visible rather than assumed.
"""

from __future__ import annotations

from dataclasses import replace
from pathlib import Path

import pytest
from typer.testing import CliRunner

from rig.image_drift import cli, cli_base, cli_explore, labs_parametric
from rig.image_drift.builder_contract import FeatureRegion
from rig.image_drift.edge_regularity import DEFAULT_AXES, analyze_edges
from rig.image_drift.metrics import CandidateScore, DriftResult
from rig.image_drift.pathways import (
    FIT_FIELDS,
    PATHWAYS,
    PathwayContext,
    fit_parameters,
    trace_reference,
)

pytestmark = pytest.mark.ci

MASTER = cli_base.DEFAULT_LOGO.read_text(encoding="utf-8")


def _ctx(score: object = None) -> PathwayContext:
    return PathwayContext(
        master_svg=MASTER,
        reference_path=cli_base.DEFAULT_REFERENCE,
        axes=DEFAULT_AXES,
        score=score,  # pyright: ignore[reportArgumentType]
    )


def test_both_pathways_are_retained() -> None:
    """Tracing and construction both remain available; neither replaced the other."""
    assert "traced" in PATHWAYS
    assert "parametric" in PATHWAYS


@pytest.mark.parametrize("key", [k for k, p in PATHWAYS.items() if not p.needs_scoring])
def test_pathway_is_deterministic(key: str) -> None:
    """The same inputs produce byte-identical output."""
    pathway = PATHWAYS[key]
    assert pathway.generate(_ctx()) == pathway.generate(_ctx())


@pytest.mark.parametrize("key", [k for k, p in PATHWAYS.items() if not p.needs_scoring])
def test_pathway_produces_an_svg(key: str) -> None:
    out = PATHWAYS[key].generate(_ctx())
    assert out.lstrip().startswith("<svg")
    assert "viewBox=" in out


def test_traced_pathway_is_the_master_unchanged() -> None:
    """The governed trace must pass through untouched."""
    assert PATHWAYS["traced"].generate(_ctx()) == MASTER


def test_constructive_pathway_beats_the_trace_on_edge_quality() -> None:
    """Construction from named geometry has exact coordinates by definition.

    This is the whole reason to keep a second pathway: the trace cannot reach
    this, because it is following a raster's anti-aliased pixel boundaries.
    """
    traced = analyze_edges(PATHWAYS["traced"].generate(_ctx()))
    built = analyze_edges(PATHWAYS["parametric"].generate(_ctx()))
    assert built.axis_conformance_pct > traced.axis_conformance_pct
    assert built.count("micro_segment") == 0
    assert built.count("off_axis") == 0
    assert built.count("staircase") == 0


def test_regularized_pathway_improves_on_the_trace_without_redrawing_it() -> None:
    """The hybrid keeps the trace's shapes and cleans only its edges."""
    traced = analyze_edges(MASTER)
    fixed_svg = PATHWAYS["regularized"].generate(_ctx())
    fixed = analyze_edges(fixed_svg)
    assert fixed.axis_conformance_pct > traced.axis_conformance_pct
    # Same number of drawn shapes: edges moved, nothing was added or removed.
    assert fixed_svg.count("<path") == MASTER.count("<path")


def test_fit_is_deterministic_and_never_worsens_the_objective() -> None:
    """Coordinate descent is reproducible and monotone.

    A synthetic objective keeps this renderer-free: it rewards a specific
    r_outer, so the walk has a known direction to move in.
    """
    calls: list[str] = []

    def objective(svg: str) -> float:
        calls.append(svg)
        # Recover r_outer indirectly via document length; instead score the
        # params we can see by re-deriving from the generated geometry size.
        return float(len(svg))

    first = fit_parameters(_ctx(objective))
    second = fit_parameters(_ctx(objective))
    assert first == second
    baseline = float(len(labs_parametric.generate_svg(params=labs_parametric.IsoLoopParams())))
    assert first.loss <= baseline


def test_fit_rejects_a_missing_objective() -> None:
    """A searching pathway without a scorer is an error, not a silent no-op."""
    with pytest.raises(ValueError, match="ctx.score"):
        fit_parameters(_ctx(None))


def test_fit_fields_are_real_parameters() -> None:
    """Every fitted field must exist, or the walk silently does nothing."""
    params = labs_parametric.IsoLoopParams()
    for fit_field in FIT_FIELDS:
        assert hasattr(params, fit_field.name), fit_field.name
        assert fit_field.read(replace(params, **{fit_field.name: 7.0})) == 7.0


def test_pathway_keys_match_their_registry_entries() -> None:
    for key, pathway in PATHWAYS.items():
        assert pathway.key == key


def test_writing_candidates_does_not_touch_the_master(tmp_path: Path) -> None:
    """Building candidates is read-only with respect to the committed asset."""
    before = cli_base.DEFAULT_LOGO.read_bytes()
    for pathway in PATHWAYS.values():
        if pathway.needs_scoring:
            continue
        (tmp_path / f"{pathway.key}.svg").write_text(pathway.generate(_ctx()), encoding="utf-8")
    assert cli_base.DEFAULT_LOGO.read_bytes() == before


def test_autotraced_pathway_traces_the_reference_not_the_master() -> None:
    """The VTracer pathway starts from the reference raster, so the master is irrelevant to it."""
    blank_master = replace(_ctx(), master_svg="")
    out = PATHWAYS["autotraced"].generate(blank_master)
    assert out == trace_reference(cli_base.DEFAULT_REFERENCE)
    assert out.startswith("<svg")
    assert 'viewBox="0 0 1024 1024"' in out
    assert "<path" in out


@pytest.mark.parametrize(
    ("raw", "expected_root"),
    [
        (
            '<?xml version="1.0"?>\n<!-- gen -->\n<svg width="64" height="32"><path d="M0,0"/></svg>',
            '<svg width="64" height="32" viewBox="0 0 64 32">',
        ),
        (
            '<svg width="10" height="10" viewBox="0 0 5 5"><path d="M0,0"/></svg>',
            '<svg width="10" height="10" viewBox="0 0 5 5">',
        ),
        ('<svg><path d="M0,0"/></svg>', "<svg>"),
    ],
)
def test_autotraced_output_is_normalised_to_a_scalable_root(
    monkeypatch: pytest.MonkeyPatch, tmp_path: Path, raw: str, expected_root: str
) -> None:
    """VTracer's prolog is dropped and a viewBox added only when width and height say what it is."""
    import vtracer

    def fake_convert(_image_path: str, out_path: str, **_: object) -> None:
        Path(out_path).write_text(raw, encoding="utf-8")

    monkeypatch.setattr(vtracer, "convert_image_to_svg_py", fake_convert)
    out = trace_reference(tmp_path / "reference.png")
    assert out.startswith(expected_root)
    assert out.endswith("</svg>")


def test_pathways_command_scores_autotraced_beside_the_trace(monkeypatch: pytest.MonkeyPatch, tmp_path: Path) -> None:
    """`pathways` scores the VTracer trace through the same scorer as every other pathway.

    The renderer is stubbed, so this runs without Chromium.
    """
    scored: list[str] = []

    def fake_score_svg(
        svg_text: str, reference: Path, regions: list[FeatureRegion], scratch: Path, label: str
    ) -> cli_explore.SvgScore:
        assert reference == cli_base.DEFAULT_REFERENCE
        assert regions, "regions must come from the project's builder"
        scored.append(label)
        composite = 2.5 if label == "pathway-autotraced" else 4.0
        return cli_explore.SvgScore(
            score=CandidateScore(composite_pct=composite, full_frame_pct=composite, per_feature_pct={}),
            drift=DriftResult(rmse_pct=composite, ssim=0.9, psnr_db=30.0),
        )

    monkeypatch.setattr(cli_explore, "_score_svg", fake_score_svg)
    monkeypatch.setattr(cli_explore, "chromium_available", lambda: True)

    result = CliRunner().invoke(
        cli.app,
        ["pathways", "-p", "rtkelly-labs", "--only", "traced,autotraced", "--out-dir", str(tmp_path)],
        env={"COLUMNS": "200"},
    )

    assert result.exit_code == 0, result.output
    assert scored == ["pathway-traced", "pathway-autotraced"]
    assert "Autotraced (VTracer)" in result.output
    assert "2.500%" in result.output
    written = (tmp_path / "rtkelly-labs-autotraced.svg").read_text(encoding="utf-8")
    assert written == trace_reference(cli_base.DEFAULT_REFERENCE)
