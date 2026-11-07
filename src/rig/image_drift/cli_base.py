"""Shared state for the image-drift CLI: the Typer app, projects and builder loading."""

from __future__ import annotations

import importlib.util
import inspect
import sys
from collections.abc import Callable
from dataclasses import dataclass, field
from pathlib import Path
from typing import TYPE_CHECKING, Protocol, runtime_checkable

import typer
from pydantic import ValidationError
from rich.console import Console

from rig.image_drift.builder_contract import FeatureRegion
from rig.image_drift.geometry import CropBox
from rig.image_drift.labs_builder import (
    DEFAULT_VARIANT as _DEFAULT_VARIANT_KEY,
)
from rig.image_drift.labs_builder import (
    FEATURE_REGIONS as _DEFAULT_FEATURE_REGIONS,
)
from rig.image_drift.labs_builder import (
    VARIANTS as _DEFAULT_VARIANTS,
)
from rig.image_drift.labs_builder import (
    build_svg_logo as _default_build_svg_logo,
)
from rig.image_drift.variants import Variant, VariantRegistry
from rig.typed_json import decode, decode_json

if TYPE_CHECKING:
    pass

app = typer.Typer(help="Deterministic SVG Image Drift Reconciler & Visual Feature Extraction Engine")
console = Console()

_THIS_MODULE = Path(__file__).resolve()


def _repo_root() -> Path:
    for candidate in _THIS_MODULE.parents:
        if (candidate / "pyproject.toml").exists():
            return candidate
    return Path.cwd()


REPO_ROOT = _repo_root()
ASSETS_DIR = REPO_ROOT / "assets"


@dataclass(frozen=True)
class ProjectConfig:
    """Configuration mapping for a managed brand asset project."""

    name: str
    asset_dir: Path
    reference_path: Path
    svg_path: Path
    builder_module: Path
    default_variant: str = "master"


@dataclass(frozen=True, slots=True)
class _ReferenceManifest:
    """`reference/manifest.json`: which reference image is the baseline."""

    primary: str = ""


def _resolve_canonical_reference(asset_dir: Path, candidates: list[str]) -> Path:
    ref_dir = asset_dir / "reference"
    manifest_file = ref_dir / "manifest.json"
    if manifest_file.exists():
        try:
            prim = decode_json(_ReferenceManifest, manifest_file.read_text(encoding="utf-8")).primary.strip()
            if prim:
                if (ref_dir / prim).exists():
                    return ref_dir / prim
                if (asset_dir / prim).exists():
                    return asset_dir / prim
        except (OSError, ValueError) as exc:
            console.print(f"[yellow]Warning:[/] Ignoring unreadable reference manifest {manifest_file}: {exc}")

    for cand_name in candidates:
        if (ref_dir / cand_name).exists():
            return ref_dir / cand_name
        if (asset_dir / cand_name).exists():
            return asset_dir / cand_name
    return ref_dir / candidates[0]


_LABS_PROJECT = ProjectConfig(
    name="rtkelly-labs",
    asset_dir=ASSETS_DIR / "rtkelly-labs",
    reference_path=_resolve_canonical_reference(
        ASSETS_DIR / "rtkelly-labs",
        ["02-smooth-crossover-baseline.jpg", "rtkelly-labs.png"],
    ),
    svg_path=ASSETS_DIR / "rtkelly-labs" / "rtkelly-labs.svg",
    builder_module=_THIS_MODULE.parent / "labs_builder.py",
)

PROJECTS: dict[str, ProjectConfig] = {
    "rtkelly-labs": _LABS_PROJECT,
    "parquet-source-generator": ProjectConfig(
        name="parquet-source-generator",
        asset_dir=ASSETS_DIR / "parquet-source-generator",
        reference_path=_resolve_canonical_reference(
            ASSETS_DIR / "parquet-source-generator",
            ["02-simplified-cad-baseline.jpg", "parquet-diamond.png"],
        ),
        svg_path=ASSETS_DIR / "parquet-source-generator" / "parquet-diamond.svg",
        builder_module=_THIS_MODULE.parent / "diamond_parquet_builder.py",
    ),
}

DEFAULT_PROJECT = "rtkelly-labs"

PROJECT_ALIASES: dict[str, str] = {
    "rtkelly-labs": "rtkelly-labs",
    "labs": "rtkelly-labs",
    "parquet-source-generator": "parquet-source-generator",
    "parquet": "parquet-source-generator",
    "diamond": "parquet-source-generator",
}


def resolve_project(name: str | None = None) -> ProjectConfig:
    """Resolve a project key or alias to its canonical ProjectConfig."""
    raw = (name or DEFAULT_PROJECT).strip().lower()
    canonical = PROJECT_ALIASES.get(raw, raw)
    if canonical not in PROJECTS:
        known = ", ".join(sorted(PROJECTS.keys()))
        console.print(f"[bold red]Error:[/] Unknown project '{name}'. Available projects: {known}")
        raise typer.Exit(code=1)
    return PROJECTS[canonical]


DEFAULT_BUILDER_MODULE = _LABS_PROJECT.builder_module
DEFAULT_REFERENCE = _LABS_PROJECT.reference_path
DEFAULT_LOGO = _LABS_PROJECT.svg_path


def _scratch_root() -> Path:
    """This repo's gitignored `temp/` directory, for regenerable output.

    Preview output must not land in the target project: `preview.html` inlines
    every image as a data URI and runs to megabytes, so it would be re-committed
    on every run. Falls back to the working directory when the package is
    installed rather than run from a checkout.
    """
    for candidate in _THIS_MODULE.parents:
        if (candidate / "pyproject.toml").exists():
            return candidate / "temp"
    return Path.cwd() / "temp"


