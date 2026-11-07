"""Interlocking-columns logo explorer, and the document scaffold the explorers share.

Unscored: no promote gate, pathway or test compares it against a reference. It
was `rig.image_drift.svg_builder`; the types the shipped builders need
(`FeatureRegion`, `Style`, `variant_style`) stay in `rig.image_drift.builder_contract`.
The measured hexagon geometry lives in `hex_shapes`.

The layer order below describes the hexagon mark in `rhombille_builder.py`,
which draws on `prepare_document` here:

Layer order:

1. Dark slate card, then a solid dark hexagon base under the tiling.
2. Rhombille 60/120-degree tiling (the chosen house look), clipped to the
   hexagon, with circuit traces running parallel to the seam directions.
3. Glowing hexagon frame line with an outward socket notch centred on the
   frame node.
4. Circuit network stroked directly with the global gradient -- no SVG
   `<mask>`, so Cairo/librsvg-based rasterisers match browsers.
5. Developer emblems drawn as explicit stroke geometry rather than `<text>`,
   so output never depends on a font being installed.

A `herringbone` tiling is also available for candidate comparison.
"""

from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path

import svgwrite
from hex_shapes import Block, Box, Circuit, Node, Rect
from svgwrite.container import Group

from rig.image_drift.builder_contract import FeatureRegion, Style, variant_style
from rig.image_drift.hex_geometry import Point
from rig.image_drift.variants import StyleOverrides, Variant, VariantRegistry

# ---------------------------------------------------------------------------
# Measured canvas constants
# ---------------------------------------------------------------------------

CANVAS = 500

# Radius of the commit dot the socket ring encircles.
FRAME_NODE_RADIUS = 6.5

# Width of the dark cut separating every commit dot and trace from the tiling.
# One constant so the edging reads as uniform rather than incidental.
DOT_NOTCH_WIDTH = 3.0

# The socket dot sits inside its ring, so it gets a tighter cut -- at the shared
# width its halo would touch the ring and the two would read as one blob.
SOCKET_HALO_WIDTH = 2.0

# Card inset. Equal on all four sides so the card is square and its centre
# coincides with the hexagon's; it was 436x430 at (32, 33), putting the artwork
# 2px above centre and breaking any square crop.
CARD_INSET = 32.0

SEAM_COLOUR = "#160d07"
CARD_FILL = "#1d222d"
HEX_BASE_FILL = "#121620"
BEVEL_COLOUR = "#1e140c"

DEFAULT_VARIANT = "columns"

LOGO_TITLE = "Parquet.SourceGenerator"
LOGO_DESC = (
    "Five interlocking columns in a cyan-to-violet gradient, their joints "
    "staggered like a parquet bond, with an emitter dot above the centre column."
)

# svgwrite hard-codes these on the root element; none is used by this document.
_UNUSED_ROOT_ATTRIBUTES = (
    ' xmlns:ev="http://www.w3.org/2001/xml-events"',
    ' xmlns:xlink="http://www.w3.org/1999/xlink"',
    ' baseProfile="full"',
)


def strip_unused_root_attributes(path: Path) -> None:
    """Remove namespace declarations svgwrite emits but this document never uses.

    Applied to the root tag only, as an exact-string removal, so it cannot touch
    document content.
    """
    text = path.read_text(encoding="utf-8")
    # Locate the <svg ...> tag itself; partitioning on the first ">" would land
    # on the XML declaration instead.
    start = text.find("<svg")
    end = text.find(">", start)
    if start < 0 or end < 0:
        return
    root = text[start:end]
    for attribute in _UNUSED_ROOT_ATTRIBUTES:
        root = root.replace(attribute, "")
    path.write_text(text[:start] + root + text[end:], encoding="utf-8")


