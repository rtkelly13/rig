"""Parametric CAD explorer for the rtkelly-labs isometric mark.

The governed master (`labs_builder`) is a trace: high fidelity, but nothing can
recompute it, so it cannot be explored. This module is the other half -- the
same mark expressed as named geometry (loop radii, prism depth, beam width)
that can be swept to propose alternatives.

An earlier attempt at this failed because it drew flat unshaded facets, which
stripped out the planar lighting that gives the mark its depth. The fix is
`FaceLighting`: every facet gets its own `userSpaceOnUse` linear gradient whose
vector is projected from that facet's own extent along the light direction,
with the ramp chosen by the facet's orientation. That is what the traced master
does with fifteen hand-placed gradients, derived instead of hand-placed.

This module deliberately is **not** registered in `cli.PROJECTS`. No routine
command can make a parametric render the master; only `image-reconcile promote`
can, and only after the candidate beats the incumbent against the reference.
"""

from __future__ import annotations

import math
from collections.abc import Mapping, Sequence
from dataclasses import asdict, dataclass, field, fields
from pathlib import Path
from typing import Literal

from rig.image_drift.builder_contract import FeatureRegion
from rig.image_drift.hex_geometry import Point, hexagon_vertices
from rig.image_drift.labs_builder import FEATURE_REGIONS as _MASTER_FEATURE_REGIONS
from rig.image_drift.variants import Variant, VariantRegistry, resolve_variant
from rig.typed_json import decode

Orientation = Literal["top", "left", "right", "inner"]

# Feature regions are shared with the governed master so candidate scores are
# comparable against the incumbent's, region for region.
FEATURE_REGIONS: list[FeatureRegion] = list(_MASTER_FEATURE_REGIONS)


@dataclass(frozen=True, slots=True)
class GradientRamp:
    """An ordered colour ramp applied along a facet's lighting vector."""

    stops: tuple[str, ...]

    def offsets(self) -> tuple[float, ...]:
        if len(self.stops) == 1:
            return (0.0,)
        step = 1.0 / (len(self.stops) - 1)
        return tuple(round(i * step, 4) for i in range(len(self.stops)))


@dataclass(frozen=True, slots=True)
class FaceLighting:
    """Per-orientation colour ramps and the direction the light comes from."""

    top: GradientRamp
    left: GradientRamp
    right: GradientRamp
    inner: GradientRamp
    light_direction_deg: float = -60.0

    def ramp_for(self, orientation: Orientation) -> GradientRamp:
        match orientation:
            case "top":
                return self.top
            case "left":
                return self.left
            case "right":
                return self.right
            case "inner":
                return self.inner


DEFAULT_LIGHTING = FaceLighting(
    top=GradientRamp(("#5de7e9", "#35c8e1", "#1b8ec8")),
    left=GradientRamp(("#14b6c3", "#198ec3", "#1e6dc1")),
    right=GradientRamp(("#2069bd", "#1c54ac", "#173f96")),
    inner=GradientRamp(("#147eb6", "#1b6ab8", "#244aa4")),
)


@dataclass(frozen=True, slots=True)
class IsoLoopParams:
    """Named geometry for the interlocking isometric loops and crossover beams.

    Defaults are the CAD exploration the traced master was derived alongside,
    with the loop centres and beam length tuned so the two loops actually
    interlock -- a sweep starts from a recognisable mark rather than a blank
    page. They do **not** reproduce the traced master, and are not expected to:
    the explorer proposes, the promotion gate disposes.
    """

    canvas: int = 1024
    center: Point = Point(512.0, 512.0)
    loop_center: Point = Point(440.0, 440.0)
    r_outer: float = 158.0
    r_mid: float = 114.0
    r_inner: float = 72.0
    depth: float = 46.0
    gap: float = 6.0
    beam_length: float = 420.0
    beam_half_width: float = 36.0
    background: str = "#101923"
    lighting: FaceLighting = field(default=DEFAULT_LIGHTING)

    def with_overrides(self, overrides: Mapping[str, str]) -> IsoLoopParams:
        """Return a copy with `overrides` applied, coercing to each field's type.

        Raises:
            KeyError: If an override names a field that does not exist.
            ValueError: If a value cannot be coerced to the field's type.
        """
        types = {f.name: f.type for f in fields(self)}
        coerced: dict[str, int | float | str | Point] = {}
        for key, raw in overrides.items():
            if key not in types:
                raise KeyError(f"Unknown parameter {key!r}. Known: {', '.join(sorted(types))}")
            if key in {"canvas"}:
                coerced[key] = int(raw)
            elif key in {"background"}:
                coerced[key] = str(raw)
            elif key in {"center", "loop_center"}:
                x, y = (part.strip() for part in str(raw).split(","))
                coerced[key] = Point(float(x), float(y))
            else:
                coerced[key] = float(raw)
        return decode(IsoLoopParams, asdict(self) | coerced)


