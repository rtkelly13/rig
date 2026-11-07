"""Detect and repair jagged edges in an axis-constrained vector mark.

An isometric mark is built on a small set of canonical axes -- for the
rtkelly-labs loops, 30/90/150 degrees. Any facet edge should therefore lie on
one of those axes. A raster trace does not know that: it follows anti-aliased
pixel boundaries, so it emits edges scattered a degree or two either side of
the true axis, plus short jogs where the tracer stepped around a pixel corner.

Measured on the committed rtkelly-labs master, only 81.2% of total edge length
sits within 3 degrees of an isometric axis -- which is close to the 81.6% edge
correlation the drift diagnostics independently report against the reference
raster. Those are two views of the same defect.

This module makes that defect measurable (`analyze_edges`) and repairable
(`regularize_svg`). Repair snaps each near-axis edge to its exact axis and then
rebuilds vertices as the intersections of consecutive snapped lines, so
corners stay sharp instead of being rounded off the way a pure
distance-threshold simplifier would leave them.
"""

from __future__ import annotations

import math
import re
from dataclasses import dataclass, field
from typing import ClassVar, Literal

from rig.image_drift.hex_geometry import Point

DefectKind = Literal["micro_segment", "off_axis", "collinear", "staircase"]

ISOMETRIC_AXES: tuple[float, ...] = (30.0, 90.0, 150.0)
"""The three isometric projection axes: prism edges and extrusion walls."""

HEXAGONAL_AXES: tuple[float, ...] = (0.0, 30.0, 60.0, 90.0, 120.0, 150.0)
"""Full pointy-top hexagonal lattice -- the correct set for a hex-prism mark.

A pointy-top hexagon's own sides run at 0/60/120 degrees while the isometric
extrusion runs at 30/90/150, so a mark built from hexagonal prisms uses all
six. Scoring such a mark against only the three isometric axes understates it:
the parametric explorer measures 88.5% on the isometric set and 100.0% on this
one, with zero defects either way.
"""

DEFAULT_AXES: tuple[float, ...] = HEXAGONAL_AXES
"""Axis set used when a caller does not name one."""

DEFAULT_ANGLE_TOL = 6.0
"""How far off-axis an edge may be and still be treated as jitter to snap."""

DEFAULT_EXACT_TOL = 0.5
"""How far off-axis an edge may be and still count as conforming."""

DEFAULT_MIN_SEGMENT = 5.0
"""Segments shorter than this are tracer stepping artefacts, not design."""

_PATH_RE = re.compile(r'(<path\b[^>]*\bd=")([^"]*)(")')
_POLY_RE = re.compile(r'(<polygon\b[^>]*\bpoints=")([^"]*)(")')
_POINT_RE = re.compile(r"(-?[\d.]+)[, ](-?[\d.]+)")


@dataclass(frozen=True, slots=True)
class _Geometry:
    match: re.Match[str]
    is_polygon: bool


def _geometry_matches(svg_text: str) -> list[_Geometry]:
    """Every polygonal geometry carrier in document order.

    Builders differ: the traced master emits `<path d="M ... L ... Z">` while
    the parametric explorer emits `<polygon points="...">`. Both describe the
    same thing, and the metric must see both or it silently scores a document
    it never actually read.
    """
    found = [_Geometry(m, is_polygon=False) for m in _PATH_RE.finditer(svg_text)]
    found += [_Geometry(m, is_polygon=True) for m in _POLY_RE.finditer(svg_text)]
    return sorted(found, key=lambda geometry: geometry.match.start())


@dataclass(frozen=True)
class EdgeDefect:
    """One irregular edge, located well enough to find it in the file."""

    kind: DefectKind
    path_index: int
    segment_index: int
    start: Point
    end: Point
    length: float
    angle: float
    deviation: float
    """Degrees away from the nearest canonical axis."""

    def describe(self) -> str:
        a, b = self.start, self.end
        return (
            f"path {self.path_index} seg {self.segment_index}: {self.kind} "
            f"({a.x:.0f},{a.y:.0f})->({b.x:.0f},{b.y:.0f}) "
            f"len={self.length:.1f}px angle={self.angle:.1f}° dev={self.deviation:+.1f}°"
        )


