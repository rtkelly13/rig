"""Hexagon-and-circuit logo builder: the rhombille approach.

Unscored explorer, kept in `experiments/` rather than the package. The
interlocking-columns design is in `svg_builder.py`; this one is the earlier
direction -- a rhombille (or herringbone) tiling inside a glowing hexagon frame,
with a seam-locked circuit network, a notched socket dot and etched emblems.

Drive it through the CLI's builder-module hook, which is the supported way to run
a structurally different mark:

    PYTHONPATH=experiments/logo-explorers uv run --project tools/image-drift --with svgwrite image-reconcile build \\
        -B experiments/logo-explorers/rhombille_builder.py \\
        --variant rhombille_reference --svg /tmp/hex.svg

Every geometry helper comes from `hex_shapes` and every gradient and glow
definition from `svg_builder`, so fixes to the shared parts -- the arc-sweep convention, the
global gradient units, off-clip culling -- apply here too rather than drifting.
"""

from __future__ import annotations

import math
from collections.abc import Iterable, Sequence
from dataclasses import dataclass
from pathlib import Path

import svgwrite
from hex_shapes import (
    FRAME_NODE,
    FRAME_RADIUS,
    WOOD_RADIUS,
    Box,
    Circuit,
    Node,
    distance,
    frame_outline_path,
    herringbone_planks,
    rhombille_faces,
    scaled_circuit_point,
    socket_dome_path,
)
from image_drift.builder_contract import NODE_RADIUS_SCALE, FeatureRegion, variant_style
from image_drift.hex_geometry import HEX_CENTER, Point, hexagon_vertices
from image_drift.variants import StyleOverrides, Variant, VariantRegistry, resolve_variant
from svg_builder import (
    BEVEL_COLOUR,
    CANVAS,
    CARD_FILL,
    DOT_NOTCH_WIDTH,
    FRAME_NODE_RADIUS,
    HEX_BASE_FILL,
    SEAM_COLOUR,
    SOCKET_HALO_WIDTH,
    TONES,
    Tone,
    prepare_document,
    strip_unused_root_attributes,
)
from svgwrite.container import Group

DEFAULT_VARIANT = "rhombille_reference"

LOGO_DESC = (
    "A hexagonal parquet tile overlaid with a glowing circuit network, carrying "
    "the braces and angle-bracket emblems of generated source code."
)

__all__ = [
    "DEFAULT_VARIANT",
    "FEATURE_REGIONS",
    "VARIANTS",
    "build_svg_logo",
]


# ---------------------------------------------------------------------------
# Feature regions, measured off icon.jpg normalised to 500x500
# ---------------------------------------------------------------------------

FEATURE_REGIONS: list[FeatureRegion] = [
    FeatureRegion(
        key="top_right_socket",
        label="Notched Socket Ring & Frame Node",
        x0=292,
        y0=76,
        x1=372,
        y1=152,
    ),
    FeatureRegion(
        key="brace_badge",
        label="{ } Braces & Upper-Left Tiling",
        x0=136,
        y0=156,
        x1=212,
        y1=228,
    ),
    FeatureRegion(
        key="angle_badge",
        label="</> Angle Tag & Lower Tiling",
        x0=214,
        y0=340,
        x1=290,
        y1=408,
    ),
    FeatureRegion(
        key="center_hub",
        label="Central Circuit Hub & Seam Junction",
        x0=200,
        y0=200,
        x1=300,
        y1=300,
    ),
]