PRESETS: dict[str, IsoLoopParams] = {
    "baseline": IsoLoopParams(),
    "chunky": IsoLoopParams(r_inner=64.0, depth=54.0, beam_half_width=44.0),
    "fine": IsoLoopParams(r_inner=80.0, depth=38.0, beam_half_width=30.0),
}

DEFAULT_PRESET = "baseline"
DEFAULT_VARIANT = DEFAULT_PRESET

VARIANTS: VariantRegistry = {
    key: Variant(
        name=key.replace("_", " ").title(),
        description=(
            f"Parametric candidate: r_outer={p.r_outer}, r_inner={p.r_inner}, "
            f"depth={p.depth}, beam_half_width={p.beam_half_width}."
        ),
    )
    for key, p in PRESETS.items()
}


@dataclass(frozen=True, slots=True)
class IsoBasis:
    """Isometric basis vectors: `u` down-right (30 deg), `v` down-left (150 deg), `w` down."""

    u: Point
    v: Point
    w: Point


def iso_basis() -> IsoBasis:
    """Isometric basis vectors: down-right (30 deg), down-left (150 deg), down."""
    cos30 = math.sqrt(3) / 2.0
    return IsoBasis(u=Point(cos30, 0.5), v=Point(-cos30, 0.5), w=Point(0.0, 1.0))


def rot180(point: Point, center: Point) -> Point:
    """Rotate `point` a half turn about `center`; the second loop is the first's image."""
    return Point(2 * center.x - point.x, 2 * center.y - point.y)


def translate(point: Point, direction: Point, distance: float) -> Point:
    """Move `point` along `direction` by `distance`."""
    return point.offset(direction, distance)


def extrude(points: Sequence[Point], direction: Point, depth: float) -> list[Point]:
    """Offset every point along `direction` to form a prism's far face."""
    return [translate(p, direction, depth) for p in points]


def facet_orientation(points: Sequence[Point], center: Point) -> Orientation:
    """Classify a facet by where its centroid sits and how it is wound.

    Orientation drives which colour ramp a facet takes, which is what makes the
    result read as a lit solid rather than a flat diagram.
    """
    cx = sum(p.x for p in points) / len(points)
    cy = sum(p.y for p in points) / len(points)
    dx, dy = cx - center.x, cy - center.y
    if dy < -abs(dx) * 0.5:
        return "top"
    if abs(dx) < 0.25 * max(abs(dy), 1.0):
        return "inner"
    return "left" if dx < 0 else "right"


@dataclass(frozen=True, slots=True)
class GradientVector:
    """A `linearGradient`'s start (`x1`, `y1`) and end (`x2`, `y2`) in user space."""

    x1: float
    y1: float
    x2: float
    y2: float


@dataclass(frozen=True, slots=True)
class _Facet:
    points: list[Point]
    orientation: Orientation


def facet_gradient_vector(points: Sequence[Point], light_deg: float) -> GradientVector:
    """Project a facet's own extent onto the light direction to get x1,y1,x2,y2.

    Using the facet's extent rather than a shared canvas-wide vector is what
    keeps each face independently shaded.
    """
    ux, uy = math.cos(math.radians(light_deg)), math.sin(math.radians(light_deg))
    projections = [p.x * ux + p.y * uy for p in points]
    lo, hi = min(projections), max(projections)
    cx = sum(p.x for p in points) / len(points)
    cy = sum(p.y for p in points) / len(points)
    mid = (lo + hi) / 2.0
    return GradientVector(
        x1=round(cx + ux * (lo - mid), 2),
        y1=round(cy + uy * (lo - mid), 2),
        x2=round(cx + ux * (hi - mid), 2),
        y2=round(cy + uy * (hi - mid), 2),
    )


def _hexagon(radius: float, centre: Point) -> list[Point]:
    return hexagon_vertices(radius, centre)


def _fmt(points: Sequence[Point]) -> str:
    return " ".join(f"{p.x:.2f},{p.y:.2f}" for p in points)


