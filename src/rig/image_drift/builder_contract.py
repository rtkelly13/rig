"""Types every builder module and the image-drift CLI share.

`FeatureRegion` is the crop-box contract a builder exports as `FEATURE_REGIONS`.
`Style` is a variant's resolved style knobs: `variant_style` merges a variant's
`StyleOverrides` over `STYLE_DEFAULTS`, and the choice gallery summarises them.
"""

from __future__ import annotations

from dataclasses import dataclass

from rig.image_drift.geometry import CropBox
from rig.image_drift.variants import Variant

# Ring radius as a multiple of the dot radius it encircles.
NOTCH_RING_RATIO = 2.0

# Stroke weights measured off the reference at 500x500 by isolating cool-hued
# stroke cores (blue-dominant, so the warm tiling is excluded) and taking the
# median run length across scanlines. The outer frame really is much lighter
# than the interior traces -- that contrast is most of the neon look.
FRAME_LINE_WIDTH = 3.0
TRACE_WIDTH = 5.0

# Commit dots scale with the thinner traces so they stay proportional.
NODE_RADIUS_SCALE = 0.85


@dataclass(frozen=True)
class FeatureRegion:
    """An axis-aligned crop region on the 500x500 normalised canvas.

    Both images are normalised to 500x500 before cropping, and the builder
    places every feature at its measured reference location, so one shared box
    is correct for both. The reference-only override exists for the rare case
    where the vector interpretation is deliberately offset.

    Attributes:
        key: Machine-readable identifier used as a dict key.
        label: Human-readable plain-text label shown in the preview HTML.
            Not pre-escaped -- the template escapes it.
        x0, y0, x1, y1: Crop box applied to both reference and target.
        ref_x0, ref_y0, ref_x1, ref_y1: Optional reference-only override; left
            at zero, `ref_bbox` falls back to the shared box.
    """

    key: str
    label: str
    x0: int
    y0: int
    x1: int
    y1: int
    ref_x0: int = 0
    ref_y0: int = 0
    ref_x1: int = 0
    ref_y1: int = 0

    @property
    def bbox(self) -> CropBox:
        """Target SVG crop box."""
        return CropBox(self.x0, self.y0, self.x1, self.y1)

    @property
    def ref_bbox(self) -> CropBox:
        """Reference image crop box."""
        if self.ref_x1 > self.ref_x0:
            return CropBox(self.ref_x0, self.ref_y0, self.ref_x1, self.ref_y1)
        return self.bbox

    @property
    def has_ref_override(self) -> bool:
        """True when this region crops the reference at a different location."""
        return self.ref_x1 > self.ref_x0


# Style knobs every variant may override. Kept as data rather than as separate
# copies of a builder: a fix then lands once, for every experiment, instead of
# N times with drift between them. Structurally different experiments belong
# in their own module via the CLI's --builder-module instead.
@dataclass(slots=True, frozen=True)
class Style:
    """Resolved style knobs for one variant."""

    frame_width: float = FRAME_LINE_WIDTH
    trace_width: float = TRACE_WIDTH
    ring_ratio: float = NOTCH_RING_RATIO
    node_scale: float = NODE_RADIUS_SCALE
    glow_wide: float = 6.0
    glow_tight: float = 2.0
    # False drops the emblems and thins the network to its trunk. Below about 48px
    # the emblems are smudges and the full circuit is mud, so a favicon needs
    # fewer, heavier marks rather than the same drawing shrunk.
    show_emblems: bool = True
    trunk_only: bool = False
    # Structural toggles, so a proposal is a parameter set rather than a fork.
    show_circuit: bool = True
    show_frame: bool = True
    flat_faces: bool = False
    # Column mark: fewer, fatter columns survive small sizes; five merge into a
    # single block below about 24px.
    column_count: int = 3
    column_segment: float = 56.0
    column_gap: float = 26.0
    show_footer: bool = True


STYLE_DEFAULTS = Style()


def _pick[T](override: T | None, default: T) -> T:
    return default if override is None else override


def variant_style(variant: Variant) -> Style:
    """Merge a variant's style overrides over the defaults."""
    o, d = variant.style, STYLE_DEFAULTS
    return Style(
        frame_width=_pick(o.frame_width, d.frame_width),
        trace_width=_pick(o.trace_width, d.trace_width),
        ring_ratio=_pick(o.ring_ratio, d.ring_ratio),
        node_scale=_pick(o.node_scale, d.node_scale),
        glow_wide=_pick(o.glow_wide, d.glow_wide),
        glow_tight=_pick(o.glow_tight, d.glow_tight),
        show_emblems=_pick(o.show_emblems, d.show_emblems),
        trunk_only=_pick(o.trunk_only, d.trunk_only),
        show_circuit=_pick(o.show_circuit, d.show_circuit),
        show_frame=_pick(o.show_frame, d.show_frame),
        flat_faces=_pick(o.flat_faces, d.flat_faces),
        column_count=_pick(o.column_count, d.column_count),
        column_segment=_pick(o.column_segment, d.column_segment),
        column_gap=_pick(o.column_gap, d.column_gap),
        show_footer=_pick(o.show_footer, d.show_footer),
    )
