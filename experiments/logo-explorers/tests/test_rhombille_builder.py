"""Tests for the rhombille (hexagon-and-circuit) builder and shared geometry.

The interlocking-columns explorer is covered in `test_columns_builder.py`.
This module covers the rhombille explorer plus the `hex_shapes` geometry.

These lock the properties the reconciler depends on: byte-identical output,
geometry that is actually on the shapes it claims to be on, a tiling that
covers without overlapping, and output that carries no font or `<mask>`
dependency.
"""

from __future__ import annotations

import math
import xml.etree.ElementTree as ET
from collections.abc import Sequence
from pathlib import Path

import pytest
from hex_shapes import (
    FRAME_NODE,
    FRAME_RADIUS,
    MEASURED_FRAME_NODE,
    WOOD_RADIUS,
    _project_onto_segment,
    _rotate_about_centre,
    arc_center,
    distance,
    frame_outline_path,
    herringbone_planks,
    hexagon_half_planes,
    rhombille_faces,
    socket_dome_path,
    touches_hexagon,
)
from rhombille_builder import (
    DEFAULT_VARIANT,
    EMBLEMS,
    FEATURE_REGIONS,
    SEAM_ANGLES,
    VARIANTS,
    build_svg_logo,
    circuit_geometry,
    emblem_box,
    glyph_paths,
    resolve_emblem_center,
)
from svg_builder import (
    CANVAS,
    CARD_INSET,
    FRAME_NODE_RADIUS,
    SOCKET_HALO_WIDTH,
)

from rig.image_drift.builder_contract import FRAME_LINE_WIDTH, NOTCH_RING_RATIO, variant_style
from rig.image_drift.hex_geometry import HEX_CENTER, Point, hexagon_vertices
from rig.image_drift.variants import UnknownVariantError

pytestmark = pytest.mark.ci


# ---------------------------------------------------------------------------
# Determinism
# ---------------------------------------------------------------------------


@pytest.mark.parametrize("variant", list(VARIANTS))
def test_build_is_byte_identical_across_runs(tmp_path: Path, variant: str) -> None:
    """The whole point of the builder: same inputs, same bytes."""
    first, second = tmp_path / "a.svg", tmp_path / "b.svg"
    build_svg_logo(first, variant=variant)
    build_svg_logo(second, variant=variant)
    assert first.read_bytes() == second.read_bytes()


def test_unknown_variant_raises(tmp_path: Path) -> None:
    """An undeclared variant must raise, not silently render the default.

    A silent fallback returns a wrong drawing that looks like a right one --
    exactly how the rtkelly-labs favicon shipped as a scaled-down master.
    """
    with pytest.raises(UnknownVariantError):
        build_svg_logo(tmp_path / "unknown.svg", variant="no-such-variant")


def test_build_creates_missing_parent_directories(tmp_path: Path) -> None:
    target = tmp_path / "nested" / "deeper" / "logo.svg"
    build_svg_logo(target)
    assert target.exists()


# ---------------------------------------------------------------------------
# Output portability
# ---------------------------------------------------------------------------


def _render_tree(tmp_path: Path, variant: str = DEFAULT_VARIANT) -> ET.Element:
    out = tmp_path / "logo.svg"
    build_svg_logo(out, variant=variant)
    return ET.fromstring(out.read_text(encoding="utf-8"))


@pytest.mark.parametrize("variant", list(VARIANTS))
def test_output_has_no_font_or_mask_dependency(tmp_path: Path, variant: str) -> None:
    """No `<text>` (needs a font installed) and no `<mask>` (poor non-browser support)."""
    svg = tmp_path / "logo.svg"
    build_svg_logo(svg, variant=variant)
    content = svg.read_text(encoding="utf-8")
    assert "<text" not in content
    assert "font-family" not in content
    assert "<mask" not in content
    assert "mask=" not in content


@pytest.mark.parametrize("variant", list(VARIANTS))
def test_circuit_gradient_spans_the_whole_canvas(tmp_path: Path, variant: str) -> None:
    """The circuit gradient must be one global ramp, not one per trace.

    With the default objectBoundingBox units each polyline gets its own
    cyan-to-magenta gradient, and a vertical trace has zero bounding-box width
    so it degenerates to a flat dark stroke. That is what the old `<mask>` was
    working around.
    """
    svg = tmp_path / "logo.svg"
    build_svg_logo(svg, variant=variant)
    content = svg.read_text(encoding="utf-8")
    gradient = content.split('id="circuit-global-grad"')[0].rsplit("<linearGradient", 1)[1]
    tail = content.split('id="circuit-global-grad"')[1].split(">")[0]
    assert 'gradientUnits="userSpaceOnUse"' in gradient + tail
    assert f'x2="{CANVAS}"' in tail
    assert f'y2="{CANVAS}"' in tail