VARIANTS: VariantRegistry = {
    "rhombille_reference": Variant(
        name="A: Reference Weights",
        description="House look at the measured reference weights: 3.0 frame, 5.0 traces, warm oak.",
        tiling="rhombille",
        cell=52.3,
        tone="warm",
    ),
    "rhombille_hairline": Variant(
        name="B: Hairline",
        description=("Lighter throughout - 2.5 frame, 4.0 traces, smaller dots. Reads finer and more intricate."),
        tiling="rhombille",
        cell=52.3,
        tone="warm",
        style=StyleOverrides(frame_width=2.5, trace_width=4.0, node_scale=0.72, ring_ratio=2.2),
    ),
    "rhombille_bold_trace": Variant(
        name="C: Bold Trace, Thin Frame",
        description="Thin 3.0 frame against heavier 6.5 traces, maximising the contrast between them.",
        tiling="rhombille",
        cell=52.3,
        tone="warm",
        style=StyleOverrides(frame_width=3.0, trace_width=6.5, node_scale=0.95),
    ),
    "rhombille_soft_glow": Variant(
        name="D: Wide Bloom",
        description="Reference weights with a broader, softer neon bloom around every stroke.",
        tiling="rhombille",
        cell=52.3,
        tone="warm",
        style=StyleOverrides(glow_wide=9.0, glow_tight=2.5),
    ),
    "rhombille_amber": Variant(
        name="E: Deep Amber",
        description="Reference weights on a deeper, higher-contrast amber wood palette.",
        tiling="rhombille",
        cell=52.3,
        tone="amber",
    ),
    "rhombille_fine": Variant(
        name="F: Fine Cells",
        description="Denser 41px rhombille cells; more seams, finer read at small sizes.",
        tiling="rhombille",
        cell=41.0,
        tone="warm",
        style=StyleOverrides(trace_width=4.5),
    ),
    "direction_b_weave": Variant(
        name="Direction B: Minimal Herringbone",
        description=(
            "The actual parquet weave as a few large flat planks. No grain, no circuit, no "
            "emblems: the weave is the mark."
        ),
        mark="herringbone",
        tiling="herringbone",
        cell=62.0,
        tone="warm",
        style=StyleOverrides(frame_width=4.0, flat_faces=True, show_circuit=False, show_emblems=False),
    ),
    "favicon": Variant(
        name="Favicon",
        description="Small-size cut: coarse cells, trunk-only circuit, no emblems, heavier lines.",
        tiling="rhombille",
        cell=105.0,
        tone="warm",
        style=StyleOverrides(
            frame_width=7.0,
            trace_width=9.0,
            ring_ratio=2.1,
            node_scale=1.15,
            glow_wide=5.0,
            glow_tight=1.5,
            show_emblems=False,
            trunk_only=True,
        ),
    ),
    "herringbone_warm": Variant(
        name="G: 45 deg Herringbone",
        description="Literal parquet weave: 23px herringbone planks, the pattern icon.jpg uses.",
        tiling="herringbone",
        cell=23.0,
        tone="warm",
    ),
}


# ---------------------------------------------------------------------------
# Circuit network -- segments run parallel to the tiling seam directions
# ---------------------------------------------------------------------------

# Seam directions each tiling admits, in degrees mod 180.
SEAM_ANGLES: dict[str, tuple[float, ...]] = {
    "rhombille": (30.0, 90.0, 150.0),
    "herringbone": (45.0, 135.0),
}

# Rhombille seams run at 30/90/150 degrees, so these segments do too.
_RHOMBILLE_PATHS: list[list[Point]] = [
    [Point(92, 159), Point(171, 204), Point(250, 250), Point(329, 295), Point(408, 341)],
    [Point(171, 204), Point(250, 159), Point(329, 204), Point(329, 295)],
    [Point(171, 204), Point(171, 295), Point(250, 340), Point(329, 295)],
    [Point(250, 68), Point(250, 159)],
    # Bottom spur detours left of the `</>` emblem, staying on 30/150-degree
    # seam directions, then rejoins the frame's bottom vertex.
    [Point(250, 340), Point(211, 363), Point(211, 409), Point(250, 432)],
]

_RHOMBILLE_NODES: list[Node] = [
    Node(Point(92, 159), 7.5),
    Node(Point(171, 204), 8.0),
    Node(Point(250, 159), 7.0),
    Node(Point(329, 204), 7.0),
    Node(Point(171, 295), 7.0),
    Node(Point(250, 250), 9.0),
    Node(Point(250, 340), 7.5),
    Node(Point(329, 295), 8.0),
    Node(Point(408, 341), 7.5),
    Node(Point(250, 68), 7.5),
    Node(Point(250, 432), 7.5),
]

