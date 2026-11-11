"""The single-SVG image-drift commands, run end to end against the labs brand's own builder."""

from __future__ import annotations

from typing import TYPE_CHECKING

import pytest
from typer.testing import CliRunner

from rig.image_drift import cli, cli_base

if TYPE_CHECKING:
    from pathlib import Path

pytestmark = pytest.mark.ci

RUNNER = CliRunner()


def _first_feature() -> str:
    labs = cli_base.resolve_project(cli_base.DEFAULT_PROJECT)
    return next(iter(cli_base.feature_map(cli_base.load_builder_module(labs.builder_module).feature_regions)))


def _built(tmp_path: Path) -> Path:
    svg = tmp_path / "built.svg"
    result = RUNNER.invoke(cli.app, ["build", "--svg", str(svg)])
    assert result.exit_code == 0, result.output
    return svg


def test_build_lists_the_builders_variants() -> None:
    result = RUNNER.invoke(cli.app, ["build", "--list-variants"])

    assert result.exit_code == 0, result.output
    assert "Builder Variants" in result.output


def test_build_writes_the_same_svg_every_time(tmp_path: Path) -> None:
    first = _built(tmp_path).read_bytes()
    second = _built(tmp_path).read_bytes()

    assert first == second
    assert first.lstrip().startswith(b"<")


def test_build_refuses_an_unknown_variant(tmp_path: Path) -> None:
    result = RUNNER.invoke(cli.app, ["build", "--svg", str(tmp_path / "x.svg"), "--variant", "no-such-variant"])

    assert result.exit_code == 1
    assert "Unknown variant" in result.output


def test_extract_feature_refuses_a_missing_image(tmp_path: Path) -> None:
    result = RUNNER.invoke(
        cli.app,
        [
            "extract-feature",
            "--image",
            str(tmp_path / "gone.png"),
            "--feature",
            "x",
            "--output",
            str(tmp_path / "o.png"),
        ],
    )

    assert result.exit_code == 1
    assert "does not exist" in result.output


def test_extract_feature_refuses_an_unknown_region(tmp_path: Path) -> None:
    svg = _built(tmp_path)

    result = RUNNER.invoke(
        cli.app,
        ["extract-feature", "--image", str(svg), "--feature", "no-such-region", "--output", str(tmp_path / "o.png")],
    )

    assert result.exit_code == 1
    assert "Unknown feature region" in result.output


@pytest.mark.parametrize("box", ["--target-box", "--reference-box"])
def test_extract_feature_crops_a_region_from_an_svg_and_cleans_up(tmp_path: Path, box: str) -> None:
    svg = _built(tmp_path)
    out = tmp_path / "crop.png"

    result = RUNNER.invoke(
        cli.app, ["extract-feature", "--image", str(svg), "--feature", _first_feature(), "--output", str(out), box]
    )

    assert result.exit_code == 0, result.output
    assert out.is_file()
    assert not list(tmp_path.glob("_temp_extract_*"))


def test_compare_refuses_a_missing_reference(tmp_path: Path) -> None:
    result = RUNNER.invoke(
        cli.app, ["compare", "--reference", str(tmp_path / "gone.png"), "--svg", str(tmp_path / "x.svg")]
    )

    assert result.exit_code == 1
    assert "Reference image" in result.output


def test_compare_refuses_a_missing_svg_and_render(tmp_path: Path) -> None:
    reference = cli_base.resolve_project(cli_base.DEFAULT_PROJECT).reference_path

    missing_svg = RUNNER.invoke(cli.app, ["compare", "--reference", str(reference), "--svg", str(tmp_path / "x.svg")])
    missing_render = RUNNER.invoke(
        cli.app, ["compare", "--reference", str(reference), "--render", str(tmp_path / "x.png")]
    )

    assert (missing_svg.exit_code, missing_render.exit_code) == (1, 1)
    assert "SVG file" in missing_svg.output
    assert "Render image" in missing_render.output


def test_compare_scores_a_build_against_the_reference(tmp_path: Path) -> None:
    svg = _built(tmp_path)
    reference = cli_base.resolve_project(cli_base.DEFAULT_PROJECT).reference_path

    result = RUNNER.invoke(
        cli.app,
        [
            "compare",
            "--reference",
            str(reference),
            "--svg",
            str(svg),
            "--output-sheet",
            str(tmp_path / "sheet.png"),
            "--decompose",
        ],
    )

    assert result.exit_code == 0, result.output
    assert (tmp_path / "sheet.png").is_file()
    assert not list(tmp_path.glob("_rendered_*"))


def test_compare_isolates_one_feature_and_refuses_an_unknown_one(tmp_path: Path) -> None:
    svg = _built(tmp_path)
    reference = cli_base.resolve_project(cli_base.DEFAULT_PROJECT).reference_path
    diff = tmp_path / "diff.png"

    known = RUNNER.invoke(
        cli.app,
        [
            "compare",
            "--reference",
            str(reference),
            "--svg",
            str(svg),
            "--feature",
            _first_feature(),
            "--output-diff",
            str(diff),
        ],
    )
    unknown = RUNNER.invoke(
        cli.app, ["compare", "--reference", str(reference), "--svg", str(svg), "--feature", "no-such-region"]
    )

    assert known.exit_code == 0, known.output
    assert diff.is_file()
    assert unknown.exit_code == 1
    assert "Unknown feature region" in unknown.output


def test_diagnose_reports_the_drift_channels(tmp_path: Path) -> None:
    svg = _built(tmp_path)
    reference = cli_base.resolve_project(cli_base.DEFAULT_PROJECT).reference_path

    result = RUNNER.invoke(cli.app, ["diagnose", "--reference", str(reference), "--svg", str(svg), "--size", "200"])

    assert result.exit_code == 0, result.output
    assert "DECOMPOSED_DRIFT_DIAGNOSTICS" in result.output


def test_diagnose_refuses_missing_inputs(tmp_path: Path) -> None:
    reference = cli_base.resolve_project(cli_base.DEFAULT_PROJECT).reference_path

    no_reference = RUNNER.invoke(
        cli.app, ["diagnose", "--reference", str(tmp_path / "gone.png"), "--svg", str(tmp_path / "x.svg")]
    )
    no_svg = RUNNER.invoke(cli.app, ["diagnose", "--reference", str(reference), "--svg", str(tmp_path / "x.svg")])
    no_render = RUNNER.invoke(cli.app, ["diagnose", "--reference", str(reference), "--render", str(tmp_path / "x.png")])

    assert (no_reference.exit_code, no_svg.exit_code, no_render.exit_code) == (1, 1, 1)