SCRATCH_DIR = _scratch_root()
DEFAULT_OUTPUT_DIR = SCRATCH_DIR / "logo-preview"
TEMPLATE_DIR = _THIS_MODULE.parent / "templates"

PROJECT_NAME = "rtkelly-labs"

# Sizes shown in choose mode. A logo's small-size legibility is the decisive
# question and cannot be judged from one large render.
CHOICE_SIZES = (128, 64, 32)

# Committed page assets live under assets/rtkelly-labs/
DEFAULT_FAVICON_DIR = ASSETS_DIR / "rtkelly-labs"
FAVICON_MASTER_SIZE = 512
FAVICON_PNG_SIZES = (512, 180, 48, 32, 16)
FAVICON_ICO_SIZES = (48, 32, 16)
APPLE_TOUCH_SIZE = 180


@runtime_checkable
class BuilderModule(Protocol):
    """Protocol that any SVG builder module must satisfy to be usable with this engine."""

    FEATURE_REGIONS: list[FeatureRegion]

    def build_svg_logo(self, output_path: Path, variant: str = ...) -> None: ...


def _accepts_variant(build: Callable[..., object]) -> bool:
    """Whether a builder's `build_svg_logo` takes a `variant` keyword."""
    try:
        return "variant" in inspect.signature(build).parameters
    except (TypeError, ValueError):
        return False


@dataclass(frozen=True)
class Builder:
    """A loaded builder module's public surface."""

    feature_regions: list[FeatureRegion]
    build: Callable[..., object]
    variants: VariantRegistry
    default_variant: str | None
    accepts_variant: bool = True

    def render(self, output_path: Path, variant: str | None) -> None:
        """Build an SVG, tolerating builders that take no `variant` argument.

        Support is decided by inspecting the signature rather than by catching
        TypeError, so a TypeError raised *inside* a builder propagates instead
        of being retried as a default-variant render.
        """
        if variant is None or not self.accepts_variant:
            self.build(output_path)
            return
        self.build(output_path, variant=variant)


@dataclass(frozen=True, slots=True)
class _BuilderExports:
    """The names a builder module exports, validated as they are read off the module."""

    FEATURE_REGIONS: list[FeatureRegion] | None = None
    build_svg_logo: Callable[..., object] | None = None
    VARIANTS: VariantRegistry = field(default_factory=dict[str, Variant])
    DEFAULT_VARIANT: str | None = None


def _default_builder() -> Builder:
    return Builder(
        feature_regions=_DEFAULT_FEATURE_REGIONS,
        build=_default_build_svg_logo,
        variants=_DEFAULT_VARIANTS,
        default_variant=_DEFAULT_VARIANT_KEY,
        accepts_variant=_accepts_variant(_default_build_svg_logo),
    )


def load_builder_module(module_path: Path) -> Builder:
    """Dynamically import a builder module from a filesystem path."""
    if module_path.resolve() == DEFAULT_BUILDER_MODULE.resolve():
        builder = _default_builder()
        console.print(
            f"[green]✓ Loaded builder module:[/] [bold]{module_path.name}[/] "
            f"({len(builder.feature_regions)} feature regions, {len(builder.variants)} wave variants)"
        )
        return builder

    if not module_path.exists():
        console.print(f"[bold red]Error:[/] Builder module '{module_path}' does not exist.")
        raise typer.Exit(code=1)

    spec = importlib.util.spec_from_file_location(module_path.stem, module_path)
    if spec is None or spec.loader is None:
        console.print(f"[bold red]Error:[/] Cannot load module spec from '{module_path}'.")
        raise typer.Exit(code=1)

    mod = importlib.util.module_from_spec(spec)
    sys.modules[module_path.stem] = mod
    spec.loader.exec_module(mod)

    try:
        exports = decode(_BuilderExports, vars(mod))
    except ValidationError as exc:
        console.print(f"[bold red]Error:[/] Builder module '{module_path.name}' has a mistyped export: {exc}")
        raise typer.Exit(code=1) from exc
    feature_regions, build_fn = exports.FEATURE_REGIONS, exports.build_svg_logo

    if feature_regions is None:
        console.print(
            f"[bold red]Error:[/] Builder module '{module_path.name}' must export FEATURE_REGIONS: list[FeatureRegion]."
        )
        raise typer.Exit(code=1)
    if build_fn is None:
        console.print(
            f"[bold red]Error:[/] Builder module '{module_path.name}' must export build_svg_logo(output_path: Path)."
        )
        raise typer.Exit(code=1)

    builder = Builder(
        feature_regions=feature_regions,
        build=build_fn,
        variants=exports.VARIANTS,
        default_variant=exports.DEFAULT_VARIANT,
        accepts_variant=_accepts_variant(build_fn),
    )
    console.print(
        f"[green]✓ Loaded builder module:[/] [bold]{module_path.name}[/] "
        f"({len(builder.feature_regions)} feature regions, {len(builder.variants)} wave variants)"
    )
    return builder


def feature_map(regions: list[FeatureRegion]) -> dict[str, FeatureRegion]:
    return {fr.key: fr for fr in regions}


def feature_boxes(regions: list[FeatureRegion]) -> dict[str, CropBox]:
    return {region.key: region.bbox for region in regions}