def test_vertical_traces_exist_to_justify_the_global_gradient() -> None:
    """Guards the assumption above: a zero-width trace really is present."""
    paths = circuit_geometry("rhombille").paths
    verticals = [
        (start, end)
        for path in paths
        for start, end in zip(path, path[1:], strict=False)
        if abs(end.x - start.x) < 1e-9 and abs(end.y - start.y) > 1e-9
    ]
    assert verticals


def test_output_declares_expected_canvas(tmp_path: Path) -> None:
    root = _render_tree(tmp_path)
    assert root.get("viewBox") == f"0 0 {CANVAS} {CANVAS}"


# ---------------------------------------------------------------------------
# Frame + socket geometry
# ---------------------------------------------------------------------------


def test_frame_node_lies_on_the_frame_edge() -> None:
    """The old constant (329, 103) floated off the edge; this must not regress."""
    top, upper_right = hexagon_vertices(FRAME_RADIUS)[:2]
    cross = (upper_right.x - top.x) * (FRAME_NODE.y - top.y) - (upper_right.y - top.y) * (FRAME_NODE.x - top.x)
    assert abs(cross) / distance(top, upper_right) < 0.01


def test_frame_node_stays_near_its_measured_position() -> None:
    assert distance(FRAME_NODE, MEASURED_FRAME_NODE) < 12.0


def _parse_ring(d: str) -> dict[str, object]:
    """Pull radius, flags and endpoints out of a single-arc path."""
    tokens = d.split("A ")[1].replace(",", " ").split()
    start = tuple(float(v) for v in d.split("A ", maxsplit=1)[0].removeprefix("M ").split())
    return {
        "radius": float(tokens[0]),
        "large_arc": int(tokens[3]),
        "sweep": int(tokens[4]),
        "start": start,
        "end": (float(tokens[5]), float(tokens[6])),
    }


def _outline_points(d: str) -> list[Point]:
    return [Point(float(pair.split()[0]), float(pair.split()[1])) for pair in d.removeprefix("M ").split(" L ")]


def test_arc_center_matches_the_svg_spec_convention() -> None:
    """Independent check: endpoints at 0 and 90 degrees on the unit circle.

    Getting this sign wrong put the ring's centre on the mirror point, so the
    dot sat on its own ring's rim instead of inside it.
    """
    p1, p2 = Point(1.0, 0.0), Point(0.0, 1.0)
    on_origin = {
        (large_arc, sweep)
        for large_arc in (0, 1)
        for sweep in (0, 1)
        if distance(arc_center(p1, p2, 1.0, large_arc, sweep), Point(0.0, 0.0)) < 1e-9
    }
    assert on_origin == {(0, 1), (1, 0)}


@pytest.mark.parametrize("ring_radius", [12.0, 15.0, 20.0])
def test_frame_outline_never_leaves_the_hexagon_edges(ring_radius: float) -> None:
    """No bowing: every outline point lies exactly on the hexagon's boundary.

    A previous version led into the ring along the arc's tangent, which pulled
    the join points off the edge line and made the hexagon look bowed.
    """
    verts = hexagon_vertices(FRAME_RADIUS)
    edges = list(zip(verts, [*verts[1:], verts[0]], strict=True))
    for point in _outline_points(frame_outline_path(FRAME_RADIUS, FRAME_NODE, ring_radius)):
        distances = [distance(point, _project_onto_segment(point, a, b).point) for a, b in edges]
        assert min(distances) < 0.01, f"{point} is {min(distances):.3f}px off every edge"


def test_frame_outline_gap_straddles_the_socket_dot() -> None:
    """The break must be centred on the dot, so the ring slots into it."""
    ring_radius = FRAME_NODE_RADIUS * NOTCH_RING_RATIO
    points = _outline_points(frame_outline_path(FRAME_RADIUS, FRAME_NODE, ring_radius))
    # Open subpath: it resumes after the gap and stops just before it.
    after, before = points[0], points[-1]
    assert distance(after, FRAME_NODE) == pytest.approx(ring_radius, abs=0.01)
    assert distance(before, FRAME_NODE) == pytest.approx(ring_radius, abs=0.01)
    midpoint = Point((after.x + before.x) / 2.0, (after.y + before.y) / 2.0)
    assert distance(midpoint, FRAME_NODE) < 0.01


