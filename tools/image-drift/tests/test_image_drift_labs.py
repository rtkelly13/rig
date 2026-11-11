"""Tests for rtkelly-labs vector logo builder and drift calibration."""

from __future__ import annotations

import hashlib
from pathlib import Path

import pytest

from image_drift import cli_base, labs_builder
from image_drift.imaging import chromium_available, render_svg
from image_drift.metrics import compute_drift
from image_drift.variants import UnknownVariantError

pytestmark = pytest.mark.ci


def test_labs_builder_exports() -> None:
    """labs_builder must satisfy the BuilderModule protocol."""
    assert len(labs_builder.FEATURE_REGIONS) == 5
    assert labs_builder.DEFAULT_VARIANT in labs_builder.VARIANTS
    assert callable(labs_builder.build_svg_logo)


def test_feature_region_bounds() -> None:
    """All feature regions must be within the 500x500 normalised canvas."""
    for fr in labs_builder.FEATURE_REGIONS:
        assert 0 <= fr.x0 < fr.x1 <= 500
        assert 0 <= fr.y0 < fr.y1 <= 500


def test_build_svg_logo_produces_clean_vector(tmp_path: Path) -> None:
    """Built SVG must be pure vector (< 30 KB) with no base64 raster embedding."""
    out_svg = tmp_path / "test_logo.svg"
    labs_builder.build_svg_logo(out_svg)

    assert out_svg.exists()
    content = out_svg.read_text(encoding="utf-8")
    assert "<svg" in content
    assert "viewBox=" in content
    assert "<path" in content
    assert "data:image" not in content  # No embedded base64 raster
    size_kb = out_svg.stat().st_size / 1024.0
    assert size_kb < 30.0, f"SVG size {size_kb:.2f} KB exceeds 30 KB limit"


def test_cli_calibrated_defaults() -> None:
    """CLI defaults must point to rtkelly-labs brand assets and builder."""
    assert cli_base.PROJECT_NAME == "rtkelly-labs"
    assert cli_base.DEFAULT_BUILDER_MODULE.name == "labs_builder.py"
    assert cli_base.DEFAULT_BUILDER_MODULE.exists()
    assert cli_base.DEFAULT_REFERENCE.name in ("02-smooth-crossover-baseline.jpg", "rtkelly-labs.png")
    assert cli_base.DEFAULT_REFERENCE.exists()
    assert cli_base.DEFAULT_LOGO.name == "rtkelly-labs.svg"
    assert cli_base.DEFAULT_LOGO.exists()


@pytest.mark.skipif(not chromium_available(), reason="Chromium not installed for rendering")
def test_drift_metrics_against_reference(tmp_path: Path) -> None:
    """Rendered SVG must achieve high fidelity against the baseline raster."""
    svg_path = cli_base.DEFAULT_LOGO
    ref_path = cli_base.DEFAULT_REFERENCE
    rendered_png = tmp_path / "rendered.png"

    render_svg(svg_path, rendered_png)
    result = compute_drift(ref_path, rendered_png)

    assert result.psnr_db >= 28.0, f"PSNR {result.psnr_db:.2f} dB is below 28.0 dB threshold"
    assert result.ssim >= 0.968, f"SSIM {result.ssim:.4f} is below 0.968 threshold"
    assert result.rmse_pct <= 4.0, f"RMSE {result.rmse_pct:.2f}% is above 4.0% threshold"


def test_committed_master_matches_builder_output() -> None:
    """The committed master SVG must be exactly what the builder produces.

    This is the guard against the asset and its builder drifting apart. It is
    byte-for-byte on purpose: the master ends `</svg>` with no trailing newline,
    and a round-trip that silently adds one is the most likely way for the
    traced geometry to be replaced by a near-copy.
    """
    generated = labs_builder.generate_svg("master")
    committed = cli_base.DEFAULT_LOGO.read_text(encoding="utf-8")
    assert generated == committed, (
        "assets/rtkelly-labs/rtkelly-labs.svg has diverged from labs_builder. "
        "Regenerate it with: uv run image-reconcile build -p rtkelly-labs"
    )


def test_master_structure_is_intact() -> None:
    """Structural tripwire on the traced master -- no renderer required.

    The master is a trace, not computed geometry, so there is nothing to
    recompute it from if it is damaged. These counts are cheap to check and
    catch a truncated or partially-rewritten file immediately.
    """
    master = labs_builder.generate_svg("master")
    assert master.count("<path") == 16
    assert master.count("<linearGradient") == 15
    assert master.count("feDropShadow") == 1
    assert master.rstrip().endswith("</svg>")


def test_favicon_variant_is_not_the_master() -> None:
    """The declared favicon must be a real cut, not the master under another name.

    This is the regression that shipped: `generate_svg` accepted `variant` and
    discarded it, so `image-reconcile favicon` emitted the full logo and every
    downstream raster was it scaled down.
    """
    assert labs_builder.generate_svg("favicon") != labs_builder.generate_svg("master")


def test_every_declared_variant_is_distinct() -> None:
    """No two declared variants may render the same SVG."""
    rendered = {key: labs_builder.generate_svg(key) for key in labs_builder.VARIANTS}
    assert len(set(rendered.values())) == len(labs_builder.VARIANTS)


def test_every_declared_variant_has_a_backing_file() -> None:
    """Each declared variant resolves to a real, non-empty committed SVG."""
    entries = labs_builder.manifest().variants
    for key in labs_builder.VARIANTS:
        assert key in entries, f"variant {key!r} is declared but absent from manifest.json"
        path = labs_builder.BUILDER_ASSETS / entries[key].file
        assert path.is_file() and path.stat().st_size > 0


def test_unknown_variant_raises() -> None:
    """An undeclared variant must raise rather than fall back to the master."""
    with pytest.raises(UnknownVariantError):
        labs_builder.generate_svg("no-such-variant")


def test_master_sha256_matches_manifest() -> None:
    """The master's recorded digest must match the bytes on disk.

    The master is a trace with no generator behind it, so an unprovenanced
    hand-edit is unrecoverable. The digest makes one fail loudly.
    """
    entry = labs_builder.manifest().variants["master"]
    digest = hashlib.sha256((labs_builder.BUILDER_ASSETS / entry.file).read_bytes()).hexdigest()
    assert digest == entry.sha256, (
        "builder_assets/rtkelly-labs/master.svg changed without its manifest digest being updated. "
        "The master may only change via `image-reconcile promote`."
    )


def test_favicon_is_simpler_than_master() -> None:
    """The favicon must actually shed detail, not merely differ.

    Below ~48px the master's facets turn to mud, so the cut drops the drop
    shadow and collapses fifteen gradients into two flat tones.
    """
    favicon = labs_builder.generate_svg("favicon")
    master = labs_builder.generate_svg("master")
    assert favicon.count("<path") < master.count("<path")
    assert favicon.count("<linearGradient") < master.count("<linearGradient")
    assert "feDropShadow" not in favicon


def test_favicon_viewbox_matches_master() -> None:
    """Both variants share a viewBox so every raster cut registers identically."""
    assert 'viewBox="0 0 1024 1024"' in labs_builder.generate_svg("favicon")
    assert 'viewBox="0 0 1024 1024"' in labs_builder.generate_svg("master")


def test_default_logo_is_not_the_optimized_sidecar() -> None:
    """The canonical logo must never resolve to a lossy derivative."""
    assert cli_base.DEFAULT_LOGO.name == "rtkelly-labs.svg"
    assert "optimized" not in cli_base.DEFAULT_LOGO.name
    assert "min" not in cli_base.DEFAULT_LOGO.stem.split("-")
