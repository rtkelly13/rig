"""Native Vector SVG Generator for the Parquet.SourceGenerator Minimalist Diamond Parquet Mark.

Synthesizes the resolution-independent vector logo and avatar assets for Parquet.SourceGenerator
combining an interlocking diamond-oriented parquet tile field with a git-style commit branching
lineage and neon cyan/violet glowing nodes. Conforms to the BuilderModule protocol expected
by the image-reconcile CLI harness.
"""

# ruff: noqa: E501
from __future__ import annotations

from pathlib import Path

from image_drift.builder_contract import FeatureRegion
from image_drift.variants import Variant, VariantRegistry, resolve_variant

FEATURE_REGIONS: list[FeatureRegion] = [
    FeatureRegion(key="diamond_tile", label="Diamond Tile", x0=65, y0=65, x1=445, y1=445),
    FeatureRegion(key="root_node", label="Root Node", x0=225, y0=325, x1=285, y1=385),
    FeatureRegion(key="center_trunk", label="Center Trunk", x0=225, y0=150, x1=285, y1=325),
    FeatureRegion(key="left_branch", label="Left Branch", x0=140, y0=175, x1=225, y1=275),
    FeatureRegion(key="right_branch", label="Right Branch", x0=285, y0=175, x1=370, y1=275),
]

VARIANTS: VariantRegistry = {
    "master": Variant(
        name="Minimalist Diamond Parquet Master",
        description=(
            "Resolution-independent vector mark for Parquet.SourceGenerator featuring an "
            "interlocking 45-degree diamond parquet tile card on dark navy-charcoal with a "
            "centered 3-way git fork graph carved into wood seams with glowing nodes."
        ),
    ),
    "favicon": Variant(
        name="Favicon Cut",
        description="High-contrast diamond parquet cut optimized for 16-32px display.",
    ),
}

DEFAULT_VARIANT = "master"

BUILDER_ASSETS = Path(__file__).parent / "builder_assets" / "parquet-source-generator"
"""One hand-tuned SVG per variant, named `<variant>.svg`."""


def generate_svg(variant: str | None = DEFAULT_VARIANT) -> str:
    """Generate the SVG XML string for the specified brand mark variant.

    Raises:
        UnknownVariantError: If `variant` is not declared in `VARIANTS`.
    """
    key = resolve_variant(variant, VARIANTS, DEFAULT_VARIANT)
    return (BUILDER_ASSETS / f"{key}.svg").read_text(encoding="utf-8")


def build_svg_logo(output_path: Path, variant: str | None = DEFAULT_VARIANT) -> None:
    """Build the resolution-independent vector logo at the specified output path."""
    output_path.parent.mkdir(parents=True, exist_ok=True)
    svg_text = generate_svg(variant=variant)
    output_path.write_text(svg_text, encoding="utf-8", newline="\n")