def test_frame_outline_is_a_single_open_subpath() -> None:
    """One stroke around the hexagon, not a closed loop that fills the gap."""
    d = frame_outline_path(FRAME_RADIUS, FRAME_NODE, 15.0)
    assert d.count("M") == 1
    assert "Z" not in d


def _parse_dome(d: str) -> dict[str, object]:
    """Split the two-quarter dome into its endpoints and arc parameters."""
    head, *arcs = d.replace("M ", "").split(" A ")
    x, y = (float(v) for v in head.split())
    start = Point(x, y)
    parsed = []
    for arc in arcs:
        tokens = arc.replace(",", " ").split()
        parsed.append(
            {
                "radius": float(tokens[0]),
                "large_arc": int(tokens[3]),
                "sweep": int(tokens[4]),
                "end": Point(float(tokens[5]), float(tokens[6])),
            }
        )
    return {"start": start, "arcs": parsed}


@pytest.mark.parametrize("ring_radius", [12.0, 15.0, 20.0])
def test_socket_dome_arcs_are_centred_on_the_commit_dot(ring_radius: float) -> None:
    """Every arc must curve about the dot, not about a mirror point."""
    dome = _parse_dome(socket_dome_path(FRAME_RADIUS, FRAME_NODE, ring_radius))
    point = dome["start"]
    for arc in dome["arcs"]:
        centre = arc_center(point, arc["end"], arc["radius"], arc["large_arc"], arc["sweep"])
        assert distance(centre, FRAME_NODE) < 0.05
        point = arc["end"]


@pytest.mark.parametrize("ring_radius", [12.0, 15.0, 20.0])
def test_socket_dome_stops_where_it_meets_the_frame_line(ring_radius: float) -> None:
    """The notch must terminate at the line crossings, not wrap past them.

    Wrapping the full way round closed the shape off and left the feeder trace
    crossing its own ring.
    """
    dome = _parse_dome(socket_dome_path(FRAME_RADIUS, FRAME_NODE, ring_radius))
    outline = _outline_points(frame_outline_path(FRAME_RADIUS, FRAME_NODE, ring_radius))
    dome_ends = [dome["start"], dome["arcs"][-1]["end"]]
    gap_ends = [outline[0], outline[-1]]
    for end in dome_ends:
        assert min(distance(end, gap) for gap in gap_ends) < 0.01


@pytest.mark.parametrize("ring_radius", [12.0, 15.0, 20.0])
def test_socket_dome_bulges_outward(ring_radius: float) -> None:
    """The apex must sit further from the centre than the dot does."""
    dome = _parse_dome(socket_dome_path(FRAME_RADIUS, FRAME_NODE, ring_radius))
    apex = dome["arcs"][0]["end"]
    assert distance(apex, HEX_CENTER) > distance(FRAME_NODE, HEX_CENTER)


def test_socket_dome_avoids_the_degenerate_semicircle() -> None:
    """A 180-degree arc has radius exactly half its chord — renderers differ.

    Splitting the dome at its apex keeps every chord safely inside the diameter.
    """
    ring_radius = FRAME_NODE_RADIUS * NOTCH_RING_RATIO
    dome = _parse_dome(socket_dome_path(FRAME_RADIUS, FRAME_NODE, ring_radius))
    assert len(dome["arcs"]) == 2
    point = dome["start"]
    for arc in dome["arcs"]:
        chord = distance(point, arc["end"])
        assert arc["large_arc"] == 0
        assert arc["radius"] > chord / 2 + 0.5
        point = arc["end"]


def test_socket_ring_clears_the_dot_and_its_halo() -> None:
    """Ring and dot must not merge into one blob."""
    ring_radius = FRAME_NODE_RADIUS * NOTCH_RING_RATIO
    ring_inner_edge = ring_radius - FRAME_LINE_WIDTH / 2.0
    halo_outer_edge = FRAME_NODE_RADIUS + SOCKET_HALO_WIDTH
    assert ring_inner_edge - halo_outer_edge >= 2.0


def test_wood_sits_inside_the_frame() -> None:
    assert WOOD_RADIUS < FRAME_RADIUS