# A 45-degree weave has seams in only two directions, so neither horizontal nor
# vertical runs are on-seam. Every segment here is a diamond diagonal.
_HERRINGBONE_PATHS: list[list[Point]] = [
    [Point(92, 250), Point(171, 171), Point(250, 92)],
    [Point(92, 250), Point(171, 329), Point(250, 408)],
    [Point(408, 250), Point(329, 171), Point(250, 92)],
    [Point(408, 250), Point(329, 329), Point(250, 408)],
    [Point(171, 171), Point(250, 250), Point(329, 329)],
    [Point(329, 171), Point(250, 250), Point(171, 329)],
]

_HERRINGBONE_NODES: list[Node] = [
    Node(Point(92, 250), 8.0),
    Node(Point(408, 250), 8.0),
    Node(Point(250, 92), 7.5),
    Node(Point(250, 408), 7.5),
    Node(Point(250, 250), 9.0),
    Node(Point(171, 171), 7.0),
    Node(Point(329, 171), 7.0),
    Node(Point(171, 329), 7.0),
    Node(Point(329, 329), 7.0),
]


def circuit_geometry(tiling: str, node_scale: float = NODE_RADIUS_SCALE, trunk_only: bool = False) -> Circuit:
    """Seam-locked circuit paths and nodes, scaled to the measured wood radius.

    A feeder connects the socket node to the nearest node below it using only
    directions the tiling actually admits: a run along the steepest available
    seam, then a diagonal along the shallowest. A naive vertical-then-horizontal
    elbow would cross the grain on both tilings.
    """
    raw_paths = _RHOMBILLE_PATHS if tiling == "rhombille" else _HERRINGBONE_PATHS
    raw_nodes = _RHOMBILLE_NODES if tiling == "rhombille" else _HERRINGBONE_NODES

    if trunk_only:
        # Keep the main spine only, and the dots that terminate it.
        raw_paths = raw_paths[:1]
        spine = {point for path in raw_paths for point in path}
        raw_nodes = [node for node in raw_nodes if node.centre in spine]

    paths = [tuple(scaled_circuit_point(p) for p in path) for path in raw_paths]
    nodes = [Node(scaled_circuit_point(node.centre), node.radius * node_scale) for node in raw_nodes]

    feeder = _feeder_path(tiling, nodes)
    if feeder is not None:
        paths.append(feeder)
    nodes.append(Node(FRAME_NODE, FRAME_NODE_RADIUS))
    return Circuit(tuple(paths), tuple(nodes))


def _feeder_path(tiling: str, nodes: Sequence[Node]) -> tuple[Point, ...] | None:
    """Route the socket node into the network along seam directions only."""
    angles = SEAM_ANGLES[tiling]
    # "Steep" means vertical when the tiling has a vertical seam -- not the
    # largest angle, since 150 degrees is shallower than 90, just mirrored.
    steep = 90.0 if 90.0 in angles else max(angles)
    shallow = min(angles)
    fx, fy = FRAME_NODE.x, FRAME_NODE.y

    below = [node.centre for node in nodes if node.centre.y > fy]
    if not below:
        return None
    target = min(below, key=lambda centre: (abs(centre.x - fx), centre.y))
    tx, ty = target.x, target.y

    run = abs(tx - fx)
    # The shallow diagonal consumes this much vertical distance.
    drop = run * math.tan(math.radians(shallow))
    if math.isclose(run, 0.0, abs_tol=1e-9):
        return (FRAME_NODE, target)

    if steep == 90.0 and ty - drop >= fy:
        # Vertical drop, then a diagonal into the node.
        return (FRAME_NODE, Point(round(fx, 3), round(ty - drop, 3)), target)

    # No steep vertical available (herringbone): a single diagonal only works
    # when the node already sits on one, so fall back to a two-diagonal dogleg.
    sign = 1.0 if tx > fx else -1.0
    half = (ty - fy + sign * (tx - fx) * math.tan(math.radians(shallow))) / 2.0
    bend = Point(round(fx + sign * half / math.tan(math.radians(shallow)), 3), round(fy + half, 3))
    return (FRAME_NODE, bend, target)


