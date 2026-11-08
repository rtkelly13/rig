"""Tests for the drift reconciler's templating and scoring layer.

These cover the parts that do not need a browser: template rendering, escaping,
composite scoring, and the tie-detection that stops the gallery presenting
noise as a ranking.
"""

from __future__ import annotations

from pathlib import Path

import pytest

from rig.image_drift import cli, cli_base, cli_gallery, metrics
from rig.image_drift.builder_contract import FeatureRegion

pytestmark = pytest.mark.ci


REGIONS = [
    FeatureRegion(key="alpha", label="Alpha & Ampersand", x0=0, y0=0, x1=10, y1=10),
    FeatureRegion(key="beta", label="<beta>", x0=0, y0=0, x1=10, y1=10, ref_x0=5, ref_y0=5, ref_x1=9, ref_y1=9),
]

PIXEL = "data:image/png;base64,iVBORw0KGgo="


def _candidate(
    key: str = "a",
    name: str = "Candidate A: Thing",
    composite: float = 12.0,
    *,
    description: str = "A description",
) -> cli_gallery._CandidateView:
    return cli_gallery._CandidateView(
        key=key,
        name=name,
        short_name=name.split(":", maxsplit=1)[0],
        description=description,
        composite_pct=composite,
        full_pct=composite - 1.0,
        per_feature={region.key: composite for region in REGIONS},
        hero=PIXEL,
        feature_crops={region.key: PIXEL for region in REGIONS},
        badge_class=cli_gallery._badge_class(composite),
        is_chosen=True,
    )


def _context(**overrides: object) -> dict[str, object]:
    context: dict[str, object] = {
        "project_name": "Parquet.SourceGenerator",
        "reference_name": "icon.jpg",
        "reference_hero": PIXEL,
        "reference_crops": {region.key: PIXEL for region in REGIONS},
        "regions": REGIONS,
        "candidates": [_candidate()],
        "tied": False,
        "spread": 3.0,
        "tie_threshold": metrics.TIE_THRESHOLD_PCT,
        "frame_weight": 50,
        "feature_weight": 50,
    }
    context.update(overrides)
    return context


# ---------------------------------------------------------------------------
# Templates
# ---------------------------------------------------------------------------


def test_template_directory_ships_with_the_package() -> None:
    assert cli_base.TEMPLATE_DIR.is_dir()
    names = {p.name for p in cli_base.TEMPLATE_DIR.iterdir()}
    assert {"base.html.jinja", "wave_gallery.html.jinja", "gallery.css"} <= names


@pytest.mark.parametrize("template", ["wave_gallery.html.jinja", "single_gallery.html.jinja"])
def test_gallery_renders_self_contained_html(template: str) -> None:
    """No external stylesheet or script: the file must open straight from disk."""
    html = cli_gallery._render_gallery(template, **_context())
    assert html.startswith("<!DOCTYPE html>")
    assert "<style>" in html
    assert "<link" not in html
    assert "<script" not in html
    # CSS is inlined from gallery.css rather than linked.
    assert "--ds-surface-base" in html  # design-system token


def test_choose_gallery_renders_candidate_records() -> None:
    chips = [cli_gallery._SizeChip(64, PIXEL), cli_gallery._SizeChip(32, PIXEL)]
    choice = cli_gallery._ChoiceView(
        key="woven",
        name="Woven <grid>",
        description="Interlaced strands",
        hero=PIXEL,
        sizes=chips,
        spec=[cli_gallery._SpecRow("tiling", "hex")],
        is_current=True,
    )
    html = cli_gallery._render_gallery(
        "choose_gallery.html.jinja",
        project_name="Parquet.SourceGenerator",
        reference_name="icon.jpg",
        reference_hero=PIXEL,
        reference_sizes=chips,
        candidates=[choice],
        socket_sheet="",
    )
    assert "Woven &lt;grid&gt;" in html
    assert "woven at 32px" in html
    assert "--variant woven" in html
    assert "hex" in html


def test_wave_gallery_includes_the_reference_hero() -> None:
    """The original regression: the gallery scored fidelity without showing the source."""
    html = cli_gallery._render_gallery("wave_gallery.html.jinja", **_context())
    assert "Canonical Reference" in html
    assert "icon.jpg" in html
    assert html.count(PIXEL) >= 2


def test_labels_are_escaped_by_the_template() -> None:
    """Region labels are plain text, so the template must do the escaping."""
    html = cli_gallery._render_gallery("wave_gallery.html.jinja", **_context())
    assert "Alpha &amp; Ampersand" in html
    assert "&lt;beta&gt;" in html
    assert "<beta>" not in html


def test_reference_box_override_is_flagged_in_the_matrix() -> None:
    html = cli_gallery._render_gallery("wave_gallery.html.jinja", **_context())
    assert "reference box offset" in html


def test_tie_banner_appears_only_when_tied() -> None:
    tied = cli_gallery._render_gallery("wave_gallery.html.jinja", **_context(tied=True, spread=0.1))
    assert "statistically indistinguishable" in tied
    assert "Default (tied)" in tied
    assert "Best Match" not in tied

    ranked = cli_gallery._render_gallery("wave_gallery.html.jinja", **_context(tied=False, spread=3.0))
    assert "statistically indistinguishable" not in ranked
    assert "Best Match" in ranked


