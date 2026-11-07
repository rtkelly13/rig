"""Hexagon geometry shared by the unscored logo explorers.

Derived from measurements taken off the reference normalised to 500x500:

* stroke centreline fitted per edge (cool-hued pixels only, rms < 0.5px) gives a
  perpendicular of ~161 and so `FRAME_RADIUS` ~186.2. Measuring the *glow* instead
  reads ~188, because the bloom extends outward past the stroke.
* wood mask spanning x 100..399, y 80..423 -> `WOOD_RADIUS` ~173
* frame node measured at (330, 114), projected onto the frame edge so it sits
  exactly on the line rather than floating beside it
"""

from __future__ import annotations

import math
from collections.abc import Sequence
from dataclasses import dataclass

from rig.image_drift.hex_geometry import HEX_CENTER, Point, hexagon_vertices


def distance(a: Point, b: Point) -> float:
    return math.dist((a.x, a.y), (b.x, b.y))


@dataclass(slots=True, frozen=True)
class Projection:
    """Where a point lands on a segment, and how far along the segment that is."""

    point: Point
    distance: float


@dataclass(slots=True, frozen=True)
class HalfPlane:
    """One clip-hexagon edge: its outward (unnormalised) normal and a point on it."""

    normal: Point
    anchor: Point


@dataclass(slots=True, frozen=True)
class Face:
    """A tiling polygon and the wood tone (0 light, 1 mid, 2 dark) it is filled with."""

    points: tuple[Point, ...]
    tone: int


@dataclass(slots=True, frozen=True)
class Block:
    """An axis-aligned rectangle with the tone or column it belongs to."""

    x: float
    y: float
    width: float
    height: float
    index: int


@dataclass(slots=True, frozen=True)
class Box:
    """An axis-aligned box by its corners."""

    x0: float
    y0: float
    x1: float
    y1: float


@dataclass(slots=True, frozen=True)
class Rect:
    """An axis-aligned rectangle by its top-left corner and size."""

    x: float
    y: float
    width: float
    height: float


@dataclass(slots=True, frozen=True)
class Node:
    """A commit dot."""

    centre: Point
    radius: float


@dataclass(slots=True, frozen=True)
class Circuit:
    """Polyline traces and the dots on them."""

    paths: tuple[tuple[Point, ...], ...]
    nodes: tuple[Node, ...]


FRAME_RADIUS = 186.2
WOOD_RADIUS = 173.0

# Radius the original circuit coordinates below were authored against. Paths
# are scaled by WOOD_RADIUS / LEGACY_WOOD_RADIUS so they stay locked to the
# tiling when the measured radius changes.
LEGACY_WOOD_RADIUS = 182.0

# Where the reference's upper-right frame node reads on the 500x500 grid. The
# drawn node is this point projected onto the frame edge.
MEASURED_FRAME_NODE = Point(330.0, 114.0)


def _project_onto_segment(point: Point, start: Point, end: Point) -> Projection:
    """Project `point` onto the segment `start`->`end`."""
    dx, dy = end.x - start.x, end.y - start.y
    length = math.hypot(dx, dy)
    unit = Point(dx / length, dy / length)
    t = (point.x - start.x) * unit.x + (point.y - start.y) * unit.y
    t = max(0.0, min(length, t))
    return Projection(start.offset(unit, t), t)


def frame_node_position(radius: float = FRAME_RADIUS, measured: Point = MEASURED_FRAME_NODE) -> Point:
    """The socket node, guaranteed to lie on the frame's upper-right edge.

    The previous constant (329, 103) sat off the edge line, which is why the
    notch rendered as a lump rather than a socket seated on the frame.
    """
    verts = hexagon_vertices(radius)
    return _project_onto_segment(measured, verts[0], verts[1]).point.rounded(3)


FRAME_NODE = frame_node_position()


def scaled_circuit_point(point: Point) -> Point:
    """Rescale a legacy circuit coordinate to the measured wood radius."""
    factor = WOOD_RADIUS / LEGACY_WOOD_RADIUS
    return Point(
        round(HEX_CENTER.x + (point.x - HEX_CENTER.x) * factor, 3),
        round(HEX_CENTER.y + (point.y - HEX_CENTER.y) * factor, 3),
    )


def arc_center(start: Point, end: Point, radius: float, large_arc: int, sweep: int) -> Point:
    """Centre of the circle an SVG elliptical-arc command actually draws.

    Used to pick the sweep flag by construction instead of by trial: the ring
    is only correct when its centre lands on the commit dot.
    """
    mid = Point((start.x + end.x) / 2.0, (start.y + end.y) / 2.0)
    dx, dy = end.x - start.x, end.y - start.y
    chord = math.hypot(dx, dy)
    height = math.sqrt(max(0.0, radius * radius - (chord / 2.0) ** 2))
    # Perpendicular to the chord, matching the sign convention in SVG spec
    # F.6.5: the centre offset is s * h * (-dy, dx) / chord.
    perp = Point(-dy / chord, dx / chord)
    sign = 1.0 if large_arc != sweep else -1.0
    return Point(mid.x + sign * height * perp.x, mid.y + sign * height * perp.y)