# ---------------------------------------------------------------------------
# Tilings
# ---------------------------------------------------------------------------


def test_rhombille_covers_hexagon_with_three_faces_per_cell() -> None:
    faces = rhombille_faces(52.3, WOOD_RADIUS)
    assert len(faces) % 3 == 0
    assert {face.tone for face in faces} == {0, 1, 2}
    # Every face is a quad, and the tiling reaches past the clip radius.
    assert all(len(face.points) == 4 for face in faces)
    reach = max(distance(point, HEX_CENTER) for face in faces for point in face.points)
    assert reach >= WOOD_RADIUS


def test_herringbone_planks_are_two_to_one() -> None:
    for plank in herringbone_planks(23.0, WOOD_RADIUS):
        long_side, short_side = max(plank.width, plank.height), min(plank.width, plank.height)
        assert long_side == pytest.approx(2 * short_side)


def test_herringbone_planks_do_not_overlap() -> None:
    """The domino colouring must tile, not double-cover."""
    cell = 23.0
    planks = herringbone_planks(cell, 60.0)
    covered: set[tuple[int, int]] = set()
    for plank in planks:
        for i in range(int(round(plank.width / cell))):
            for j in range(int(round(plank.height / cell))):
                key = (int(round((plank.x + i * cell) / cell)), int(round((plank.y + j * cell) / cell)))
                assert key not in covered, f"cell {key} covered twice"
                covered.add(key)
    assert covered, "expected the herringbone to produce planks"


def test_herringbone_covers_a_contiguous_block() -> None:
    """No gaps: every lattice cell in the interior is claimed by some plank."""
    cell = 23.0
    planks = herringbone_planks(cell, 60.0)
    covered: set[tuple[int, int]] = set()
    for plank in planks:
        for i in range(int(round(plank.width / cell))):
            for j in range(int(round(plank.height / cell))):
                covered.add((int(round((plank.x + i * cell) / cell)), int(round((plank.y + j * cell) / cell))))
    xs = [k[0] for k in covered]
    ys = [k[1] for k in covered]
    # Probe well inside the generated block so edge truncation cannot matter.
    for gx in range(min(xs) + 3, max(xs) - 3):
        for gy in range(min(ys) + 3, max(ys) - 3):
            assert (gx, gy) in covered, f"gap at {(gx, gy)}"


# ---------------------------------------------------------------------------
# Circuit network
# ---------------------------------------------------------------------------


@pytest.mark.parametrize("tiling", ["rhombille", "herringbone"])
def test_circuit_includes_the_frame_node(tiling: str) -> None:
    assert any(node.centre == FRAME_NODE for node in circuit_geometry(tiling).nodes)


@pytest.mark.parametrize("tiling", ["rhombille", "herringbone"])
def test_circuit_segments_run_along_seam_directions(tiling: str) -> None:
    """Rhombille admits 30/90/150; a 45-degree weave admits only 45/135."""
    allowed = SEAM_ANGLES[tiling]
    for path in circuit_geometry(tiling).paths:
        for start, end in zip(path, path[1:], strict=False):
            dx, dy = end.x - start.x, end.y - start.y
            if math.hypot(dx, dy) < 1e-9:
                continue
            angle = math.degrees(math.atan2(dy, dx)) % 180.0
            assert any(abs(angle - a) < 2.5 or abs(angle - a) > 177.5 for a in allowed), (
                f"segment {start}->{end} at {angle:.1f} deg is off-seam"
            )


@pytest.mark.parametrize("tiling", ["rhombille", "herringbone"])
def test_circuit_stays_inside_the_canvas(tiling: str) -> None:
    circuit = circuit_geometry(tiling)
    for path in circuit.paths:
        for point in path:
            assert 0 <= point.x <= CANVAS and 0 <= point.y <= CANVAS
    for node in circuit.nodes:
        r = node.radius
        assert -r <= node.centre.x <= CANVAS + r and -r <= node.centre.y <= CANVAS + r


# ---------------------------------------------------------------------------
# Emblems
# ---------------------------------------------------------------------------


def test_glyph_paths_are_font_independent_geometry() -> None:
    run = glyph_paths("int")
    assert run.width == pytest.approx(30.0)
    assert run.commands, "expected drawn geometry for a supported string"
    assert all(cmd.startswith("M ") for cmd in run.commands)


