"""Export a brand project's production cuts exactly as its `production-cuts.csv` declares them.

The CSV is the only source of what gets written: one file per row, at the row's size and format.
`favicon*` cut types come from the builder's `favicon` variant, which stays legible at small
sizes; every other cut comes from the canonical master SVG.
"""

from __future__ import annotations

import csv
import shutil
import tempfile
from collections.abc import Callable
from dataclasses import dataclass
from pathlib import Path

from PIL import Image
from pydantic import StrictStr

from image_drift.imaging import render_svg
from image_drift.typed_json import decode

PRODUCTION_CUTS_CSV = "production-cuts.csv"
FAVICON_VARIANT = "favicon"
ICO_SIZES: list[int] = [48, 32, 16]

type RenderSvg = Callable[[Path, Path, int, int], Path]
"""Render an SVG to a PNG of exactly (width, height): `render_svg`'s signature."""

type BuildVariant = Callable[[Path, str], None]
"""Write a builder variant's SVG to a path."""


class UnknownCutFormatError(ValueError):
    """A CSV row names a format the exporter cannot produce."""


@dataclass(frozen=True, slots=True)
class _CutCsvRow:
    filename: StrictStr
    cut_type: StrictStr
    width: StrictStr
    height: StrictStr
    format: StrictStr


@dataclass(frozen=True, slots=True)
class CutSpec:
    """One declared production cut."""

    filename: str
    cut_type: str
    width: int
    height: int
    format: str

    @property
    def from_favicon_variant(self) -> bool:
        return self.cut_type.startswith(FAVICON_VARIANT)


def read_cut_specs(csv_path: Path) -> list[CutSpec]:
    """Every row of a `production-cuts.csv`, in file order."""
    with csv_path.open(newline="", encoding="utf-8") as handle:
        rows = [decode(_CutCsvRow, raw) for raw in csv.DictReader(handle)]
    return [
        CutSpec(
            filename=row.filename.strip(),
            cut_type=row.cut_type.strip(),
            width=int(row.width),
            height=int(row.height),
            format=row.format.strip().lower(),
        )
        for row in rows
        if row.filename.strip()
    ]


def export_production_cuts(
    cuts: list[CutSpec],
    output_dir: Path,
    *,
    master_svg: Path,
    build_variant: BuildVariant,
    render: RenderSvg | None = None,
) -> list[Path]:
    """Write each declared cut into `output_dir` and return the written paths, in CSV order."""
    render_png = render or render_svg
    output_dir.mkdir(parents=True, exist_ok=True)
    written: list[Path] = []
    with tempfile.TemporaryDirectory() as scratch_name:
        scratch = Path(scratch_name)
        favicon_svg = scratch / "favicon.svg"
        build_variant(favicon_svg, FAVICON_VARIANT)
        for cut in cuts:
            source = favicon_svg if cut.from_favicon_variant else master_svg
            target = output_dir / cut.filename
            if cut.format == "svg":
                _ = shutil.copyfile(source, target)
            elif cut.format == "png":
                _ = render_png(source, target, cut.width, cut.height)
            elif cut.format == "ico":
                largest = render_png(source, scratch / f"{cut.filename}.png", cut.width, cut.height)
                with Image.open(largest) as image:
                    image.save(target, format="ICO", sizes=[(size, size) for size in ICO_SIZES if size <= cut.width])
            else:
                msg = f"{cut.filename}: unknown cut format {cut.format!r}"
                raise UnknownCutFormatError(msg)
            written.append(target)
    return written
