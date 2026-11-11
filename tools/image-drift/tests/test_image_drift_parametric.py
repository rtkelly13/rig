"""Tests for the parametric explorer.

The explorer exists to propose candidates, never to become the master by
accident, and it must not repeat the flat-facet mistake that sank the earlier
CAD attempt.
"""

from __future__ import annotations

import re
from pathlib import Path

import pytest

from rig.image_drift import cli_base, hex_geometry, labs_builder, labs_parametric
from rig.image_drift.hex_geometry import Point
from rig.image_drift.variants import UnknownVariantError

pytestmark = pytest.mark.ci


def test_generate_svg_is_deterministic() -> None:
    """Identical parameters must produce byte-identical output."""
    assert labs_parametric.generate_svg("baseline") == labs_parametric.generate_svg("baseline")


def test_presets_are_distinct() -> None:
    """Each preset renders differently; otherwise a sweep is meaningless."""
    rendered = {k: labs_parametric.generate_svg(k) for k in labs_parametric.PRESETS}
    assert len(set(rendered.values())) == len(rendered)


def test_unknown_preset_raises() -> None:
    with pytest.raises(UnknownVariantError):
        labs_parametric.generate_svg("no-such-preset")


def test_every_facet_has_a_gradient_fill() -> None:
    """No facet may be a flat fill.

    The abandoned CAD generator drew unshaded polygons, which stripped out the
    planar lighting and made the mark look dead. Per-facet gradients are the
    fix, so the absence of flat fills is the regression guard.
    """
    svg = labs_parametric.generate_svg("baseline")
    flat = [m for m in re.findall(r'<polygon[^>]*fill="([^"]+)"', svg) if not m.startswith("url(")]
    assert not flat, f"facets with flat fills: {flat}"


def test_each_facet_gets_its_own_gradient() -> None:
    """Gradient vectors are projected per facet, not shared canvas-wide."""
    svg = labs_parametric.generate_svg("baseline")
    assert svg.count("<polygon") == svg.count("<linearGradient")


def test_with_overrides_coerces_and_validates() -> None:
    params = labs_parametric.IsoLoopParams()
    assert params.with_overrides({"r_outer": "166"}).r_outer == pytest.approx(166.0)
    assert params.with_overrides({"loop_center": "400,410"}).loop_center == Point(400.0, 410.0)
    with pytest.raises(KeyError):
        params.with_overrides({"not_a_field": "1"})
    with pytest.raises(ValueError):
        params.with_overrides({"r_outer": "wide"})


def test_satisfies_the_builder_contract(tmp_path: Path) -> None:
    """Exports what `--builder-module` needs, so it works with compare/preview."""
    assert labs_parametric.DEFAULT_VARIANT in labs_parametric.VARIANTS
    out = tmp_path / "candidate.svg"
    labs_parametric.build_svg_logo(out, variant="baseline")
    assert out.read_text(encoding="utf-8").startswith("<svg")


def test_feature_regions_match_the_master() -> None:
    """Shared regions keep candidate and incumbent scores comparable."""
    assert labs_parametric.FEATURE_REGIONS == labs_builder.FEATURE_REGIONS


def test_explorer_is_not_a_registered_project_builder() -> None:
    """No routine command may route to the explorer.

    Registration would let `build -p rtkelly-labs` overwrite the traced master
    with a parametric render. Promotion must stay the only path.
    """
    registered = {cfg.builder_module.name for cfg in cli_base.PROJECTS.values()}
    assert "labs_parametric.py" not in registered


def test_reuses_shared_hexagon_geometry() -> None:
    """Hexagon vertices come from hex_geometry, not a second local copy."""
    assert not hasattr(labs_parametric, "hex_verts")
    assert labs_parametric.hexagon_vertices is hex_geometry.hexagon_vertices


def test_loops_are_half_turn_symmetric() -> None:
    """The second loop is the first rotated 180 degrees about the canvas centre."""
    params = labs_parametric.IsoLoopParams()
    mirrored = labs_parametric.rot180(params.loop_center, params.center)
    assert labs_parametric.rot180(mirrored, params.center) == params.loop_center


def test_isometric_basis_is_on_the_30_90_150_axes() -> None:
    basis = labs_parametric.iso_basis()
    assert basis.u.x == pytest.approx(3**0.5 / 2) and basis.u.y == pytest.approx(0.5)
    assert basis.v.x == pytest.approx(-(3**0.5) / 2) and basis.v.y == pytest.approx(0.5)
    assert basis.w == Point(0.0, 1.0)