@dataclass
class EdgeReport:
    """Edge-quality summary for one SVG document."""

    total_edges: int = 0
    total_length: float = 0.0
    conforming_length: float = 0.0
    defects: list[EdgeDefect] = field(default_factory=list[EdgeDefect])

    CONFORMANCE_PRECISION: ClassVar[int] = 6
    """Decimal places the published score is rounded to.

    Floating-point summation is order-dependent, so accumulating the same edge
    lengths in a different order can differ in the last bit. Rounding at the
    boundary makes the published number exactly reproducible -- which is what
    lets it be compared against a fixed CI floor -- at a precision far below
    any geometrically meaningful difference.
    """

    @property
    def axis_conformance_pct(self) -> float:
        """Share of edge length lying on a canonical axis. Higher is better."""
        if self.total_length <= 0:
            return 100.0
        raw = 100.0 * self.conforming_length / self.total_length
        return round(raw, self.CONFORMANCE_PRECISION)

    def count(self, kind: DefectKind) -> int:
        return sum(1 for d in self.defects if d.kind == kind)

    @property
    def summary(self) -> dict[str, float]:
        return {
            "axis_conformance_pct": round(self.axis_conformance_pct, 2),
            "total_edges": self.total_edges,
            "micro_segment": self.count("micro_segment"),
            "off_axis": self.count("off_axis"),
            "collinear": self.count("collinear"),
            "staircase": self.count("staircase"),
        }


def _angle(a: Point, b: Point) -> float:
    """Undirected direction of a->b in [0, 180)."""
    return math.degrees(math.atan2(b.y - a.y, b.x - a.x)) % 180.0


@dataclass(frozen=True, slots=True)
class _Deviation:
    degrees: float
    """Signed degrees away from `axis`."""
    axis: float


def _deviation(angle: float, axes: tuple[float, ...]) -> _Deviation:
    """Signed deviation from the nearest axis, and that axis."""
    best_axis, best_dev = axes[0], 999.0
    for axis in axes:
        raw = (angle - axis + 90.0) % 180.0 - 90.0
        if abs(raw) < abs(best_dev):
            best_axis, best_dev = axis, raw
    return _Deviation(best_dev, best_axis)


def parse_subpaths(d: str) -> list[list[Point]]:
    """Split a path's `d` into closed point rings.

    Deliberately simple: these builders emit polygonal M/L/Z paths, so a curve
    command would be mis-read. `has_curves` guards against that.
    """
    rings: list[list[Point]] = []
    for chunk in re.split(r"(?=[Mm])", d):
        pts = parse_points(chunk)
        if len(pts) >= 3:
            rings.append(pts)
    return rings


def parse_points(points: str) -> list[Point]:
    """Parse a `<polygon points="...">` attribute into one closed ring."""
    return [Point(float(m.group(1)), float(m.group(2))) for m in _POINT_RE.finditer(points)]


def has_curves(d: str) -> bool:
    """Whether a path uses curve commands, which this module does not handle."""
    return bool(re.search(r"[CcSsQqTtAa]", d))


def analyze_edges(
    svg_text: str,
    *,
    axes: tuple[float, ...] = DEFAULT_AXES,
    exact_tol: float = DEFAULT_EXACT_TOL,
    angle_tol: float = DEFAULT_ANGLE_TOL,
    min_segment: float = DEFAULT_MIN_SEGMENT,
) -> EdgeReport:
    """Measure how well an SVG's edges conform to its canonical axes."""
    report = EdgeReport()
    for path_index, geometry in enumerate(_geometry_matches(svg_text)):
        d, is_polygon = geometry.match.group(2), geometry.is_polygon
        if not is_polygon and has_curves(d):
            continue
        rings = [parse_points(d)] if is_polygon else parse_subpaths(d)
        for ring in (r for r in rings if len(r) >= 3):
            for seg_index, (a, b) in enumerate(zip(ring, ring[1:] + ring[:1], strict=False)):
                length = math.hypot(b.x - a.x, b.y - a.y)
                if length <= 0:
                    continue
                angle = _angle(a, b)
                dev = _deviation(angle, axes).degrees
                report.total_edges += 1
                report.total_length += length
                if abs(dev) <= exact_tol:
                    report.conforming_length += length

                kind: DefectKind | None = None
                if length < min_segment:
                    kind = "micro_segment"
                elif exact_tol < abs(dev) <= angle_tol:
                    kind = "off_axis"
                if kind is not None:
                    report.defects.append(
                        EdgeDefect(
                            kind=kind,
                            path_index=path_index,
                            segment_index=seg_index,
                            start=a,
                            end=b,
                            length=length,
                            angle=angle,
                            deviation=dev,
                        )
                    )

            # A staircase is a run of short segments that alternate direction
            # while making net progress along one line -- the classic tracer
            # artefact, and invisible to a per-segment check.
            run = 0
            for a, b, c in zip(ring, ring[1:], ring[2:], strict=False):
                short = math.hypot(b.x - a.x, b.y - a.y) < min_segment
                turn = abs(_angle(a, b) - _angle(b, c))
                if short and min(turn, 180.0 - turn) > 20.0:
                    run += 1
                    if run >= 2:
                        report.defects.append(
                            EdgeDefect(
                                kind="staircase",
                                path_index=path_index,
                                segment_index=ring.index(a),
                                start=a,
                                end=c,
                                length=math.hypot(c.x - a.x, c.y - a.y),
                                angle=_angle(a, c),
                                deviation=_deviation(_angle(a, c), axes).degrees,
                            )
                        )
                else:
                    run = 0
    return report