def test_rendering_is_deterministic() -> None:
    first = cli_gallery._render_gallery("wave_gallery.html.jinja", **_context())
    second = cli_gallery._render_gallery("wave_gallery.html.jinja", **_context())
    assert first == second


def test_every_candidate_appears_in_the_feature_matrix() -> None:
    candidates = [
        _candidate(key="a", name="Candidate A: One", composite=10.0),
        _candidate(key="b", name="Candidate B: Two", composite=20.0),
    ]
    html = cli_gallery._render_gallery("wave_gallery.html.jinja", **_context(candidates=candidates))
    for region in REGIONS:
        assert region.label.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;") in html
    assert html.count("Candidate A") >= len(REGIONS)
    assert html.count("Candidate B") >= len(REGIONS)


# ---------------------------------------------------------------------------
# Badge thresholds (scoring itself is covered in test_image_drift_metrics.py)
# ---------------------------------------------------------------------------


def test_badge_class_thresholds() -> None:
    assert cli_gallery._badge_class(10.0) == "badge-good"
    assert cli_gallery._badge_class(25.0) == "badge-warn"
    assert cli_gallery._badge_class(40.0) == "badge-bad"


def test_vectorize_cli_produces_svg(tmp_path: Path) -> None:
    from PIL import Image
    from typer.testing import CliRunner

    runner = CliRunner()
    input_png = tmp_path / "input.png"
    Image.new("RGB", (64, 64), (10, 100, 200)).save(input_png)
    out_svg = tmp_path / "out.svg"

    res = runner.invoke(cli.app, ["vectorize", "--image", str(input_png), "--output", str(out_svg), "--no-compare"])
    assert res.exit_code == 0
    assert out_svg.exists()
    assert "<svg" in out_svg.read_text(encoding="utf-8")


# ---------------------------------------------------------------------------
# Project Path Routing & Registry
# ---------------------------------------------------------------------------


def test_resolve_project_canonical_and_aliases() -> None:
    """Project resolution must correctly map canonical names and aliases to existing paths."""
    labs = cli_base.resolve_project("rtkelly-labs")
    assert labs.name == "rtkelly-labs"
    assert labs.asset_dir.is_dir()
    assert labs.reference_path.exists()
    assert labs.svg_path.exists()
    assert labs.builder_module.exists()

    # Alias check
    assert cli_base.resolve_project("labs") == labs

    parquet = cli_base.resolve_project("parquet-source-generator")
    assert parquet.name == "parquet-source-generator"
    assert parquet.asset_dir.is_dir()
    assert parquet.reference_path.exists()
    assert parquet.svg_path.exists()
    assert parquet.builder_module.exists()

    # Alias checks
    assert cli_base.resolve_project("parquet") == parquet
    assert cli_base.resolve_project("diamond") == parquet


def test_resolve_project_unknown_exits() -> None:
    """Resolving an unrecognized project must raise typer.Exit(code=1)."""
    with pytest.raises(cli_base.typer.Exit):
        cli_base.resolve_project("non-existent-project")


def test_cli_subcommands_expose_project_option() -> None:
    """All key subcommands must expose the --project / -p option for path routing."""
    import re

    from typer.testing import CliRunner

    runner = CliRunner()
    for cmd in ["compare", "export-avatar", "build", "favicon"]:
        result = runner.invoke(cli.app, [cmd, "--help"])
        assert result.exit_code == 0, f"'{cmd} --help' failed with code {result.exit_code}"
        clean_output = re.sub(r"\x1b\[[0-9;]*[a-zA-Z]", "", result.output)
        assert "--project" in clean_output, f"'{cmd}' missing --project option in {clean_output}"
        assert "-p" in clean_output, f"'{cmd}' missing -p option in {clean_output}"


def test_cli_serve_subcommand_help() -> None:
    """The 'serve' command must expose --port, --host, and --open options."""
    import re

    from typer.testing import CliRunner

    runner = CliRunner()
    result = runner.invoke(cli.app, ["serve", "--help"])
    assert result.exit_code == 0
    clean_output = re.sub(r"\x1b\[[0-9;]*[a-zA-Z]", "", result.output)
    assert "--port" in clean_output
    assert "--host" in clean_output
    assert "--open" in clean_output or "--no-open" in clean_output


def test_cli_snapshot_subcommand_execution(tmp_path: Path) -> None:
    """The 'snapshot' command captures an ephemeral variation to the variations dir."""
    from typer.testing import CliRunner

    assets_dir = tmp_path / "assets"
    variations_dir = tmp_path / "temp" / "variations"

    proj_dir = assets_dir / "test-brand"
    proj_dir.mkdir(parents=True)
    svg_file = proj_dir / "test-brand.svg"
    svg_file.write_text("<svg viewBox='0 0 100 100'></svg>")

    runner = CliRunner()
    result = runner.invoke(
        cli.app,
        [
            "snapshot",
            "--project",
            "test-brand",
            "--name",
            "step-alpha",
            "--run-id",
            "iteration-1",
            "--svg",
            str(svg_file),
            "--variations-dir",
            str(variations_dir),
        ],
    )
    assert result.exit_code == 0
    assert "Snapshot Captured Successfully" in result.output

    # Verify captured files
    run_dir = variations_dir / "test-brand" / "iteration-1"
    assert run_dir.exists()
    svgs = list(run_dir.glob("*.svg"))
    assert len(svgs) == 1
    jsons = list(run_dir.glob("*.json"))
    assert len(jsons) == 1
