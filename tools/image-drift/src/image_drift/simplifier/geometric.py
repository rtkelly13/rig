"""Geometric Decimation Engine: Shapely Polyline RDP & Bézier Knot Simplification."""

from __future__ import annotations

import math
from collections.abc import Iterable, Sequence

from shapely.errors import ShapelyError
from shapely.geometry import LineString, MultiPolygon, Polygon

from image_drift.hex_geometry import Point
from image_drift.simplifier.ast_parser import (
    PathCommand,
    SubPath,
    SVGPathTokenizer,
    serialize_path_d,
)


def point_to_segment_distance(px: float, py: float, x1: float, y1: float, x2: float, y2: float) -> float:
    """Perpendicular distance from point (px, py) to line segment (x1, y1)-(x2, y2)."""
    dx = x2 - x1
    dy = y2 - y1
    l2 = dx * dx + dy * dy
    if l2 == 0:
        return math.hypot(px - x1, py - y1)

    t = max(0.0, min(1.0, ((px - x1) * dx + (py - y1) * dy) / l2))
    proj_x = x1 + t * dx
    proj_y = y1 + t * dy
    return math.hypot(px - proj_x, py - proj_y)


def is_cubic_near_linear(
    p0: Point,
    p1: Point,
    p2: Point,
    p3: Point,
    tolerance: float,
) -> bool:
    """Check if cubic Bézier control points p1 and p2 lie within tolerance of chord p0->p3."""
    d1 = point_to_segment_distance(p1.x, p1.y, p0.x, p0.y, p3.x, p3.y)
    d2 = point_to_segment_distance(p2.x, p2.y, p0.x, p0.y, p3.x, p3.y)
    return d1 <= tolerance and d2 <= tolerance


def _advance(cmd: PathCommand, pos: Point, start: Point) -> Point:
    """The absolute current point after *cmd*, from *pos* and the subpath *start* (for Z)."""
    c = cmd.command
    args = cmd.args
    relative = c.islower()
    if c in ("Z", "z"):
        return start
    if c in ("H", "h") and args:
        return Point(args[0] + (pos.x if relative else 0.0), pos.y)
    if c in ("V", "v") and args:
        return Point(pos.x, args[0] + (pos.y if relative else 0.0))
    if len(args) >= 2:
        x, y = args[-2], args[-1]
        return Point(pos.x + x, pos.y + y) if relative else Point(x, y)
    return pos


def end_point(commands: Sequence[PathCommand], origin: Point) -> Point:
    """The absolute current point after *commands*, starting at *origin*."""
    pos = origin
    start = origin
    for cmd in commands:
        pos = _advance(cmd, pos, start)
        if cmd.command in ("M", "m"):
            start = pos
    return pos