def line_circle_intersections(origin: Point, direction: Point, centre: Point, radius: float) -> list[float]:
    """Parameters along `origin + t * direction` where the line meets the circle.

    Returned in ascending order; empty when the line misses. `direction` need not
    be normalised, but the returned parameters are in its units.
    """
    fx, fy = origin.x - centre.x, origin.y - centre.y
    dx, dy = direction.x, direction.y
    a = dx * dx + dy * dy
    b = 2.0 * (fx * dx + fy * dy)
    c = fx * fx + fy * fy - radius * radius
    discriminant = b * b - 4.0 * a * c
    if discriminant < 0.0:
        return []
    root = math.sqrt(discriminant)
    return sorted(((-b - root) / (2.0 * a), (-b + root) / (2.0 * a)))


def _edge_direction(radius: float) -> Point:
    verts = hexagon_vertices(radius)
    top, upper_right = verts[0], verts[1]
    length = distance(top, upper_right)
    return Point((upper_right.x - top.x) / length, (upper_right.y - top.y) / length)


def _path(points: Sequence[Point]) -> str:
    return "M " + " L ".join(f"{p.x:g} {p.y:g}" for p in points)


def frame_outline_path(radius: float, node: Point, ring_radius: float) -> str:
    """Hexagon outline that stops exactly where it meets the socket ring.

    Built in the order the geometry actually depends on: the dot is placed first,
    the ring is a circle of `ring_radius` about it, and each straight run is then
    terminated at its true intersection with that circle -- solved, not assumed.

    Doing it by intersection rather than by stepping `ring_radius` along the edge
    matters whenever the dot is not perfectly on the line: the shortcut would put
    the two ends at different distances from the ring and the join would sit
    visibly off-centre. Every point still lies on a hexagon edge, so the outline
    cannot bow.
    """
    verts = hexagon_vertices(radius)
    top = verts[0]
    direction = _edge_direction(radius)

    hits = line_circle_intersections(top, direction, node, ring_radius)
    if len(hits) < 2:
        # Ring does not reach the edge: draw the hexagon unbroken.
        return _path([*verts, top])

    before = top.offset(direction, hits[0])
    after = top.offset(direction, hits[-1])

    # One open subpath: resume after the gap, run the whole way round, stop
    # just before it.
    return _path([after, *verts[1:], top, before])


def dome_path(node: Point, ring_radius: float, line_origin: Point, line_direction: Point, outward: Point) -> str:
    """Arch over a dot, stopping exactly where it meets a straight line.

    The dot sits on the line, so the line crosses the circle at two
    diametrically opposite points. The notch is the half on the `outward` side,
    terminating at both crossings -- it must not continue round the other side,
    which would close the shape off and leave any feeder trace crossing its own
    ring.

    Emitted as two quarter arcs via the apex rather than one semicircle: a
    180-degree arc has radius exactly equal to half its chord, the degenerate
    case renderers disagree about. Two quarters have chord `r * sqrt(2)`, safely
    inside the diameter, and describe the identical curve.
    """
    hits = line_circle_intersections(line_origin, line_direction, node, ring_radius)
    if len(hits) < 2:
        return ""
    entry = line_origin.offset(line_direction, hits[0])
    leave = line_origin.offset(line_direction, hits[-1])
    apex = node.offset(outward, ring_radius)

    def quarter(start: Point, end: Point) -> str:
        sweep = next(
            candidate
            for candidate in (1, 0)
            if distance(arc_center(start, end, ring_radius, 0, candidate), node) < 0.01
        )
        return f"A {ring_radius:g} {ring_radius:g} 0 0 {sweep} {end.x:g} {end.y:g}"

    return f"M {entry.x:g} {entry.y:g} {quarter(entry, apex)} {quarter(apex, leave)}"


def socket_dome_path(radius: float, node: Point, ring_radius: float) -> str:
    """The hexagon frame's socket dome: `dome_path` along the upper-right edge."""
    top = hexagon_vertices(radius)[0]
    direction = _edge_direction(radius)

    # Normal pointing away from the hexagon centre.
    outward = Point(-direction.y, direction.x)
    if outward.x * (node.x - HEX_CENTER.x) + outward.y * (node.y - HEX_CENTER.y) < 0:
        outward = Point(direction.y, -direction.x)
    return dome_path(node, ring_radius, top, direction, outward)


# ---------------------------------------------------------------------------
# Tiling generators
# ---------------------------------------------------------------------------