# Boxes measured off icon.jpg normalised to 500x500, shared between reference
# and target so the preview matrix compares like with like.
FEATURE_REGIONS: list[FeatureRegion] = [
    FeatureRegion(key="socket_void", label="Socket Void & Island Dot", x0=208, y0=110, x1=292, y1=210),
    FeatureRegion(key="bond_seam", label="Staggered Bond Slots", x0=128, y0=200, x1=372, y1=300),
    FeatureRegion(key="left_column", label="Left Column", x0=120, y0=112, x1=222, y1=362),
    FeatureRegion(key="footer_bar", label="Footer Bar", x0=118, y0=352, x1=382, y1=394),
]


# ---------------------------------------------------------------------------
# Wood palettes
# ---------------------------------------------------------------------------


@dataclass(slots=True, frozen=True)
class GradientStops:
    start: str
    end: str


@dataclass(slots=True, frozen=True)
class Tone:
    """One wood palette: a gradient per rhombus face."""

    light: GradientStops
    mid: GradientStops
    dark: GradientStops

    @property
    def faces(self) -> tuple[GradientStops, ...]:
        """Indexed by a face's tone: 0 light, 1 mid, 2 dark."""
        return (self.light, self.mid, self.dark)


TONES: dict[str, Tone] = {
    "warm": Tone(
        GradientStops("#f2ddc0", "#e3c39a"), GradientStops("#dfb98c", "#c99f6c"), GradientStops("#c49a68", "#a87f4c")
    ),
    "amber": Tone(
        GradientStops("#e2af7b", "#ce9b66"), GradientStops("#c9955f", "#b4763f"), GradientStops("#a97741", "#8b5a28")
    ),
}


# ---------------------------------------------------------------------------
# Candidate Variant Definitions for Wave Generation
# ---------------------------------------------------------------------------
#
# Variants vary *structural* parameters -- tiling family, cell size, wood tone
# -- because those move enough pixels for the drift metric to discriminate.
# The earlier set differed only in node radius and feeder routing, a few
# hundred pixels out of 250,000, so ranking them was reading noise.


VARIANTS: VariantRegistry = {
    "favicon": Variant(
        name="Favicon",
        description=(
            "Small-size cut: three fat columns and a heavier emitter, so the mark still "
            "separates at 16-32px where five columns merge."
        ),
        mark="columns",
        tiling="rhombille",
        cell=52.3,
        tone="warm",
        style=StyleOverrides(column_gap=30.0, column_segment=86.0, trace_width=9.0, glow_wide=4.0, glow_tight=1.5),
    ),
    "columns": Variant(
        name="Direction A: Interlocking Columns",
        description=(
            "Columnar - the Parquet property - with a progressive parquet bond and an emitter"
            " dot above the centre column."
        ),
        mark="columns",
        tiling="rhombille",
        cell=52.3,
        tone="warm",
        style=StyleOverrides(glow_wide=7.0, glow_tight=2.0),
    ),
}


# ---------------------------------------------------------------------------
# Column mark (Direction A): interlocking columns
# ---------------------------------------------------------------------------
#
# Parquet's one unambiguous property is that it is *columnar*. Equal-height
# columns whose segment joints alternate read as columns and as an interlocking
# floor at the same time -- the format and the pun -- without the bar-chart
# connotation that varying heights would add.

COLUMN_BOX = Box(128.0, 120.0, 372.0, 352.0)
COLUMN_COUNT = 3
COLUMN_GAP = 26.0
COLUMN_SEGMENT = 46.0
COLUMN_SEGMENT_GAP = 14.0

# Slot cutouts: the bond joints are voids inside each column rather than gaps
# between stacked blocks. Negative space survives downscaling better than added
# elements do, and it keeps the columns reading as single solid objects.
COLUMN_CORNER = 7.0

# Commit-graph geometry. Tips sit this far below the top of the columns, so each
# node reads as terminating inside its own section rather than escaping it.
COLUMN_TIP_INSET = 26.0
COLUMN_TIP_RADIUS = 9.0
COLUMN_NODE_RADIUS = 7.5

# How far above the footer the first fork sits, so the trunk has a visible run
# before it branches.
COLUMN_ROOT_RUN = 34.0

# The graph is cased in the card colour before the bright trace is drawn, so it
# reads as carved through the parquet field rather than laid on top of it.
COLUMN_CHANNEL_PAD = 5.0