# ---------------------------------------------------------------------------
# Emblem glyph geometry (font-independent)
# ---------------------------------------------------------------------------
#
# Each glyph is a list of SVG path commands on a 10 wide x 14 tall em box with
# the origin at the box's top-left. Stroking these keeps output identical on
# any machine, which `<text font-family="Courier New">` cannot promise.

GLYPH_ADVANCE = 10.0
GLYPH_EM = 14.0

_GLYPHS: dict[str, tuple[str, ...]] = {
    # Braces carry an explicit mid-cusp at x=3.2 / x=6.8; a single smooth curve
    # from top to bottom reads as a parenthesis at logo sizes.
    "{": (
        "M 7.4 0.8 C 5.8 0.8 6.1 4.6 5.7 6.0 "
        "C 5.5 6.8 4.5 7.0 3.2 7.0 "
        "C 4.5 7.0 5.5 7.2 5.7 8.0 "
        "C 6.1 9.4 5.8 13.2 7.4 13.2",
    ),
    "}": (
        "M 2.6 0.8 C 4.2 0.8 3.9 4.6 4.3 6.0 "
        "C 4.5 6.8 5.5 7.0 6.8 7.0 "
        "C 5.5 7.0 4.5 7.2 4.3 8.0 "
        "C 3.9 9.4 4.2 13.2 2.6 13.2",
    ),
    "<": ("M 8 2.5 L 2.5 7 L 8 11.5",),
    ">": ("M 2 2.5 L 7.5 7 L 2 11.5",),
    "/": ("M 8 1.5 L 2 12.5",),
    "i": ("M 5 5.5 L 5 13", "M 5 2.4 L 5 2.9"),
    "n": ("M 3 13 L 3 5.5", "M 3 7.4 Q 5.5 5 8 7.4 L 8 13"),
    "t": ("M 5 2.5 L 5 11 Q 5 13 7 13", "M 2.5 5.8 L 7.6 5.8"),
    " ": (),
}


@dataclass(slots=True, frozen=True)
class Emblem:
    centre: Point
    text: str
    colour: str
    em_size: float


@dataclass(slots=True, frozen=True)
class GlyphRun:
    """Stroke path commands for a string, and its total advance width."""

    commands: tuple[str, ...]
    width: float


# Positions measured off the reference at 500x500. The reference carries
# exactly these.
EMBLEMS: list[Emblem] = [
    Emblem(Point(174.0, 192.0), "{}", "#7ef9ff", 26.0),
    Emblem(Point(252.0, 375.0), "</>", "#c9a4ff", 22.0),
]


def _translate_path(d: str, dx: float) -> str:
    """Shift an absolute-coordinate path command string along x by `dx`.

    Only the absolute commands used in `_GLYPHS` (M, L, C, Q) appear, and each
    takes plain x y pairs, so shifting every x is sufficient.
    """
    parts = d.split()
    out: list[str] = []
    index = 0
    while index < len(parts):
        token = parts[index]
        if token.isalpha():
            out.append(token)
            index += 1
            continue
        out.append(f"{float(token) + dx:g} {float(parts[index + 1]):g}")
        index += 2
    return " ".join(out)


# ---------------------------------------------------------------------------
# SVG Builder Engine
# ---------------------------------------------------------------------------


def emblem_box(centre: Point, text: str, em_size: float, margin: float = 3.0) -> Box:
    """The rectangle an emblem occupies."""
    ink_width = glyph_paths(text).width
    half_w = (ink_width * em_size / GLYPH_EM) / 2.0 + margin
    half_h = em_size / 2.0 + margin
    return Box(centre.x - half_w, centre.y - half_h, centre.x + half_w, centre.y + half_h)