def generate_svg(variant: str | None = DEFAULT_PRESET, params: IsoLoopParams | None = None) -> str:
    """Render a parametric candidate as raw SVG text.

    Raw text (rather than svgwrite) keeps output byte-deterministic and
    textually diffable against the traced master.
    """
    cfg = params if params is not None else PRESETS[resolve_variant(variant, VARIANTS, DEFAULT_PRESET)]
    basis = iso_basis()
    u, v, w = basis.u, basis.v, basis.w
    light = cfg.lighting.light_direction_deg

    facets: list[_Facet] = []

    # --- the two hexagonal loops -------------------------------------------
    for loop_center in (cfg.loop_center, rot180(cfg.loop_center, cfg.center)):
        outer = _hexagon(cfg.r_outer, loop_center)
        mid = _hexagon(cfg.r_mid, loop_center)
        inner = _hexagon(cfg.r_inner, loop_center)

        # Top annulus band between the outer and mid rings.
        for i in range(6):
            j = (i + 1) % 6
            facets.append(
                _Facet([outer[i], outer[j], mid[j], mid[i]], facet_orientation([outer[i], mid[j]], cfg.center))
            )
        # Inner annulus band, reading as the loop's lit interior lip.
        for i in range(6):
            j = (i + 1) % 6
            facets.append(_Facet([mid[i], mid[j], inner[j], inner[i]], "inner"))
        # Outer side walls give the loop its prism depth.
        outer_down = extrude(outer, w, cfg.depth)
        for i in (2, 3, 4):
            j = (i + 1) % 6
            facets.append(_Facet([outer[i], outer[j], outer_down[j], outer_down[i]], "right" if i == 2 else "left"))

    # --- the crossing beams ------------------------------------------------
    half_len = cfg.beam_length / 2.0
    hw = cfg.beam_half_width
    stop = hw + cfg.gap

    def beam_face(axis: Point, cross: Point, start: float, end: float) -> list[Point]:
        a = translate(cfg.center, axis, start)
        b = translate(cfg.center, axis, end)
        return [translate(a, cross, -hw), translate(b, cross, -hw), translate(b, cross, hw), translate(a, cross, hw)]

    # Under-beam first, split either side of the over-beam so it reads as passing beneath.
    for start, end in ((-half_len, -stop), (stop, half_len)):
        face = beam_face(v, u, start, end)
        facets.append(_Facet(face, "right"))
        facets.append(_Facet([face[3], face[2], *extrude([face[2], face[3]], w, cfg.depth)], "left"))

    # Over-beam last: one unbroken top face, no longitudinal seam.
    over = beam_face(u, v, -half_len, half_len)
    facets.append(_Facet(over, "top"))
    facets.append(_Facet([over[3], over[2], *extrude([over[2], over[3]], w, cfg.depth)], "left"))

    # --- emit ---------------------------------------------------------------
    defs: list[str] = []
    body: list[str] = []
    for idx, facet in enumerate(facets):
        ramp = cfg.lighting.ramp_for(facet.orientation)
        g = facet_gradient_vector(facet.points, light)
        stops = "".join(
            f'<stop offset="{off}" stop-color="{col}" />' for off, col in zip(ramp.offsets(), ramp.stops, strict=True)
        )
        defs.append(
            f'<linearGradient id="f{idx}" x1="{g.x1}" y1="{g.y1}" x2="{g.x2}" y2="{g.y2}" '
            f'gradientUnits="userSpaceOnUse">{stops}</linearGradient>'
        )
        body.append(f'<polygon points="{_fmt(facet.points)}" fill="url(#f{idx})" />')

    c = cfg.canvas
    return (
        f'<svg xmlns="http://www.w3.org/2000/svg" version="1" width="{c}" height="{c}" viewBox="0 0 {c} {c}">\n'
        f"<defs>\n" + "\n".join(defs) + "\n</defs>\n"
        f'<path d="M0,0 L{c},0 L{c},{c} L0,{c} Z" fill="{cfg.background}" />\n' + "\n".join(body) + "\n</svg>"
    )


def build_svg_logo(output_path: Path, variant: str | None = DEFAULT_PRESET) -> None:
    """Write a parametric candidate to `output_path`."""
    output_path.parent.mkdir(parents=True, exist_ok=True)
    output_path.write_text(generate_svg(variant), encoding="utf-8", newline="\n")