def test_unknown_glyph_advances_without_drawing() -> None:
    """An unsupported character must leave a gap, not a missing-glyph box."""
    run = glyph_paths("§")
    assert run.commands == ()
    assert run.width == pytest.approx(10.0)


def test_glyph_advance_offsets_successive_characters() -> None:
    single = glyph_paths("<").commands
    double = glyph_paths("<<").commands
    assert len(double) == 2 * len(single)
    assert double[0] != double[1], "second glyph should be shifted along x"


def test_emblems_are_clear_of_circuit_geometry() -> None:
    """Every emblem must resolve to a position no trace or node intrudes on."""
    circuit = circuit_geometry("rhombille")
    circuit_width = variant_style(VARIANTS[DEFAULT_VARIANT]).trace_width
    for emblem in EMBLEMS:
        box = emblem_box(resolve_emblem_center(emblem, circuit, circuit_width), emblem.text, emblem.em_size)
        pad = circuit_width / 2.0 + 2.0
        for node in circuit.nodes:
            nearest = Point(
                max(box.x0 - pad, min(node.centre.x, box.x1 + pad)),
                max(box.y0 - pad, min(node.centre.y, box.y1 + pad)),
            )
            assert distance(node.centre, nearest) > node.radius, f"node {node} intrudes on emblem {emblem.text!r}"


def test_emblem_nudge_stays_close_to_the_measured_position() -> None:
    """Collision avoidance may move an emblem, but not relocate it."""
    circuit = circuit_geometry("rhombille")
    for emblem in EMBLEMS:
        assert distance(emblem.centre, resolve_emblem_center(emblem, circuit, 7.5)) <= 30.0


def test_emblem_resolution_is_deterministic() -> None:
    circuit = circuit_geometry("rhombille")
    first = resolve_emblem_center(EMBLEMS[0], circuit, 7.5)
    second = resolve_emblem_center(EMBLEMS[0], circuit, 7.5)
    assert first == second


def test_emblem_set_is_braces_and_angle_tag() -> None:
    """`int` was dropped by request; these two remain."""
    assert [emblem.text for emblem in EMBLEMS] == ["{}", "</>"]


# ---------------------------------------------------------------------------
# Document metadata and payload size
# ---------------------------------------------------------------------------


@pytest.mark.parametrize("variant", list(VARIANTS))
def test_output_carries_accessible_metadata(tmp_path: Path, variant: str) -> None:
    """A logo on a page needs a name screen readers can announce."""
    svg = tmp_path / "logo.svg"
    build_svg_logo(svg, variant=variant)
    content = svg.read_text(encoding="utf-8")
    assert "<title>" in content
    assert "<desc>" in content
    assert 'role="img"' in content
    assert 'aria-label="' in content


def test_root_element_omits_fixed_pixel_size(tmp_path: Path) -> None:
    """The viewBox carries the ratio; hard-coded px fights CSS and img sizing."""
    svg = tmp_path / "logo.svg"
    build_svg_logo(svg)
    root = svg.read_text(encoding="utf-8").split(">", maxsplit=2)[1]
    assert "viewBox=" in root
    assert 'width="' not in root
    assert 'height="' not in root


def test_root_element_drops_unused_namespaces(tmp_path: Path) -> None:
    """svgwrite emits xlink/ev declarations this document never uses."""
    svg = tmp_path / "logo.svg"
    build_svg_logo(svg)
    root = svg.read_text(encoding="utf-8").split(">", maxsplit=2)[1]
    assert "xmlns:ev" not in root
    assert "xmlns:xlink" not in root
    assert "baseProfile" not in root
    assert 'xmlns="http://www.w3.org/2000/svg"' in root


@pytest.mark.parametrize("variant", list(VARIANTS))
def test_payload_stays_small_enough_for_a_page_asset(tmp_path: Path, variant: str) -> None:
    """Uncounted off-clip geometry once pushed this to 65KB across 277 polygons."""
    svg = tmp_path / "logo.svg"
    build_svg_logo(svg, variant=variant)
    assert svg.stat().st_size < 32_000, f"{variant} is {svg.stat().st_size} bytes"


# ---------------------------------------------------------------------------
# Off-clip culling
# ---------------------------------------------------------------------------


def test_every_emitted_face_can_reach_the_clip() -> None:
    """Nothing invisible should survive into the document."""
    planes = hexagon_half_planes(WOOD_RADIUS)
    faces = rhombille_faces(52.3, WOOD_RADIUS)
    assert faces
    assert all(touches_hexagon(face.points, planes) for face in faces)