def _segment_hits_box(start: Point, end: Point, box: Box, pad: float) -> bool:
    """Liang-Barsky test for a segment overlapping a padded axis-aligned box."""
    x0, y0, x1, y1 = box.x0 - pad, box.y0 - pad, box.x1 + pad, box.y1 + pad
    dx, dy = end.x - start.x, end.y - start.y
    t_enter, t_exit = 0.0, 1.0

    for delta, origin, lo, hi in ((dx, start.x, x0, x1), (dy, start.y, y0, y1)):
        if abs(delta) < 1e-12:
            if origin < lo or origin > hi:
                return False
            continue
        t_a = (lo - origin) / delta
        t_b = (hi - origin) / delta
        t_enter = max(t_enter, min(t_a, t_b))
        t_exit = min(t_exit, max(t_a, t_b))
        if t_enter > t_exit:
            return False
    return True


def _circle_hits_box(center: Point, radius: float, box: Box) -> bool:
    """True when a circle overlaps an axis-aligned box."""
    nearest_x = max(box.x0, min(center.x, box.x1))
    nearest_y = max(box.y0, min(center.y, box.y1))
    return math.hypot(center.x - nearest_x, center.y - nearest_y) <= radius


def resolve_emblem_center(emblem: Emblem, circuit: Circuit, circuit_width: float) -> Point:
    """Nudge an emblem off the circuit while keeping it near its measured spot.

    The measured reference positions are the intent, but the house circuit
    topology occupies some of those cells -- a junction node lands inside the
    `{}` and a spur runs through the `</>`. Rather than hand-tuning either
    dataset, search a deterministic spiral for the nearest clear position. The
    search order is fixed, so output stays byte-identical run to run, and it
    self-corrects if the circuit or emblem data changes later.

    Returns:
        The first clear centre found, or the original centre if none is.
    """
    pad = circuit_width / 2.0 + 2.0

    def is_clear(centre: Point) -> bool:
        box = emblem_box(centre, emblem.text, emblem.em_size)
        for path in circuit.paths:
            for start, end in zip(path, path[1:], strict=False):
                if _segment_hits_box(start, end, box, pad):
                    return False
        return all(not _circle_hits_box(node.centre, node.radius + 2.0, box) for node in circuit.nodes)

    if is_clear(emblem.centre):
        return emblem.centre

    for step in range(1, 17):
        radius = step * 3.0
        for index in range(24):
            angle = math.radians(index * 15)
            candidate = Point(
                round(emblem.centre.x + radius * math.cos(angle), 3),
                round(emblem.centre.y + radius * math.sin(angle), 3),
            )
            if is_clear(candidate):
                return candidate
    return emblem.centre


def glyph_paths(text: str) -> GlyphRun:
    """Translate `text` into stroke path commands plus total advance width.

    Unknown characters advance without drawing, so an unsupported glyph
    degrades to a gap rather than a missing-font box.
    """
    commands: list[str] = []
    for index, char in enumerate(text):
        offset = index * GLYPH_ADVANCE
        commands.extend(_translate_path(d, offset) for d in _GLYPHS.get(char, ()))
    return GlyphRun(tuple(commands), len(text) * GLYPH_ADVANCE)


def _svg_points(points: Iterable[Point]) -> list[Sequence[float]]:
    return [(p.x, p.y) for p in points]