def simplify_subpath(subpath: SubPath, tolerance: float = 0.5, origin: Point | None = None) -> list[PathCommand]:
    """Simplify a single contiguous SubPath according to its geometric classification.

    *origin* is the absolute current point before the subpath, which a leading relative ``m``
    and every later relative command are measured from.
    """
    if not subpath.commands:
        return []
    origin = origin or Point(0.0, 0.0)

    # Check if subpath is purely linear (M, L, H, V, Z)
    is_linear = all(cmd.command.upper() in ("M", "L", "H", "V", "Z") for cmd in subpath.commands)

    if is_linear:
        # Extract 2D coordinate polyline
        coords: list[Sequence[float]] = []
        curr_x = origin.x
        curr_y = origin.y

        for cmd in subpath.commands:
            c = cmd.command
            if c in ("M", "L") and len(cmd.args) >= 2:
                curr_x, curr_y = cmd.args[0], cmd.args[1]
                coords.append((curr_x, curr_y))
            elif c == "H" and len(cmd.args) >= 1:
                curr_x = cmd.args[0]
                coords.append((curr_x, curr_y))
            elif c == "h" and len(cmd.args) >= 1:
                curr_x += cmd.args[0]
                coords.append((curr_x, curr_y))
            elif c == "V" and len(cmd.args) >= 1:
                curr_y = cmd.args[0]
                coords.append((curr_x, curr_y))
            elif c == "v" and len(cmd.args) >= 1:
                curr_y += cmd.args[0]
                coords.append((curr_x, curr_y))
            elif c in ("m", "l") and len(cmd.args) >= 2:
                curr_x += cmd.args[0]
                curr_y += cmd.args[1]
                coords.append((curr_x, curr_y))

        if len(coords) < 3:
            return subpath.commands

        if subpath.is_closed and len(coords) >= 3:
            try:
                poly = Polygon(coords)
                if not poly.is_valid:
                    poly = poly.buffer(0)
                simplified = poly.simplify(tolerance, preserve_topology=True)
            except (ValueError, ShapelyError):
                return subpath.commands

            if simplified.is_empty:
                return subpath.commands

            poly_cmds: list[PathCommand] = []
            if isinstance(simplified, Polygon):
                poly_cmds.extend(_polygon_ring_to_commands(simplified.exterior.coords))
                for interior in simplified.interiors:
                    poly_cmds.extend(_polygon_ring_to_commands(interior.coords))
            elif isinstance(simplified, MultiPolygon):
                for sub_poly in simplified.geoms:
                    poly_cmds.extend(_polygon_ring_to_commands(sub_poly.exterior.coords))
                    for interior in sub_poly.interiors:
                        poly_cmds.extend(_polygon_ring_to_commands(interior.coords))
            return poly_cmds
        else:
            # Open Polyline via Shapely LineString
            try:
                line = LineString(coords)
                simplified_line = line.simplify(tolerance, preserve_topology=True)
            except (ValueError, ShapelyError):
                return subpath.commands

            if simplified_line.is_empty:
                return subpath.commands

            line_coords = list(simplified_line.coords)
            if not line_coords:
                return subpath.commands

            line_cmds = [PathCommand("M", [line_coords[0][0], line_coords[0][1]])]
            for pt in line_coords[1:]:
                line_cmds.append(PathCommand("L", [pt[0], pt[1]]))
            return line_cmds

    # Path contains curves: apply curvature-aware knot decimation. Relative commands keep their
    # relative form, but every test is made on absolute points.
    out_cmds: list[PathCommand] = []
    curr_pos = origin
    start = origin

    for cmd in subpath.commands:
        c = cmd.command
        relative = c.islower()
        if c in ("C", "c") and len(cmd.args) >= 6:
            base = curr_pos if relative else Point(0.0, 0.0)
            p1 = Point(base.x + cmd.args[0], base.y + cmd.args[1])
            p2 = Point(base.x + cmd.args[2], base.y + cmd.args[3])
            p3 = Point(base.x + cmd.args[4], base.y + cmd.args[5])

            if is_cubic_near_linear(curr_pos, p1, p2, p3, tolerance):
                # Collapse flat curve to straight line
                out_cmds.append(PathCommand("l" if relative else "L", [cmd.args[4], cmd.args[5]]))
            else:
                out_cmds.append(cmd)
            curr_pos = p3
        elif c in ("L", "l") and len(cmd.args) >= 2:
            target_pt = _advance(cmd, curr_pos, start)
            # Drop zero-length segments
            if math.hypot(target_pt.x - curr_pos.x, target_pt.y - curr_pos.y) > tolerance * 0.1:
                out_cmds.append(cmd)
                curr_pos = target_pt
        else:
            out_cmds.append(cmd)
            curr_pos = _advance(cmd, curr_pos, start)
            if c in ("M", "m"):
                start = curr_pos

    return out_cmds


def _polygon_ring_to_commands(coords: Iterable[Sequence[float]]) -> list[PathCommand]:
    """Convert a Shapely linear ring into M ... L ... Z PathCommands."""
    pts = list(coords)
    if not pts:
        return []

    # Omit duplicate closing point from Shapely ring
    if len(pts) > 1 and pts[0] == pts[-1]:
        pts = pts[:-1]

    if not pts:
        return []

    cmds: list[PathCommand] = [PathCommand("M", [pts[0][0], pts[0][1]])]
    for p in pts[1:]:
        cmds.append(PathCommand("L", [p[0], p[1]]))
    cmds.append(PathCommand("Z", []))
    return cmds


def simplify_path_geometry(d_string: str, tolerance: float = 0.5) -> str:
    """Simplify SVG path geometry with topology preservation and curve protection."""
    subpaths = SVGPathTokenizer.extract_subpaths(d_string)
    if not subpaths:
        return d_string

    out_commands: list[PathCommand] = []
    pos = Point(0.0, 0.0)
    for sp in subpaths:
        out_commands.extend(simplify_subpath(sp, tolerance=tolerance, origin=pos))
        pos = end_point(sp.commands, pos)

    return serialize_path_d(out_commands)
