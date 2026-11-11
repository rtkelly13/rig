"""Cross-builder tests for the shared variant-resolution contract.

Each builder previously handled an undeclared variant its own way: rhombille
fell back to its default, diamond branched on one hardcoded key, and labs
accepted the argument and discarded it. These tests pin the shipped builders to
one rule; the rhombille explorer in `experiments/logo-explorers/` tests its own.
"""

from __future__ import annotations

from pathlib import Path
from types import ModuleType
from typing import Any

import pytest

from rig.image_drift import (
    cli_base,
    diamond_parquet_builder,
    labs_builder,
)
from rig.image_drift.variants import UnknownVariantError, Variant, resolve_variant

pytestmark = pytest.mark.ci

BUILDERS: list[ModuleType] = [labs_builder, diamond_parquet_builder]
BUILDER_IDS = [m.__name__.rsplit(".", 1)[-1] for m in BUILDERS]


@pytest.mark.parametrize("builder", BUILDERS, ids=BUILDER_IDS)
def test_builder_rejects_unknown_variant(builder: ModuleType, tmp_path: Path) -> None:
    """Every builder raises on an undeclared variant rather than falling back."""
    with pytest.raises(UnknownVariantError):
        builder.build_svg_logo(tmp_path / "out.svg", variant="no-such-variant")


@pytest.mark.parametrize("builder", BUILDERS, ids=BUILDER_IDS)
def test_builder_default_variant_is_declared(builder: ModuleType) -> None:
    """A builder's own default must appear in its VARIANTS mapping."""
    assert builder.DEFAULT_VARIANT in builder.VARIANTS


@pytest.mark.parametrize("builder", BUILDERS, ids=BUILDER_IDS)
def test_builder_none_variant_uses_default(builder: ModuleType, tmp_path: Path) -> None:
    """Passing None selects the declared default rather than raising."""
    explicit, implicit = tmp_path / "e.svg", tmp_path / "i.svg"
    builder.build_svg_logo(explicit, variant=builder.DEFAULT_VARIANT)
    builder.build_svg_logo(implicit, variant=None)
    assert explicit.read_bytes() == implicit.read_bytes()


def test_resolve_variant_rejects_undeclared_default() -> None:
    """A builder whose default is not declared is a definition bug; fail loudly."""
    with pytest.raises(UnknownVariantError):
        resolve_variant(None, {"master": {}}, "typo")


def test_builder_render_does_not_swallow_builder_errors(tmp_path: Path) -> None:
    """A TypeError raised inside a builder must propagate, not re-render the default.

    `Builder.render` used to catch TypeError to tolerate builders with no
    `variant` parameter, which also hid genuine failures behind a silent
    default-variant render.
    """
    calls: list[dict[str, Any]] = []

    def exploding_build(output_path: Path, variant: str | None = None) -> None:
        calls.append({"variant": variant})
        raise TypeError("internal builder bug")

    builder = cli_base.Builder(
        feature_regions=[],
        build=exploding_build,
        variants={"master": Variant(name="Master")},
        default_variant="master",
        accepts_variant=True,
    )
    with pytest.raises(TypeError, match="internal builder bug"):
        builder.render(tmp_path / "out.svg", "master")
    assert len(calls) == 1, "render retried after a builder-internal TypeError"


def test_builder_render_tolerates_variantless_builder(tmp_path: Path) -> None:
    """A builder without a `variant` parameter is still callable with one."""

    def variantless_build(output_path: Path) -> None:
        output_path.write_text("<svg/>", encoding="utf-8")

    builder = cli_base.Builder(
        feature_regions=[],
        build=variantless_build,
        variants={},
        default_variant=None,
        accepts_variant=cli_base._accepts_variant(variantless_build),
    )
    out = tmp_path / "out.svg"
    builder.render(out, "anything")
    assert out.read_text(encoding="utf-8") == "<svg/>"