def hexagon_half_planes(radius: float) -> list[HalfPlane]:
    """Outward half-plane normals for the clip hexagon, one per edge."""
    verts = hexagon_vertices(radius)
    planes: list[HalfPlane] = []
    for index in range(6):
        a, b = verts[index], verts[(index + 1) % 6]
        nx, ny = -(b.y - a.y), b.x - a.x
        # Orient outward: the centre must land on the negative side.
        if nx * (HEX_CENTER.x - a.x) + ny * (HEX_CENTER.y - a.y) > 0:
            nx, ny = -nx, -ny
        planes.append(HalfPlane(Point(nx, ny), a))
    return planes


def touches_hexagon(points: Sequence[Point], planes: Sequence[HalfPlane], slack: float = 2.0) -> bool:
    """Whether a convex face might overlap the clip hexagon.

    Uses the hexagon's edge normals as separating axes. A face is discarded only
    when every one of its vertices sits outside a single edge, which can never
    be true of a face that overlaps -- so this errs toward keeping faces and can
    never change the rendered result. `slack` retains faces whose seam stroke
    reaches inside even though their fill does not.

    Culling matters: without it four fifths of the emitted geometry is invisible
    but still parsed by every consumer.
    """
    for plane in planes:
        normal, anchor = plane.normal, plane.anchor
        scale = math.hypot(normal.x, normal.y)
        if all((normal.x * (p.x - anchor.x) + normal.y * (p.y - anchor.y)) / scale > slack for p in points):
            return False
    return True


def rhombille_faces(cell: float, radius: float) -> list[Face]:
    """Rhombille 60/120 tiling: three rhombus faces per hexagonal cell.

    Each face's tone 0/1/2 selects the light/mid/dark wood gradient.
    """
    rings = int(math.ceil(radius / cell)) + 1

    def cell_vertices(center: Point) -> list[Point]:
        return [
            Point(
                center.x + cell * math.cos(math.radians(60 * i - 30)),
                center.y + cell * math.sin(math.radians(60 * i - 30)),
            )
            for i in range(6)
        ]

    planes = hexagon_half_planes(radius)
    faces: list[Face] = []
    for q in range(-rings, rings + 1):
        lo = max(-rings, -q - rings)
        hi = min(rings, -q + rings)
        for r in range(lo, hi + 1):
            center = Point(HEX_CENTER.x + cell * math.sqrt(3) * (q + r / 2.0), HEX_CENTER.y + cell * 1.5 * r)
            verts = cell_vertices(center)
            for tone, first in enumerate((0, 2, 4)):
                points = (center, verts[first], verts[first + 1], verts[(first + 2) % 6])
                if touches_hexagon(points, planes):
                    faces.append(Face(points, tone))
    return faces


def herringbone_planks(cell: float, radius: float) -> list[Block]:
    """45-degree herringbone plank layout, as 2:1 planks on a `cell` lattice.

    Uses the classic domino colouring: cell (i, j) with (i - j) mod 4 == 0
    starts a horizontal plank and == 3 starts a vertical one. Each lattice
    cell is then covered exactly once, so planks tile with no gaps or overlap.
    Callers rotate the result 45 degrees to get the diagonal weave.

    Returns:
        Planks in unrotated pattern space, each `index` the tone it is filled with.
    """
    # Cover the circumscribed circle, enlarged so the 45-degree rotation
    # cannot expose a bare corner.
    reach = radius * math.sqrt(2) + 2 * cell
    steps = int(math.ceil(reach / cell))

    planes = hexagon_half_planes(radius)
    planks: list[Block] = []
    for i in range(-steps, steps + 1):
        for j in range(-steps, steps + 1):
            phase = (i - j) % 4
            if phase not in (0, 3):
                continue
            x = HEX_CENTER.x + i * cell
            y = HEX_CENTER.y + j * cell
            width, height, tone = (2 * cell, cell, 0) if phase == 0 else (cell, 2 * cell, 2)
            corners = [
                Point(x, y),
                Point(x + width, y),
                Point(x + width, y + height),
                Point(x, y + height),
            ]
            # Planks are laid out then rotated 45 degrees, so test the rotated
            # corners against the clip.
            rotated = [_rotate_about_centre(corner, 45.0) for corner in corners]
            if touches_hexagon(rotated, planes):
                planks.append(Block(x, y, width, height, tone))
    return planks


def _rotate_about_centre(point: Point, degrees: float) -> Point:
    """Rotate a point about the hexagon centre, matching the wood group transform."""
    angle = math.radians(degrees)
    dx, dy = point.x - HEX_CENTER.x, point.y - HEX_CENTER.y
    return Point(
        HEX_CENTER.x + dx * math.cos(angle) - dy * math.sin(angle),
        HEX_CENTER.y + dx * math.sin(angle) + dy * math.cos(angle),
    )