def build_svg_logo(
    output_path: Path,
    width: int = CANVAS,
    height: int = CANVAS,
    variant: str = DEFAULT_VARIANT,
) -> None:
    """Build the hexagon-and-circuit logo for `variant` at `output_path`."""
    config = VARIANTS[resolve_variant(variant, VARIANTS, DEFAULT_VARIANT)]
    tiling, cell = config.tiling, config.cell
    if tiling is None or cell is None:
        msg = f"variant {variant!r} needs a tiling and a cell size, got {tiling!r} and {cell!r}"
        raise TypeError(msg)
    style = variant_style(config)
    circuit_width = style.trace_width
    frame_width = style.frame_width
    ring_radius = FRAME_NODE_RADIUS * style.ring_ratio
    node_scale = style.node_scale
    show_frame = style.show_frame
    show_circuit = style.show_circuit
    flat_faces = style.flat_faces
    tone = TONES[config.tone or "warm"]

    dwg = prepare_document(output_path, width, height, style, tone, desc=LOGO_DESC)
    defs = dwg.defs

    wood_verts = hexagon_vertices(WOOD_RADIUS)
    clip_hex = dwg.clipPath(id="wood-hex-clip")
    clip_hex.add(dwg.polygon(points=_svg_points(wood_verts)))
    defs.add(clip_hex)

    # Circuit geometry is resolved first: the socket ring's neck has to open the
    # way the feeder trace actually leaves the dot.
    circuit = circuit_geometry(tiling, node_scale=node_scale, trunk_only=style.trunk_only)
    frame_outline_d = frame_outline_path(FRAME_RADIUS, FRAME_NODE, ring_radius)
    socket_dome_d = socket_dome_path(FRAME_RADIUS, FRAME_NODE, ring_radius)
    if not show_circuit:
        circuit = Circuit((), ())

    # The base is a plain closed hexagon; only the stroked outline carries the
    # socket gap, so the fill has no notch artefact.
    dwg.add(dwg.polygon(points=_svg_points(hexagon_vertices(FRAME_RADIUS)), fill=HEX_BASE_FILL, stroke="none"))

    # The base is a plain closed hexagon; only the stroked outline carries the
    # socket gap, so the fill has no notch artefact.
    dwg.add(dwg.polygon(points=_svg_points(hexagon_vertices(FRAME_RADIUS)), fill=HEX_BASE_FILL, stroke="none"))

    # --- Layer 1: tiling ---------------------------------------------------
    if tiling == "rhombille":
        wood_group = Group(clip_path="url(#wood-hex-clip)")
        for face in rhombille_faces(cell, WOOD_RADIUS):
            wood_group.add(
                dwg.polygon(
                    points=_svg_points(face.points),
                    fill=_face_fill(face.tone, flat_faces, tone),
                    stroke=SEAM_COLOUR,
                    stroke_width=1.5,
                )
            )
    else:
        wood_group = Group(clip_path="url(#wood-hex-clip)", transform=f"rotate(45 {HEX_CENTER.x:g} {HEX_CENTER.y:g})")
        inset = 0.7
        for plank in herringbone_planks(cell, WOOD_RADIUS):
            wood_group.add(
                dwg.rect(
                    insert=(plank.x + inset, plank.y + inset),
                    size=(plank.width - 2 * inset, plank.height - 2 * inset),
                    fill=_face_fill(plank.index, flat_faces, tone),
                    stroke=SEAM_COLOUR,
                    stroke_width=1.4,
                )
            )
    dwg.add(wood_group)

    # Edging, drawn unrotated over the tiling. Two crisp concentric rims rather
    # than one thick stroke: the outer rim hides the ragged ends of clipped
    # faces, the inner highlight gives the wood a defined lip. Mitred joins keep
    # the six corners sharp instead of rounding them off.
    dwg.add(
        dwg.polygon(
            points=_svg_points(wood_verts),
            fill="none",
            stroke=BEVEL_COLOUR,
            stroke_width=4.5,
            stroke_linejoin="miter",
        )
    )
    dwg.add(
        dwg.polygon(
            points=_svg_points(hexagon_vertices(WOOD_RADIUS - 4.0)),
            fill="none",
            stroke="#3a2717",
            stroke_width=1.2,
            stroke_linejoin="miter",
            stroke_opacity=0.55,
        )
    )

    # --- Layer 2: frame outline + socket ring ------------------------------
    frame_group = Group(filter="url(#glow-circuit)")
    for outline in (frame_outline_d, socket_dome_d) if show_frame else ():
        frame_group.add(
            dwg.path(
                d=outline,
                fill="none",
                stroke="url(#circuit-global-grad)",
                stroke_width=frame_width,
                stroke_linejoin="miter",
                stroke_linecap="round",
            )
        )
    dwg.add(frame_group)

    # --- Layer 3: circuit network -----------------------------------------
    # Dark backing clipped to the tiling so it cannot spill onto the card,
    # then the gradient trace stroked straight on top. No <mask> anywhere.
    # The dark cut is a single uniform width everywhere -- around every dot and
    # along every trace -- so the notching reads as one deliberate treatment.
    backing = Group(
        clip_path="url(#wood-hex-clip)",
        stroke=CARD_FILL,
        stroke_width=circuit_width + 2 * DOT_NOTCH_WIDTH,
        stroke_linecap="round",
        stroke_linejoin="round",
        fill="none",
    )
    for path in circuit.paths:
        backing.add(dwg.polyline(points=_svg_points(path)))
    for node in circuit.nodes:
        is_socket = distance(node.centre, FRAME_NODE) < 0.5
        halo = SOCKET_HALO_WIDTH if is_socket else DOT_NOTCH_WIDTH
        backing.add(
            dwg.circle(
                center=(node.centre.x, node.centre.y),
                r=node.radius + halo,
                fill=CARD_FILL,
                stroke="none",
            )
        )
    dwg.add(backing)

    traces = Group(filter="url(#glow-circuit)")
    for path in circuit.paths:
        traces.add(
            dwg.polyline(
                points=_svg_points(path),
                fill="none",
                stroke="url(#circuit-global-grad)",
                stroke_width=circuit_width,
                stroke_linecap="round",
                stroke_linejoin="round",
            )
        )
    for node in circuit.nodes:
        traces.add(
            dwg.circle(
                center=(node.centre.x, node.centre.y),
                r=node.radius,
                fill="url(#circuit-global-grad)",
                stroke="none",
            )
        )
    dwg.add(traces)

    # --- Layer 4: developer emblems ---------------------------------------
    for emblem in EMBLEMS if style.show_emblems else []:
        _add_emblem(dwg, resolve_emblem_center(emblem, circuit, circuit_width), emblem)

    dwg.save(pretty=True)
    strip_unused_root_attributes(output_path)


