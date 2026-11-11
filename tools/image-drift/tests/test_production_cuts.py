"""Production cuts derive strictly from each project's `production-cuts.csv`.

Rendering is faked (a blank PNG at the requested size), so these run without
Chromium and assert what the exporter decides: which files, at which sizes,
from which source.
"""

from __future__ import annotations

from pathlib import Path

import pytest
from PIL import Image
from typer.testing import CliRunner

from image_drift import cli, cli_base
from image_drift.production_cuts import (
    PRODUCTION_CUTS_CSV,
    CutSpec,
    UnknownCutFormatError,
    export_production_cuts,
    read_cut_specs,
)

pytestmark = pytest.mark.ci

PROJECT_KEYS = sorted(cli_base.PROJECTS)
HEADER = "filename,cut_type,width,height,format,purpose,description\n"


def _fake_render(svg_path: Path, output_png_path: Path, width: int, height: int) -> Path:
    assert svg_path.is_file()
    Image.new("RGBA", (width, height)).save(output_png_path)
    return output_png_path


def _write_variant(path: Path, variant: str) -> None:
    _ = path.write_text(f"<svg><!-- {variant} --></svg>", encoding="utf-8")


@pytest.mark.parametrize("project", PROJECT_KEYS)
def test_export_avatar_writes_exactly_the_declared_cuts(
    project: str, tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    monkeypatch.setattr("image_drift.production_cuts.render_svg", _fake_render)
    cfg = cli_base.PROJECTS[project]
    declared = read_cut_specs(cfg.asset_dir / "production-cuts" / PRODUCTION_CUTS_CSV)

    result = CliRunner().invoke(cli.app, ["export-avatar", "-p", project, "--output-dir", str(tmp_path)])

    assert result.exit_code == 0, result.output
    assert sorted(p.name for p in tmp_path.iterdir()) == sorted(cut.filename for cut in declared)
    for cut in declared:
        if cut.format == "png":
            with Image.open(tmp_path / cut.filename) as image:
                assert image.size == (cut.width, cut.height), cut.filename


@pytest.mark.parametrize("project", PROJECT_KEYS)
def test_svg_cuts_match_the_committed_cuts(project: str, tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    """The master and favicon vector cuts are copies, so a re-export reproduces the committed bytes."""
    monkeypatch.setattr("image_drift.production_cuts.render_svg", _fake_render)
    cuts_dir = cli_base.PROJECTS[project].asset_dir / "production-cuts"

    result = CliRunner().invoke(cli.app, ["export-avatar", "-p", project, "--output-dir", str(tmp_path)])

    assert result.exit_code == 0, result.output
    svg_cuts = [cut for cut in read_cut_specs(cuts_dir / PRODUCTION_CUTS_CSV) if cut.format == "svg"]
    assert svg_cuts
    for cut in svg_cuts:
        assert (tmp_path / cut.filename).read_bytes() == (cuts_dir / cut.filename).read_bytes(), cut.filename


def test_a_row_removed_from_the_csv_is_not_exported(tmp_path: Path) -> None:
    csv_path = tmp_path / PRODUCTION_CUTS_CSV
    _ = csv_path.write_text(HEADER + "only-avatar.png,avatar,64,32,png,Avatar,One cut\n", encoding="utf-8")
    master = tmp_path / "master.svg"
    _write_variant(master, "master")
    out = tmp_path / "out"

    written = export_production_cuts(
        read_cut_specs(csv_path), out, master_svg=master, build_variant=_write_variant, render=_fake_render
    )

    assert [p.name for p in written] == ["only-avatar.png"]
    assert sorted(p.name for p in out.iterdir()) == ["only-avatar.png"]
    with Image.open(out / "only-avatar.png") as image:
        assert image.size == (64, 32)


@pytest.mark.parametrize(
    ("cut_type", "expected_source"),
    [
        ("master_svg", "master"),
        ("favicon_svg", "favicon"),
    ],
)
def test_favicon_cut_types_come_from_the_favicon_variant(cut_type: str, expected_source: str, tmp_path: Path) -> None:
    master = tmp_path / "master.svg"
    _write_variant(master, "master")
    cut = CutSpec(filename="cut.svg", cut_type=cut_type, width=32, height=32, format="svg")

    _ = export_production_cuts([cut], tmp_path / "out", master_svg=master, build_variant=_write_variant)

    assert (tmp_path / "out" / "cut.svg").read_text(encoding="utf-8") == f"<svg><!-- {expected_source} --></svg>"


def test_ico_bundles_every_size_up_to_the_declared_width(tmp_path: Path) -> None:
    master = tmp_path / "master.svg"
    _write_variant(master, "master")
    cut = CutSpec(filename="favicon.ico", cut_type="favicon", width=32, height=32, format="ico")

    _ = export_production_cuts([cut], tmp_path, master_svg=master, build_variant=_write_variant, render=_fake_render)

    with Image.open(tmp_path / "favicon.ico") as image:
        assert sorted(image.info["sizes"]) == [(16, 16), (32, 32)]


def test_an_unknown_format_is_refused(tmp_path: Path) -> None:
    master = tmp_path / "master.svg"
    _write_variant(master, "master")
    cut = CutSpec(filename="logo.webp", cut_type="avatar", width=10, height=10, format="webp")

    with pytest.raises(UnknownCutFormatError, match="logo.webp"):
        _ = export_production_cuts([cut], tmp_path, master_svg=master, build_variant=_write_variant)