# A footer bar ties the columns together. Parquet files end in a footer holding
# the schema and row-group metadata, so the bar is structural, not decorative.
COLUMN_FOOTER_GAP = 12.0
COLUMN_FOOTER_HEIGHT = 14.0


def column_lanes(
    box: Box = COLUMN_BOX,
    count: int = COLUMN_COUNT,
    gap: float = COLUMN_GAP,
) -> list[float]:
    """Centre x of each column: the lanes the commit graph runs along."""
    width = (box.x1 - box.x0 - (count - 1) * gap) / count
    return [box.x0 + index * (width + gap) + width / 2.0 for index in range(count)]


def parquet_blocks(
    box: Box = COLUMN_BOX,
    count: int = COLUMN_COUNT,
    gap: float = COLUMN_GAP,
    segment: float = COLUMN_SEGMENT,
    segment_gap: float = COLUMN_SEGMENT_GAP,
) -> list[Block]:
    """Parquet sections whose negative space inverts between neighbours.

    Adjacent columns are offset by half a pitch, so where one column has a block
    its neighbour has a void and vice versa. That complementary alternation is
    what makes the field read as a laid floor rather than as a set of bars.

    Returns:
        Blocks clipped to the box, each `index` the column it belongs to.
    """
    width = (box.x1 - box.x0 - (count - 1) * gap) / count
    pitch = segment + segment_gap

    blocks: list[Block] = []
    for index in range(count):
        x = box.x0 + index * (width + gap)
        edge = box.y0 - (pitch / 2.0 if index % 2 else 0.0)
        while edge < box.y1:
            top = max(edge, box.y0)
            bottom = min(edge + segment, box.y1)
            if bottom - top > 4.0:
                blocks.append(Block(x, top, width, bottom - top, index))
            edge += pitch
    return blocks


def column_footer(
    box: Box = COLUMN_BOX,
) -> Rect:
    """The footer bar beneath the columns.

    Parquet files end in a footer holding the schema and row-group metadata, so
    the bar is structural. The commit trunk rises out of it.
    """
    return Rect(box.x0, box.y1 + COLUMN_FOOTER_GAP, box.x1 - box.x0, COLUMN_FOOTER_HEIGHT)


def fork_graph(
    box: Box = COLUMN_BOX,
    count: int = COLUMN_COUNT,
    gap: float = COLUMN_GAP,
) -> Circuit:
    """A commit graph forking out of the bottom-left, one tip per lane.

    The trunk rises from the footer in the leftmost lane. Branches *cascade*:
    each one leaves the previous branch at the point where that branch turned
    vertical, climbs one lane to the right at 45 degrees, then runs up to its own
    tip. Every lane therefore ends in its own node, and the whole structure
    descends from a single root at bottom left.

    Cascading matters. Forking every branch off a fixed height in its
    left-hand neighbour puts the later forks at a point where that neighbour has
    no line yet, and the branch appears to start in mid-air.

    """
    lanes = column_lanes(box, count, gap)
    footer_top = box.y1 + COLUMN_FOOTER_GAP
    top = box.y0 + COLUMN_TIP_INSET

    # Every diagonal rises by one lane spacing, so the first fork has to sit high
    # enough above the tips for the whole cascade to fit.
    run = lanes[1] - lanes[0] if len(lanes) > 1 else 0.0
    first_fork_y = min(footer_top - COLUMN_ROOT_RUN, top + run * (len(lanes) - 1))

    root = Point(lanes[0], footer_top)
    paths: list[tuple[Point, ...]] = [(root, Point(lanes[0], top))]
    nodes = [Node(root, COLUMN_NODE_RADIUS), Node(Point(lanes[0], top), COLUMN_TIP_RADIUS)]

    fork = Point(lanes[0], first_fork_y)
    for index in range(1, len(lanes)):
        turn = Point(lanes[index], fork.y - run)
        branch = [fork, turn]
        if turn.y - top > 1.0:
            branch.append(Point(lanes[index], top))
        paths.append(tuple(branch))
        nodes.append(Node(fork, COLUMN_NODE_RADIUS))
        nodes.append(Node(Point(lanes[index], top), COLUMN_TIP_RADIUS))
        # The next branch leaves from this one's turn, where it is already drawn.
        fork = turn
    return Circuit(tuple(paths), tuple(nodes))