def test_culling_discards_the_bulk_of_the_lattice() -> None:
    """The lattice must cover the hexagon's rotated bounding box, most of which
    lies outside the clip — so culling should remove most of it."""
    rings_worth = rhombille_faces(52.3, WOOD_RADIUS)
    # Three faces per hexagonal cell; the unculled lattice was 273 faces.
    assert len(rings_worth) < 120
    assert len(rings_worth) % 3 == 0 or len(rings_worth) > 0


def test_culling_keeps_faces_covering_the_hexagon_centre() -> None:
    """A conservative cull must never drop geometry that is actually visible."""
    faces = rhombille_faces(52.3, WOOD_RADIUS)
    # The centre and a point near each vertex must all be covered by some face.
    probes = [HEX_CENTER, *hexagon_vertices(WOOD_RADIUS * 0.85)]
    for probe in probes:
        covered = any(_point_in_polygon(probe, face.points) for face in faces)
        assert covered, f"no face covers {probe}"


def _point_in_polygon(point: Point, polygon: Sequence[Point]) -> bool:
    """Even-odd ray cast, for verifying coverage."""
    x, y = point.x, point.y
    inside = False
    count = len(polygon)
    for index in range(count):
        ax, ay = polygon[index].x, polygon[index].y
        bx, by = polygon[(index + 1) % count].x, polygon[(index + 1) % count].y
        if (ay > y) != (by > y):
            crossing = ax + (y - ay) / (by - ay) * (bx - ax)
            if x < crossing:
                inside = not inside
    return inside


@pytest.mark.parametrize("cell", [23.0, 31.0])
def test_herringbone_culling_keeps_only_reachable_planks(cell: float) -> None:
    planes = hexagon_half_planes(WOOD_RADIUS)
    planks = herringbone_planks(cell, WOOD_RADIUS)
    assert planks
    for plank in planks:
        x, y, w, h = plank.x, plank.y, plank.width, plank.height
        corners = [Point(x, y), Point(x + w, y), Point(x + w, y + h), Point(x, y + h)]
        rotated = [_rotate_about_centre(corner, 45.0) for corner in corners]
        assert touches_hexagon(rotated, planes)


# ---------------------------------------------------------------------------
# Card geometry
# ---------------------------------------------------------------------------


def test_card_is_square_and_concentric_with_the_hexagon() -> None:
    """It was 436x430 at (32, 33), so the artwork sat 2px above centre."""
    size = CANVAS - 2 * CARD_INSET
    assert CARD_INSET + size / 2 == pytest.approx(HEX_CENTER.x)
    assert CARD_INSET + size / 2 == pytest.approx(HEX_CENTER.y)


# ---------------------------------------------------------------------------
# Favicon variant
# ---------------------------------------------------------------------------


def test_favicon_variant_exists_and_drops_the_emblems() -> None:
    """Below ~48px the emblems are smudges, so the small cut omits them."""
    style = variant_style(VARIANTS["favicon"])
    assert not style.show_emblems
    assert style.trunk_only
    assert style.trace_width > variant_style(VARIANTS[DEFAULT_VARIANT]).trace_width


def test_favicon_output_contains_no_emblem_geometry(tmp_path: Path) -> None:
    full, small = tmp_path / "full.svg", tmp_path / "small.svg"
    build_svg_logo(full, variant=DEFAULT_VARIANT)
    build_svg_logo(small, variant="favicon")
    # The emblem wash is the only fill-opacity rect in the document.
    assert "fill-opacity" in full.read_text(encoding="utf-8")
    assert "fill-opacity" not in small.read_text(encoding="utf-8")
    assert small.stat().st_size < full.stat().st_size


def test_trunk_only_reduces_the_network() -> None:
    full = circuit_geometry("rhombille")
    trunk = circuit_geometry("rhombille", trunk_only=True)
    assert len(trunk.paths) < len(full.paths)
    assert len(trunk.nodes) < len(full.nodes)
    # The socket dot must survive: it is the logo's signature detail.
    assert any(distance(node.centre, FRAME_NODE) < 0.5 for node in trunk.nodes)


# ---------------------------------------------------------------------------
# Feature regions
# ---------------------------------------------------------------------------


def test_no_region_targets_a_removed_emblem() -> None:
    """`int` was dropped from the artwork, so scoring it penalised every candidate."""
    assert "int_badge" not in {region.key for region in FEATURE_REGIONS}
