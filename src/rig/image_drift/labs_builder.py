"""Brand mark builder for the rtkelly-labs GitHub organization.

The master mark is a *trace* of an approved reference raster, not computed
geometry: two hollow isometric hexagonal loops on 30/90/150 degree axes with
crossing beams at the centre, where one beam passes cleanly over the other with
no longitudinal seam down its face. Nothing can recompute it, so the SVG itself
is the artefact of record and lives beside this module in `builder_assets/`.

`assets/rtkelly-labs/rtkelly-labs.svg` is *output* -- `image-reconcile build`
writes it from here, and a test asserts the two stay byte-identical. The only
sanctioned way to change the master is `image-reconcile promote`, which requires
a candidate to beat the incumbent against the canonical reference.

Conforms to the BuilderModule protocol expected by the image-reconcile CLI.
"""

from __future__ import annotations

from dataclasses import dataclass
from functools import cache
from pathlib import Path

from pydantic import TypeAdapter

from rig.image_drift.builder_contract import FeatureRegion
from rig.image_drift.variants import Variant, VariantRegistry, resolve_variant

BUILDER_ASSETS = Path(__file__).parent / "builder_assets" / "rtkelly-labs"

FEATURE_REGIONS: list[FeatureRegion] = [
    FeatureRegion(key="crossover_core", label="Crossover Core", x0=190, y0=190, x1=310, y1=310),
    FeatureRegion(key="top_left_loop", label="Top-Left Loop", x0=95, y0=100, x1=255, y1=260),
    FeatureRegion(key="top_right_loop", label="Top-Right Loop", x0=245, y0=100, x1=405, y1=260),
    FeatureRegion(key="bottom_left_loop", label="Bottom-Left Loop", x0=95, y0=240, x1=255, y1=400),
    FeatureRegion(key="bottom_right_loop", label="Bottom-Right Loop", x0=245, y0=240, x1=405, y1=400),
]

VARIANTS: VariantRegistry = {
    "master": Variant(
        name="Candidate 1 Master",
        description=(
            "Resolution-independent vector logo for rtkelly-labs: isometric facets, native "
            "multi-stop linear gradients, and zero staircase aliasing."
        ),
    ),
    "favicon": Variant(
        name="Favicon Cut",
        description=(
            "High-contrast cut for 16-32px display. Same loop and beam geometry as the "
            "master, flattened to two tones with the drop shadow removed."
        ),
    ),
}

DEFAULT_VARIANT = "master"


@dataclass(frozen=True, slots=True)
class Promotion:
    """The exploration candidate a variant was promoted from."""

    run_id: str
    candidate: int
    notes: str


@dataclass(frozen=True, slots=True)
class VariantEntry:
    """One variant's committed file, its digest, and where it came from."""

    file: str
    sha256: str
    gated: bool
    provenance: str
    promoted_from: Promotion | None = None


@dataclass(frozen=True, slots=True)
class VariantManifest:
    """`builder_assets/rtkelly-labs/manifest.json`."""

    project: str
    variants: dict[str, VariantEntry]

    def to_json(self) -> str:
        """The manifest as committed: two-space indented, unset fields omitted, trailing newline."""
        return _MANIFEST.dump_json(self, indent=2, exclude_none=True).decode("utf-8") + "\n"


_MANIFEST = TypeAdapter(VariantManifest)


@cache
def _variant_manifest() -> VariantManifest:
    """Parse and cache `builder_assets/rtkelly-labs/manifest.json`."""
    return _MANIFEST.validate_json((BUILDER_ASSETS / "manifest.json").read_bytes())


def manifest() -> VariantManifest:
    """Return the variant manifest: per-variant sha256, gating, and provenance."""
    return _variant_manifest()


@cache
def _load_variant_svg(variant: str) -> str:
    """Read a variant's committed SVG from the packaged builder assets."""
    entry = _variant_manifest().variants[variant]
    return (BUILDER_ASSETS / entry.file).read_text(encoding="utf-8")


def reload_builder_assets() -> None:
    """Drop cached variant SVGs and the manifest after they change on disk.

    Only `image-reconcile promote` rewrites them, and it must then regenerate
    the committed asset from the new bytes within the same process.
    """
    _variant_manifest.cache_clear()
    _load_variant_svg.cache_clear()


def generate_svg(variant: str | None = DEFAULT_VARIANT) -> str:
    """Return the SVG XML for a brand mark variant.

    Raises:
        UnknownVariantError: If `variant` is not declared in `VARIANTS`.
    """
    return _load_variant_svg(resolve_variant(variant, VARIANTS, DEFAULT_VARIANT))


def build_svg_logo(output_path: Path, variant: str | None = DEFAULT_VARIANT) -> None:
    """Write the resolution-independent vector logo to `output_path`."""
    output_path.parent.mkdir(parents=True, exist_ok=True)
    output_path.write_text(generate_svg(variant), encoding="utf-8", newline="\n")
