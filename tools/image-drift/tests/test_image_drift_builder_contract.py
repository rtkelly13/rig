"""Tests for the shared builder types and the hexagon the parametric builder scores against."""

from __future__ import annotations

import math

import pytest

from rig.image_drift.builder_contract import STYLE_DEFAULTS, FeatureRegion, variant_style
from rig.image_drift.geometry import CropBox
from rig.image_drift.hex_geometry import HEX_CENTER, hexagon_vertices
from rig.image_drift.variants import StyleOverrides, Variant

pytestmark = pytest.mark.ci


def test_feature_regions_default_to_a_shared_crop_box() -> None:
    """Reference and target must crop the same place unless deliberately offset."""
    region = FeatureRegion(key="k", label="l", x0=10, y0=20, x1=30, y1=40)
    assert region.bbox == region.ref_bbox == CropBox(10, 20, 30, 40)
    assert not region.has_ref_override


def test_feature_region_reports_an_explicit_reference_override() -> None:
    region = FeatureRegion(key="k", label="l", x0=10, y0=20, x1=30, y1=40, ref_x0=1, ref_y0=2, ref_x1=3, ref_y1=4)
    assert region.ref_bbox == CropBox(1, 2, 3, 4)
    assert region.has_ref_override


def test_variant_style_without_overrides_is_the_defaults() -> None:
    assert variant_style(Variant()) == STYLE_DEFAULTS


def test_variant_style_overrides_only_the_knobs_a_variant_sets() -> None:
    style = variant_style(Variant(style=StyleOverrides(trace_width=9.0, show_emblems=False)))
    assert style.trace_width == 9.0
    assert not style.show_emblems
    assert style.frame_width == STYLE_DEFAULTS.frame_width
    assert style.glow_wide == STYLE_DEFAULTS.glow_wide


def test_hexagon_is_regular_and_centred() -> None:
    radius = 186.2
    verts = hexagon_vertices(radius)
    assert len(verts) == 6
    for vertex in verts:
        assert math.dist((vertex.x, vertex.y), (HEX_CENTER.x, HEX_CENTER.y)) == pytest.approx(radius, abs=0.01)
    # Pointy-top: first vertex sits directly above the centre.
    assert verts[0].x == pytest.approx(HEX_CENTER.x)
    assert verts[0].y < HEX_CENTER.y