def prepare_document(
    output_path: Path,
    width: int,
    height: int,
    style: Style,
    tone: Tone,
    desc: str = LOGO_DESC,
) -> svgwrite.Drawing:
    """Create the document, its gradients, glow filter and card.

    Shared by every mark so the accessible root element, the global
    gradients and the glow filter are defined once rather than per builder.
    """
    output_path.parent.mkdir(parents=True, exist_ok=True)
    # debug=False disables svgwrite's SVG 1.1 attribute whitelist, which rejects
    # the ARIA attributes a logo needs.
    dwg = svgwrite.Drawing(
        filename=str(output_path),
        size=(f"{width}px", f"{height}px"),
        viewBox=f"0 0 {width} {height}",
        debug=False,
    )
    dwg.set_desc(title=LOGO_TITLE, desc=desc)
    dwg.attribs["role"] = "img"
    dwg.attribs["aria-label"] = LOGO_TITLE
    # No fixed pixel size: the viewBox carries the aspect ratio, and callers size
    # it with CSS or an img width. Hard-coded width/height fights both.
    dwg.attribs.pop("width", None)
    dwg.attribs.pop("height", None)
    defs = dwg.defs

    for index, stops in enumerate(tone.faces):
        grad = dwg.linearGradient(id=f"wood-face-{index}", x1="0%", y1="0%", x2="100%", y2="100%")
        grad.add_stop_color(offset="0%", color=stops.start)
        grad.add_stop_color(offset="100%", color=stops.end)
        defs.add(grad)

    # userSpaceOnUse makes this one gradient spanning the whole canvas, as in the
    # reference. With the default objectBoundingBox units every trace would get
    # its own private cyan-to-magenta ramp, and a vertical trace -- zero
    # bounding-box width -- degenerates to a flat dark stroke.
    circuit_grad = dwg.linearGradient(id="circuit-global-grad", start=(0, 0), end=(CANVAS, CANVAS))
    # Set directly: svgwrite drops `gradientUnits` when passed as a constructor
    # keyword.
    circuit_grad["gradientUnits"] = "userSpaceOnUse"
    # The reference holds pure cyan flat to roughly 45% of the diagonal, swings
    # ~72 degrees of hue within the next 13%, then plateaus in violet. Evenly
    # spaced stops produce a long muddy blue blend instead of that snap.
    for offset, colour in (
        ("0%", "#00f2fe"),
        ("45%", "#00c4e8"),
        ("56%", "#6d28d9"),
        ("78%", "#7209b7"),
        ("100%", "#b5179e"),
    ):
        circuit_grad.add_stop_color(offset=offset, color=colour)
    defs.add(circuit_grad)

    # Same sweep, lighter dark end. The circuit gradient's violet and magenta are
    # dark enough that large filled shapes vanish under a 1-bit threshold, which
    # is exactly what a one-colour reproduction does.
    mark_grad = dwg.linearGradient(id="mark-grad", start=(0, 0), end=(CANVAS, CANVAS))
    mark_grad["gradientUnits"] = "userSpaceOnUse"
    for offset, colour in (
        ("0%", "#22e6f7"),
        ("45%", "#3ec8ec"),
        ("60%", "#8b6cf0"),
        ("100%", "#c46ce0"),
    ):
        mark_grad.add_stop_color(offset=offset, color=colour)
    defs.add(mark_grad)

    # Glow degrades gracefully: a rasteriser that drops the filter still draws
    # the underlying stroked geometry.
    glow = dwg.filter(id="glow-circuit", x="-30%", y="-30%", width="160%", height="160%")
    # Two passes: a wide soft bloom plus a tight one, then the crisp source on
    # top. A single mid blur washes a 3px line out instead of making it glow.
    glow.feGaussianBlur(stdDeviation=f"{style.glow_wide:g}", result="wide")
    glow.feGaussianBlur("SourceGraphic", stdDeviation=f"{style.glow_tight:g}", result="tight")
    glow.feMerge(layernames=["wide", "tight", "SourceGraphic"])
    defs.add(glow)

    # --- Layer 0: card + hexagon base -------------------------------------
    dwg.add(
        dwg.rect(
            insert=(CARD_INSET, CARD_INSET),
            size=(CANVAS - 2 * CARD_INSET, CANVAS - 2 * CARD_INSET),
            rx=62,
            fill=CARD_FILL,
            stroke="#2b3140",
            stroke_width=3,
        )
    )
    return dwg