def _drop_micro_and_collinear(ring: list[Point], min_segment: float, collinear_tol: float) -> list[Point]:
    """Remove stepping artefacts and vertices that carry no direction change."""
    pts = list(ring)
    changed = True
    while changed and len(pts) > 3:
        changed = False
        for i in range(len(pts)):
            prev_pt, cur, nxt = pts[i - 1], pts[i], pts[(i + 1) % len(pts)]
            if math.hypot(cur.x - prev_pt.x, cur.y - prev_pt.y) < min_segment:
                pts.pop(i)
                changed = True
                break
            turn = abs(_angle(prev_pt, cur) - _angle(cur, nxt))
            if min(turn, 180.0 - turn) < collinear_tol:
                pts.pop(i)
                changed = True
                break
    return pts


def _intersect(p1: Point, d1: Point, p2: Point, d2: Point) -> Point | None:
    """Intersection of two lines given as point + direction."""
    det = d1.x * -d2.y - -d2.x * d1.y
    if abs(det) < 1e-9:
        return None
    rx, ry = p2.x - p1.x, p2.y - p1.y
    t = (rx * -d2.y - -d2.x * ry) / det
    return p1.offset(d1, t)


@dataclass(frozen=True, slots=True)
class _Line:
    through: Point
    direction: Point


def regularize_ring(
    ring: list[Point],
    *,
    axes: tuple[float, ...],
    angle_tol: float,
    min_segment: float,
    collinear_tol: float,
) -> list[Point]:
    """Snap a ring's near-axis edges onto their axes and rebuild its corners."""
    pts = _drop_micro_and_collinear(ring, min_segment, collinear_tol)
    if len(pts) < 3:
        return ring

    lines: list[_Line] = []
    for a, b in zip(pts, pts[1:] + pts[:1], strict=False):
        angle = _angle(a, b)
        deviation = _deviation(angle, axes)
        # Snap only genuine jitter. A deliberately oblique edge (a bevel) is
        # further off than angle_tol and must be left exactly as drawn.
        use = deviation.axis if abs(deviation.degrees) <= angle_tol else angle
        rad = math.radians(use)
        mid = Point((a.x + b.x) / 2.0, (a.y + b.y) / 2.0)
        lines.append(_Line(mid, Point(math.cos(rad), math.sin(rad))))

    out: list[Point] = []
    for i in range(len(pts)):
        prev_line = lines[i - 1]
        cur_line = lines[i]
        hit = _intersect(prev_line.through, prev_line.direction, cur_line.through, cur_line.direction)
        # Near-parallel neighbours have no stable corner; keep the traced vertex.
        out.append(hit if hit is not None else pts[i])
    return out


def regularize_svg(
    svg_text: str,
    *,
    axes: tuple[float, ...] = DEFAULT_AXES,
    angle_tol: float = DEFAULT_ANGLE_TOL,
    min_segment: float = DEFAULT_MIN_SEGMENT,
    collinear_tol: float = 2.0,
    precision: int = 2,
) -> str:
    """Return `svg_text` with its polygonal paths regularized onto the axes."""

    def _fix(ring: list[Point]) -> list[Point]:
        return regularize_ring(
            ring,
            axes=axes,
            angle_tol=angle_tol,
            min_segment=min_segment,
            collinear_tol=collinear_tol,
        )

    def rewrite_path(match: re.Match[str]) -> str:
        head, d, tail = match.group(1), match.group(2), match.group(3)
        if has_curves(d):
            return match.group(0)
        rings = [r for r in parse_subpaths(d) if len(r) >= 3]
        if not rings:
            return match.group(0)
        parts = [
            "M " + " L ".join(f"{p.x:.{precision}f},{p.y:.{precision}f}" for p in _fix(ring)) + " Z" for ring in rings
        ]
        return head + " ".join(parts) + tail

    def rewrite_polygon(match: re.Match[str]) -> str:
        head, pts, tail = match.group(1), match.group(2), match.group(3)
        ring = parse_points(pts)
        if len(ring) < 3:
            return match.group(0)
        coords = " ".join(f"{p.x:.{precision}f},{p.y:.{precision}f}" for p in _fix(ring))
        return head + coords + tail

    return _POLY_RE.sub(rewrite_polygon, _PATH_RE.sub(rewrite_path, svg_text))