def _face_fill(face_index: int, flat: bool, tone: Tone) -> str:
    """Gradient fill per face, or a flat tone when the variant asks for it.

    Flat tones drop the pseudo-material shading, which is what makes the tiling
    read as a graphic mark rather than as a photograph of a floor.
    """
    if flat:
        return tone.faces[face_index].start
    return f"url(#wood-face-{face_index})"


def _add_emblem(dwg: svgwrite.Drawing, centre: Point, emblem: Emblem) -> None:
    """Draw a glyph string as stroked geometry, etched into the tiling.

    A faint dark wash sits behind the strokes for legibility. The reference
    etches its emblems into the surface; the previous 90%-opaque black card
    read as a pasted sticker.
    """
    run = glyph_paths(emblem.text)
    ink_width, em_size = run.width, emblem.em_size
    scale = em_size / GLYPH_EM
    origin_x = centre.x - (ink_width * scale) / 2.0
    origin_y = centre.y - em_size / 2.0

    group = Group(filter="url(#glow-circuit)")
    # 0.55 occludes the tiling enough to hold contrast -- the reference does
    # darken the surface behind each emblem -- without the hard-edged
    # 90%-opaque black card that read as a pasted sticker.
    group.add(
        dwg.rect(
            insert=(origin_x - 5, origin_y - 3),
            size=(ink_width * scale + 10, em_size + 6),
            rx=5,
            ry=5,
            fill="#0a0705",
            fill_opacity=0.42,
        )
    )
    stroked = Group(
        transform=f"translate({origin_x:g} {origin_y:g}) scale({scale:g})",
        stroke=emblem.colour,
        stroke_width=1.7,
        stroke_linecap="round",
        stroke_linejoin="round",
        fill="none",
        stroke_opacity=0.97,
    )
    for d in run.commands:
        stroked.add(dwg.path(d=d))
    group.add(stroked)
    dwg.add(group)