def build_svg_logo(
    output_path: Path,
    width: int = CANVAS,
    height: int = CANVAS,
    variant: str = DEFAULT_VARIANT,
) -> None:
    """Build the interlocking-columns logo for `variant` at `output_path`.

    The hexagon-and-circuit mark this project started from lives in
    `rhombille_builder.py`; drive it with the CLI's --builder-module. Both share
    `prepare_document` in this module and the shapes in `hex_shapes`, so a
    fix lands once for both.
    """
    config = VARIANTS.get(variant, VARIANTS[DEFAULT_VARIANT])
    style = variant_style(config)
    tone = TONES[config.tone or "warm"]

    dwg = prepare_document(output_path, width, height, style, tone)
    _add_column_mark(dwg, style)
    dwg.save(pretty=True)
    strip_unused_root_attributes(output_path)


def _add_column_mark(dwg: svgwrite.Drawing, style: Style) -> None:
    """Parquet sections with inverted voids, forked by a commit graph.

    Two ideas from earlier rounds, combined: the field is columns whose negative
    space alternates between neighbours, and the figure is the forking commit
    history from the original hexagon mark -- a trunk rising out of the footer at
    bottom left, branching once per lane, every lane ending in its own node.

    The graph is cased in the card colour before the bright trace is drawn, so it
    reads as carved through the field. That is the same backing trick the hexagon
    mark used for its circuit.
    """
    count = style.column_count
    box = COLUMN_BOX
    gap = style.column_gap
    trace = style.trace_width

    group = Group(filter="url(#glow-circuit)")

    field = Group()
    for block in parquet_blocks(box, count, gap, style.column_segment, COLUMN_SEGMENT_GAP):
        field.add(
            dwg.rect(
                insert=(round(block.x, 2), round(block.y, 2)),
                size=(round(block.width, 2), round(block.height, 2)),
                rx=COLUMN_CORNER,
                ry=COLUMN_CORNER,
                fill="url(#mark-grad)",
                fill_opacity=0.9,
            )
        )
    if style.show_footer:
        footer = column_footer(box)
        field.add(
            dwg.rect(
                insert=(round(footer.x, 2), round(footer.y, 2)),
                size=(round(footer.width, 2), round(footer.height, 2)),
                rx=COLUMN_FOOTER_HEIGHT / 2.0,
                ry=COLUMN_FOOTER_HEIGHT / 2.0,
                fill="url(#mark-grad)",
            )
        )
    group.add(field)

    graph = fork_graph(box, count, gap)
    for colour, extra in ((CARD_FILL, COLUMN_CHANNEL_PAD), ("url(#mark-grad)", 0.0)):
        layer = Group(
            stroke=colour,
            stroke_width=trace + 2 * extra,
            stroke_linecap="round",
            stroke_linejoin="round",
            fill="none",
        )
        for path in graph.paths:
            layer.add(dwg.polyline(points=[(round(p.x, 2), round(p.y, 2)) for p in path]))
        for node in graph.nodes:
            layer.add(
                dwg.circle(
                    center=(round(node.centre.x, 2), round(node.centre.y, 2)),
                    r=node.radius + extra,
                    fill=colour,
                    stroke="none",
                )
            )
        group.add(layer)

    dwg.add(group)
