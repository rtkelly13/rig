"""Freeze image_drift's scores into the C# test oracle (rig issue #324, ADR 0024).

Run from the repository root, with Playwright's headless shell for the revision the .NET Playwright package pins:

    cd tools/image-drift && uv run --locked python -I oracle/dump_oracle.py \
        --out ../../tests/Rig.Image.Tests/Oracle \
        --regions-out ../../src/Rig.Image/feature-regions.json \
        --chromium <path to chrome-headless-shell>

With `--simplifier-out <dir>` it instead freezes the SVG simplifier's outputs (rig issue #325) as
`simplifier-oracle.json`, needs no Chromium and touches none of the score-oracle files:

    cd tools/image-drift && uv run --locked python -I oracle/dump_oracle.py \\
        --simplifier-out ../../tests/Rig.Image.Tests/SimplifierOracle

Without it, the generator renders every brand master and cut with image_drift's own HTML wrapper, scores them
with image_drift's own metrics and promotion gate, builds the ICO the way production_cuts.py does,
and writes the numbers as
`score-oracle.json` beside the PNGs and candidate SVGs they came from. The C# tests read those files and never
call Python. The output is deterministic for a given machine, so a second run diffs clean.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import platform
import shutil
import sys
import tempfile
from dataclasses import asdict
from importlib import metadata
from pathlib import Path
from typing import Any

import numpy as np
from PIL import Image
from playwright.sync_api import Browser, sync_playwright

from image_drift import labs_builder, labs_parametric, pathways
from image_drift.builder_contract import FeatureRegion
from image_drift.cli_base import feature_boxes
from image_drift.diagnostics import analyze_decomposed_drift
from image_drift.diamond_parquet_builder import FEATURE_REGIONS as DIAMOND_REGIONS
from image_drift.edge_regularity import regularize_svg
from image_drift.metrics import CandidateScore, DriftResult, compute_drift, score_candidate
from image_drift.production_cuts import ICO_SIZES
from image_drift.promotion import evaluate_promotion
from image_drift.simplifier import (
    SVGPathTokenizer,
    count_svg_path_nodes,
    parse_path_d,
    quantize_svg_coordinates,
    run_scour_pass,
    simplify_path_geometry,
    simplify_svg_document,
)
from image_drift.simplifier.ast_parser import PathCommand

ROOT = Path.cwd().parent.parent
NORMALISED = 500
MASTER_SIZES = (500, 180, 1000, 2000)
FAVICON_SIZES = (500, 512, 48, 32, 16)
# Renders at or below this size are checked in as PNGs; larger ones are recorded by hash only.
MAX_CHECKED_IN_SIZE = 500

PROJECTS: dict[str, dict[str, Any]] = {
    "rtkelly-labs": {
        "regions": labs_builder.FEATURE_REGIONS,
        "master": "assets/rtkelly-labs/production-cuts/rtkelly-labs.svg",
        "favicon": "assets/rtkelly-labs/production-cuts/favicon.svg",
        "refs": ["rtkelly-labs.png", "02-smooth-crossover-baseline.jpg"],
    },
    "parquet-source-generator": {
        "regions": DIAMOND_REGIONS,
        "master": "assets/parquet-source-generator/production-cuts/parquet-diamond.svg",
        "favicon": "assets/parquet-source-generator/production-cuts/favicon.svg",
        "refs": ["parquet-diamond.png", "02-simplified-cad-baseline.jpg"],
    },
}


def _html(svg: Path, size: int) -> str:
    # Identical to image_drift.imaging.render_svg_async.
    return f"""
    <!DOCTYPE html>
    <html>
    <body style="margin:0; padding:0; background:transparent; display:flex;
                 justify-content:center; align-items:center;">
        <img src="file://{svg.resolve()}" style="width:{size}px; height:{size}px;" />
    </body>
    </html>
    """


def _render(browser: Browser, svg: Path, png: Path, size: int, scratch: Path) -> None:
    png.parent.mkdir(parents=True, exist_ok=True)
    html = scratch / f"_{png.stem}.html"
    html.write_text(_html(svg, size), encoding="utf-8")
    page = browser.new_page(viewport={"width": size, "height": size})
    page.goto(f"file://{html.resolve()}")
    page.screenshot(path=png, omit_background=True)
    page.close()
    html.unlink()


def _display(path: Path, out: Path) -> str:
    """A repository-relative path for masters, an oracle-relative one for frozen candidates."""
    base = out if path.is_relative_to(out) else ROOT
    return str(path.relative_to(base)).replace("\\", "/")


def _sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def _drift(reference: Path, render: Path) -> dict[str, Any]:
    drift: DriftResult = compute_drift(reference, render)
    report = analyze_decomposed_drift(reference, render)
    return {
        "rmse_pct": drift.rmse_pct,
        "ssim": drift.ssim,
        "psnr_db": drift.psnr_db,
        "grid": [
            {
                "row": cell.row,
                "column": cell.column,
                "error": cell.error,
                "row_start": cell.bounds.row_start,
                "row_end": cell.bounds.row_end,
                "col_start": cell.bounds.col_start,
                "col_end": cell.bounds.col_end,
            }
            for cell in drift.grid
        ],
        "luminance_rmse_pct": report.luminance_rmse_pct,
        "chrominance_rmse_pct": report.chrominance_rmse_pct,
        "color_to_geometry_ratio": report.color_to_geometry_ratio,
        "edge_correlation": report.edge_correlation,
        "boundary_dilation_bias": report.boundary_dilation_bias,
        "hotspots": [asdict(spot) for spot in report.hotspots],
        "recommendations": report.recommendations,
    }


def _score(score: CandidateScore) -> dict[str, Any]:
    return {
        "composite_pct": score.composite_pct,
        "full_frame_pct": score.full_frame_pct,
        "per_feature_pct": score.per_feature_pct,
    }


def _normalised_hash(reference: Path) -> tuple[str, Image.Image]:
    image = Image.open(reference).convert("RGB").resize((NORMALISED, NORMALISED))
    return hashlib.sha256(image.tobytes()).hexdigest(), image


def _ico(out: Path, source_png: Path, project: str) -> dict[str, Any]:
    """The favicon.ico production_cuts.py writes: the 48 px favicon render saved with sizes 48, 32 and 16."""
    target = out / "ico" / f"{project}.favicon.ico"
    target.parent.mkdir(parents=True, exist_ok=True)
    with Image.open(source_png) as image:
        image.save(target, format="ICO", sizes=[(size, size) for size in ICO_SIZES if size <= image.width])
    entries: list[dict[str, Any]] = []
    with Image.open(target) as icon:
        for width, height in sorted(icon.ico.sizes()):  # type: ignore[attr-defined]
            frame = icon.ico.getimage((width, height)).convert("RGBA")  # type: ignore[attr-defined]
            entries.append(
                {"size": width, "rgba_sha256": hashlib.sha256(frame.tobytes()).hexdigest(), "height": height}
            )
    return {"file": str(target.relative_to(out)).replace("\\", "/"), "source": source_png.name, "entries": entries}


SIMPLIFIER_INPUTS = (
    "assets/rtkelly-labs/production-cuts/favicon.svg",
    "assets/rtkelly-labs/production-cuts/rtkelly-labs.svg",
    "assets/parquet-source-generator/production-cuts/favicon.svg",
    "assets/parquet-source-generator/production-cuts/parquet-diamond.svg",
    "tests/Rig.Image.Tests/Oracle/candidates/rtkelly-labs.regularized.svg",
    "tests/Rig.Image.Tests/Oracle/candidates/rtkelly-labs.parametric.svg",
    "tests/Rig.Image.Tests/Oracle/candidates/parquet-source-generator.regularized.svg",
    "tests/Rig.Image.Tests/Oracle/candidates/rtkelly-labs.autotraced.svg",
    "tests/Rig.Image.Tests/Oracle/candidates/parquet-source-generator.autotraced.svg",
)
SIMPLIFIER_TOLERANCES = (0.5, 0.25, 0.125, 0.0625)
# Documents longer than this are recorded by hash and length; the rest are stored as text.
SIMPLIFIER_TEXT_LIMIT = 20000
SIMPLIFIER_NUMBERS = (
    0.0,
    -0.0,
    0.125,
    0.375,
    0.625,
    2.5,
    10.25,
    -0.04,
    -0.05,
    0.005,
    0.015,
    2.675,
    1e-7,
    100.0,
    1234.5678,
    0.1,
    0.35,
    1.005,
    -1.5,
    3.0,
    12.34567,
    1e21,
    0.995,
    999.995,
)
SIMPLIFIER_PATHS = (
    "",
    "   ",
    "M 0 0 L 10 10",
    "M0 0L10 0 10 10 0 10z",
    "m 1 2 3 4 5 6",
    "10 20 30 40",
    "M10-20.5L.5.25e2Z",
    "M 0 0 c 10 0 20 0 30 0",
    "M 0 0 C 10 0 20 0 30 0",
    "M0 0 L1",
    "M0 0 L1 2 3",
    "H5V6h-1v-2",
    "z",
    "M1 1 Z L5 5",
    "a1 1 0 011 1",
    "M0 0 A 5 5 0 1 1 10 0",
    "M0 0 a5 5 45 0 0 10 0",
    "M0 0 Q 5 5 10 0 T 20 0",
    "M0 0 S 1 2 3 4",
    "M1.23456 2.5e1 L-0.001 0.004",
    "x M 3 4 y L 5 6",
    "M 0 0 L 1,2,3,4",
    "M +1 +2 L -.5 -.5",
    "M 5 5 m 1 1 l 2 2 L 3 3 Z m 1 1",
)
SIMPLIFIER_GEOMETRY = (
    ("M 0 0 L 10 0 L 10 10 L 0 10 Z", "square"),
    ("M 0 0 L 5 0.1 L 10 0 L 10 10 L 0 10 Z", "nearly flat edge"),
    ("M 0 0 L 10 10 L 10 0 L 0 10 Z", "bow-tie"),
    ("M 0 0 L 10 0 L 0 0 Z", "degenerate triangle"),
    ("M 0 0 L 10 0 Z", "two coordinates closed"),
    ("M 0 0 L 10 0", "two coordinates open"),
    ("M 0 0 L 5 0.2 L 10 0", "open flat"),
    ("M 0 0 L 5 3 L 10 0 L 15 3 L 20 0", "open zigzag"),
    ("M 0 0 L 0 0 L 0 0 Z", "all duplicates"),
    ("M0 0 L 100 0 L 100 100 L 0 100 Z M 20 20 L 20 80 L 80 80 L 80 20 Z", "square with hole"),
    ("M0 0 L 10 0 L 10 10 L 0 10 Z M 20 20 L 30 20 L 30 30 L 20 30 Z", "two squares"),
    ("M 0 0 h 10 v 10 h -10 z", "relative square"),
    ("M 10 10 L 20 10 L 20 20 Z m 5 5 l 10 0 l 0 10 z", "relative move after close"),
    ("M 0 0 C 0 0.1 10 0.1 10 0 L 10 10", "flat cubic"),
    ("M 0 0 c 0 0.1 10 0.1 10 0 l 0 10", "flat relative cubic"),
    ("M 0 0 C 0 5 10 5 10 0 L 10 10 L 10.01 10 Z", "curved cubic, tiny line"),
    ("M 0 0 A 5 5 0 0 1 10 0 L 10 10 Z", "arc"),
    ("M 0 0 Q 5 5 10 0 T 20 0 S 1 2 3 4", "quadratics"),
    ("M 0 0 L 100 0 L 100 100 L 50 101 L 0 100 Z", "rounded-off edge"),
    ("M 0 0 L 3 0 L 3 3 L 2 3 L 2 1 L 1 1 L 1 3 L 0 3 Z", "notch"),
    ("M 5 5 H 10 V 10 H 5 Z", "absolute hv square"),
    ("M 5 5 h 10 v 10 h -10 Z m 2 2 h 1 v 1 h -1 z", "relative hv with inner"),
    ("M 0 0 L 4 0.3 L 8 0 L 12 -0.3 L 16 0 L 16 8 L 0 8 Z", "wavy top"),
    ("M 0.123456 0.654321 L 9.87654 0.1 L 9.9 9.9 L 0.2 9.95 Z", "fractional coordinates"),
    ("not a path", "no commands"),
    ("", "empty"),
)
SIMPLIFIER_GEOMETRY_TOLERANCES = (0.0, 0.1, 0.5, 1.0, 5.0)


def _text_or_hash(text: str) -> dict[str, Any]:
    entry: dict[str, Any] = {"sha256": hashlib.sha256(text.encode("utf-8")).hexdigest(), "length": len(text)}
    if len(text) <= SIMPLIFIER_TEXT_LIMIT:
        entry["text"] = text
    return entry


def _synthetic_documents() -> list[tuple[str, str]]:
    ns = 'xmlns="http://www.w3.org/2000/svg"'
    return [
        (
            "title-desc-metadata",
            (
                f'<svg {ns} viewBox="0 0 24 24"><title>t</title><desc>d</desc><metadata>m</metadata>'
                f'<rect width="4" height="4"/></svg>'
            ),
        ),
        ("comments", f'<svg {ns} viewBox="0 0 24 24"><!-- one --><rect width="4" height="4"/><!-- two --></svg>'),
        (
            "prolog",
            (
                f'<?xml version="1.0" encoding="UTF-8"?>\n<svg {ns} viewBox="0 0 24 24">'
                f'<rect width="4" height="4"/></svg>'
            ),
        ),
        (
            "editor-namespaces",
            (
                f'<svg {ns} xmlns:inkscape="http://www.inkscape.org/namespaces/inkscape" '
                f'xmlns:sodipodi="http://sodipodi.sourceforge.net/DTD/sodipodi-0.dtd" viewBox="0 0 24 '
                f'24" inkscape:version="1.0">'
                f'<sodipodi:namedview id="nv"/><rect width="4" height="4" inkscape:label="r"/></svg>'
            ),
        ),
        (
            "unused-xlink",
            (
                f'<svg {ns} xmlns:xlink="http://www.w3.org/1999/xlink" viewBox="0 0 24 24">'
                f'<rect width="4" height="4"/></svg>'
            ),
        ),
        (
            "used-xlink",
            (
                f'<svg {ns} xmlns:xlink="http://www.w3.org/1999/xlink" viewBox="0 0 24 24"><defs>'
                f'<rect id="a" width="4" height="4"/></defs><use xlink:href="#a"/></svg>'
            ),
        ),
        (
            "colors",
            (
                f'<svg {ns} viewBox="0 0 24 24">'
                f'<rect width="4" height="4" fill="#ffffff" stroke="rgb(255,0,0)"/>'
                f'<circle r="2" fill="RED" stroke="#00ff00"/>'
                f'<rect width="1" height="1" fill="rgb(100%,0%,0%)" stroke="#aabbcc"/></svg>'
            ),
        ),
        (
            "unreferenced-defs",
            (
                f'<svg {ns} viewBox="0 0 24 24"><defs><linearGradient id="g1">'
                f'<stop offset="0" stop-color="#000"/></linearGradient><linearGradient id="g2">'
                f'<stop offset="0" stop-color="#fff"/></linearGradient></defs>'
                f'<rect width="4" height="4" fill="url(#g2)"/></svg>'
            ),
        ),
        ("empty-containers", f'<svg {ns} viewBox="0 0 24 24"><defs/><g/><g><g/></g><rect width="4" height="4"/></svg>'),
        (
            "lengths",
            (
                f'<svg {ns} width="100.0px" height="100.00" viewBox="0.0 0 24.000 24">'
                f'<rect x="1.0" y="2.50" width="4.000" height="4.12345678" rx="0.5"/>'
                f'<circle cx="10.0" cy="10.0" r="3.0"/></svg>'
            ),
        ),
        (
            "default-attributes",
            (
                f'<svg {ns} viewBox="0 0 24 24"><rect x="0" y="0" width="4" height="4"/>'
                f'<circle cx="0" cy="0" r="2"/></svg>'
            ),
        ),
        (
            "attribute-order",
            (
                f'<svg {ns} viewBox="0 0 24 24">'
                f'<rect stroke="#000" fill="#fff" height="4" width="4" y="1" x="1" id="r" class="c" '
                f'transform="translate(1 2)"/>'
                f"</svg>"
            ),
        ),
        ("quotes", f'<svg {ns} viewBox="0 0 24 24"><text x="1" y="1" font-family=\'Foo "Bar"\'>hi</text></svg>'),
        ("text-whitespace", f'<svg {ns} viewBox="0 0 24 24"><text x="1" y="1">  hello   world  </text></svg>'),
        (
            "nested-children",
            (
                f'<svg {ns} viewBox="0 0 24 24"><g id="a"><rect id="b" width="4" height="4"/>'
                f'<circle id="c" r="2" cx="3" cy="3"/></g></svg>'
            ),
        ),
        (
            "gradient-defaults",
            (
                f'<svg {ns} viewBox="0 0 24 24"><defs>'
                f'<linearGradient id="g" x1="0%" y1="0%" x2="100%" y2="0%" gradientUnits="objectBoundingBox">'
                f'<stop offset="50%" stop-color="#000"/><stop offset="100%" stop-color="#fff"/>'
                f'</linearGradient></defs><rect width="4" height="4" fill="url(#g)"/></svg>'
            ),
        ),
        ("no-namespace-root", '<svg viewBox="0 0 24 24"><rect width="4" height="4"/></svg>'),
        ("malformed", "<svg><unclosed></svg>"),
        ("not-xml", "plain text"),
        ("path-only", f'<svg {ns} viewBox="0 0 24 24"><path d="M 1.234 2.345 L 3 4"/></svg>'),
        (
            "style-attribute",
            (
                f'<svg {ns} viewBox="0 0 24 24">'
                f'<rect width="4" height="4" style="fill:red;stroke:#ff0000;opacity:1.00000;stroke-width:2.0;bogus"/>'
                f"</svg>"
            ),
        ),
        (
            "style-empty-after-defaults",
            (f'<svg {ns} viewBox="0 0 24 24"><rect width="4" height="4" style="opacity:1;display:inline"/></svg>'),
        ),
        (
            "colors-more",
            (
                f'<svg {ns} viewBox="0 0 24 24"><defs><linearGradient id="g">'
                f'<stop offset="0" stop-color="RGB(10%,20%,30%)"/>'
                f'<stop offset="1" stop-color="rgb( 1, 2, 3 )"/></linearGradient></defs><a>'
                f'<rect width="4" height="4" fill="url(#g)" stroke="#FFFFFF"/></a>'
                f'<ellipse rx="1" ry="1" fill="none" stroke="blue"/><g fill="  #AABBCC "/>'
                f'<line x1="1" x2="2" stroke="rgb(300,0,0)"/>'
                f'<rect width="1" height="1" fill="#abcdef" stroke="darkslategray"/></svg>'
            ),
        ),
        (
            "prefixed-svg-namespace",
            (
                '<svg:svg xmlns:svg="http://www.w3.org/2000/svg" viewBox="0 0 24 24">'
                '<svg:rect width="4" height="4"/></svg:svg>'
            ),
        ),
        (
            "url-forms",
            (
                f'<svg {ns} xmlns:xlink="http://www.w3.org/1999/xlink" viewBox="0 0 24 24"><defs>'
                f'<linearGradient id="a"/><linearGradient id="b"><stop offset="1"/></linearGradient>'
                f'<filter id="c"/><clipPath id="d"/><mask id="e"/><marker id="f"/>'
                f'<linearGradient id="unused"/><font id="keep"/><g id="grp"><rect id="inner"/></g><g>'
                f'<rect id="inner2"/></g><symbol id="s"/></defs>'
                f'<rect width="4" height="4" fill="url(\'#a\')" stroke=\'url("#b")\' '
                f'style="filter:url(#c)" clip-path="url(#d)" mask="url(#e)" marker-end="url(#f)"/>'
                f'<use xlink:href="#s"/></svg>'
            ),
        ),
        (
            "url-edge",
            (
                f'<svg {ns} viewBox="0 0 24 24"><defs><linearGradient id="a"/><linearGradient id="b"/>'
                f'</defs><rect width="4" height="4" fill="url(#a" stroke="url(#b)x"/></svg>'
            ),
        ),
        (
            "unreferenced-outside-defs",
            (
                f'<svg {ns} viewBox="0 0 24 24"><linearGradient id="u1"/><radialGradient id="u2"/>'
                f'<pattern id="u3"/><linearGradient id="u4"/>'
                f'<rect width="4" height="4" fill="url(#u4)"/></svg>'
            ),
        ),
        (
            "nested-groups",
            (
                f'<svg {ns} viewBox="0 0 24 24"><g><rect width="4" height="4"/></g><g id="k"><g>'
                f'<circle r="1"/></g></g><switch><g><rect width="1" height="1"/></g></switch><g>'
                f'<title>t</title><rect width="2" height="2"/></g><g><desc/>'
                f'<rect width="3" height="3"/></g></svg>'
            ),
        ),
        (
            "default-properties",
            (
                f'<svg {ns} viewBox="0 0 24 24"><g opacity=".5">'
                f'<rect opacity="1" width="4" height="4"/></g>'
                f'<rect opacity="1" style="visibility:visible;display:inline;fill:red" width="1" '
                f'height="1" stroke-linecap="butt"/>'
                f'<g marker-start="url(#m)">'
                f'<rect marker-end="none" marker="none" width="2" height="2"/></g><g marker="url(#m)">'
                f'<rect marker-start="none" width="2" height="2"/></g><g style="stroke-width:3">'
                f'<rect style="stroke-width:1" width="2" height="2"/></g></svg>'
            ),
        ),
        (
            "default-attributes-more",
            (
                f'<svg {ns} width="100%" height="100%" version="1.1" baseProfile="none" viewBox="0 0 '
                f'24 24" preserveAspectRatio="xMidYMid meet">'
                f"<defs>"
                f'<filter id="f" x="-10%" y="-10%" width="120%" height="120%" '
                f'filterUnits="objectBoundingBox" primitiveUnits="userSpaceOnUse">'
                f'<feGaussianBlur stdDeviation="0"/></filter>'
                f'<filter id="f2" x="-0.1" width="1.2" height="1.2" y="-.1"/>'
                f'<filter id="f3" gradientUnits="userSpaceOnUse" x="-0.1"/>'
                f'<radialGradient id="r" cx="50%" cy="50%" fx="50%" fy="50%" r="50%" spreadMethod="pad"/>'
                f'<radialGradient id="r2" cx="0.5" cy="0.5" fx="0.5" fy="0.4" r="0.5" gradientUnits="userSpaceOnUse"/>'
                f'<linearGradient id="l" x1="0" x2="100%" x2b="1" y2="0"/>'
                f'<linearGradient id="l2" x2="1"/>'
                f'<linearGradient id="l3" x2="1" gradientUnits="userSpaceOnUse"/>'
                f'<marker id="m" markerUnits="strokeWidth" refX="0" refY="0" markerWidth="3" '
                f'markerHeight="3" orient="0"/>'
                f"</defs>"
                f'<rect fill="url(#r)" filter="url(#f)" mask="url(#f2)" stroke="url(#f3)" '
                f'clip-path="url(#r2)" marker-start="url(#m)" marker-mid="url(#l)" '
                f'marker-end="url(#l2)" width="4" height="4" x="0px" y="0em"/>'
                f'<use xlink:href="#l3" x="0" y="0"/><feX/></svg>'
            ),
        ),
        (
            "lengths-more",
            (
                f'<svg {ns} viewBox="0,0,24.50,24">'
                f'<rect width="4.5em" height="2e1" x="1e-2" y="5%" rx="abc" ry="+3"/>'
                f'<rect width=".5" height="0.0" x="-0.5" y="1.50000" rx="100000" ry="0.0001"/>'
                f'<circle r="1E3" cx="12345.6789" cy="1.0e+3"/><ellipse rx="1e-7" ry="-0"/></svg>'
            ),
        ),
        ("viewbox-short", f'<svg {ns} viewBox="0 0 24"><rect width="4" height="4"/></svg>'),
        ("viewbox-padded", f'<svg {ns} viewBox="  0  0 24.0  24.0 "><rect width="4" height="4"/></svg>'),
        (
            "precision-attributes",
            (
                f'<svg {ns} viewBox="0 0 24 24">'
                f'<rect width="4" height="4" opacity="0.50000" stroke-width="2.000px" '
                f'font-size="12.0pt" stroke-miterlimit="4.00" fill-opacity="bad" '
                f'letter-spacing="1.0em" stroke-dashoffset="0.0"/>'
                f'<rect width="1" height="1" '
                f'style="opacity:0.5000;stroke-width:3.00;font-size:abc;stroke-opacity:.50"/>'
                f"</svg>"
            ),
        ),
        (
            "cdata-and-preserve",
            (
                f'<svg {ns} viewBox="0 0 24 24"><style><![CDATA[a>b{{fill:red}}]]></style>'
                f'<text xml:space="preserve">  a  <tspan> b </tspan> </text>'
                f'<text xml:space="default"> c </text><script><![CDATA[x<y]]></script></svg>'
            ),
        ),
        (
            "entities",
            (
                f'<svg {ns} viewBox="0 0 24 24"><text>a &amp; b &lt; c &gt; d</text>'
                f'<rect width="4" height="4" aria-label="a &amp; b" data-q="it\'s &quot;q&quot;" '
                f'data-d="say &quot;hi&quot;"/>'
                f'<rect width="1" height="1" data-m="a\'b&quot;c&quot;&quot;"/></svg>'
            ),
        ),
        ("empty-path-elements", f'<svg {ns} viewBox="0 0 24 24"><path/><path d=""/><rect width="4" height="4"/></svg>'),
        (
            "gradient-stops-more",
            (
                f'<svg {ns} viewBox="0 0 24 24"><defs><linearGradient id="g"><stop stop-color="red"/>'
                f'<stop offset="0%" stop-color="red"/><stop offset="0.5" stop-color="blue"/>'
                f'<stop offset="50%" stop-color="green"/>'
                f'<stop offset="50%" stop-color="green" stop-opacity=".5"/><stop offset="1e-5"/>'
                f'<stop offset="10px"/></linearGradient><radialGradient id="h"><stop offset="25%"/>'
                f'<stop offset="25%"/></radialGradient></defs>'
                f'<rect width="4" height="4" fill="url(#g)" stroke="url(#h)"/></svg>'
            ),
        ),
        (
            "mixed-content",
            (
                f'<svg {ns} viewBox="0 0 24 24"><text>a<tspan>b</tspan>c <tspan>d</tspan> e</text>'
                f'<g>x<rect width="1" height="1"/>y</g><text>\n\t tabbed\n text \n</text></svg>'
            ),
        ),
        ("size-small", f'<svg {ns} width="20" height="20"><path d="M 1.23456 2.34567 L 3.45678 4.56789"/></svg>'),
        (
            "size-medium",
            (f'<svg {ns} width="100px" height="100px"><path d="M 1.23456 2.34567 L 3.45678 4.56789"/></svg>'),
        ),
        ("size-large", f'<svg {ns} width="400" height="300"><path d="M 1.23456 2.34567 L 3.45678 4.56789"/></svg>'),
        (
            "size-unparsable",
            (f'<svg {ns} width="abc" height="def"><path d="M 1.23456 2.34567 L 3.45678 4.56789"/></svg>'),
        ),
        ("size-missing", f'<svg {ns}><path d="M 1.23456 2.34567 L 3.45678 4.56789"/></svg>'),
        ("size-percent", f'<svg {ns} width="100%" height="50%"><path d="M 1.23456 2.34567 L 3.45678 4.56789"/></svg>'),
        (
            "namespaced-attributes",
            (
                f'<svg {ns} xmlns:x="urn:example" xmlns:xlink="http://www.w3.org/1999/xlink" viewBox="0 0 24 24">'
                f'<rect width="4" height="4" x:flag="1"/><a xlink:href="#r" xlink:title="t"/>'
                f'<rect id="r" width="1" height="1"/></svg>'
            ),
        ),
        ("xml-lang", f'<svg {ns} xml:lang="en" viewBox="0 0 24 24"><text xml:space="preserve"> a </text></svg>'),
        (
            "unused-prefix-prefix-match",
            (
                f'<svg {ns} xmlns:a="urn:a" xmlns:ab="urn:b" viewBox="0 0 24 24">'
                f'<rect width="4" height="4" ab:x="1"/></svg>'
            ),
        ),
        *_scour_path_documents(ns),
    ]


def _scour_path_documents(ns: str) -> list[tuple[str, str]]:
    """Documents for the scour passes that rewrite path data, points, transforms, gradients and groups."""
    xl = 'xmlns:xlink="http://www.w3.org/1999/xlink"'
    paths = {
        "path-relative": "M 10 10 L 20 10 L 20 20 L 10 20 Z",
        "path-lines": "M10,10 L20,10 L30,10 L30,20 L30,30 M 5 5 l 0 0 l 3 4",
        "path-curves": "M 0 0 C 10 0 20 10 30 10 C 40 10 50 0 60 0 S 80 20 90 10 c 5 5 10 5 15 0",
        "path-straight-curve": "M 0 0 C 10 10 20 20 30 30 C 40 40 50 50 60 60",
        "path-quadratics": "M 0 0 Q 10 10 20 0 T 40 0 Q 50 10 60 0 q 5 5 10 0 t 10 0",
        "path-arcs": "M 0 0 A 10 10 0 0 1 20 0 a 10 10 0 1 0 20 0 A5 5 0 01 60 10",
        "path-horizontal-vertical": "M 0 0 H 10 H 20 V 10 V 20 h 5 h 5 v 5 v 5 z",
        "path-implicit-lineto": "M 0 0 10 10 20 20 30 30",
        "path-implicit-relative": "m 1 1 2 2 3 3 4 4",
        "path-multiple-subpaths": "M 0 0 L 10 10 Z M 20 20 L 30 30 Z m 5 5 l 1 1",
        "path-closed-noops": "M 0 0 L 10 0 L 10 10 L 0 0 Z",
        "path-empty-segments": "M 1 1 l 0 0 h 0 v 0 c 0 0 0 0 0 0 z",
        "path-same-direction": "M 0 0 l 10 0 l 20 0 l -5 0 l -5 0 l 0 5 l 0 10 l 0 -3",
        "path-exponent": "M 1e2 1E-2 L 1.5e1 2.5e-1 l .5 .5 l -.5 -.5",
        "path-precision": "M 1.23456789 2.34567891 L 3.45678912 4.56789123 l 0.00001234 100000.5",
        "path-compact-numbers": "M0-5L-1.5.5-2.5-.5",
        "path-shorter-kept": "M 0.5 0.5 L 0.5 0.5",
    }
    documents = [(name, f'<svg {ns} viewBox="0 0 24 24"><path d="{d}"/></svg>') for name, d in paths.items()]
    documents += [
        ("path-empty-d", f'<svg {ns} viewBox="0 0 24 24"><path d=""/><path/></svg>'),
        ("polygon-points", f'<svg {ns} viewBox="0 0 24 24"><polygon points="1,1 2.5,2.5 3,4.123456789"/></svg>'),
        ("polyline-points", f'<svg {ns} viewBox="0 0 24 24"><polyline points="10 10 20 10 30 10.00001"/></svg>'),
        ("polygon-empty", f'<svg {ns} viewBox="0 0 24 24"><polygon points=""/><polyline points="1"/></svg>'),
        ("polygon-odd-count", f'<svg {ns} viewBox="0 0 24 24"><polygon points="1 2 3"/></svg>'),
        ("polygon-exponent", f'<svg {ns} viewBox="0 0 24 24"><polygon points="1e1,2e-1 .5,-.5"/></svg>'),
        (
            "transform-translate",
            (f'<svg {ns} viewBox="0 0 24 24"><g transform="translate(5 0)"><rect width="1" height="1"/></g></svg>'),
        ),
        (
            "transform-translate-run",
            f'<svg {ns} viewBox="0 0 24 24"><g transform="translate(5) translate(3 4) translate(-8 -4)">'
            f'<rect width="1" height="1"/></g></svg>',
        ),
        (
            "transform-identity",
            (f'<svg {ns} viewBox="0 0 24 24"><g transform="translate(0 0)"><rect width="1" height="1"/></g></svg>'),
        ),
        (
            "transform-scale",
            (
                f'<svg {ns} viewBox="0 0 24 24"><g transform="scale(2 2) scale(3)">'
                f'<rect width="1" height="1"/></g></svg>'
            ),
        ),
        (
            "transform-scale-identity",
            (
                f'<svg {ns} viewBox="0 0 24 24"><g transform="scale(2) scale(0.5)">'
                f'<rect width="1" height="1"/></g></svg>'
            ),
        ),
        (
            "transform-scale-mixed",
            (
                f'<svg {ns} viewBox="0 0 24 24"><g transform="scale(2) scale(3 4) scale(5 6) scale(2)">'
                f'<rect width="1" height="1"/></g></svg>'
            ),
        ),
        (
            "transform-rotate",
            (
                f'<svg {ns} viewBox="0 0 24 24"><g transform="rotate(30) rotate(60 0 0) rotate(400)">'
                f'<rect width="1" height="1"/></g></svg>'
            ),
        ),
        (
            "transform-rotate-centre",
            (
                f'<svg {ns} viewBox="0 0 24 24"><g transform="rotate(30 5 5) rotate(300 5 5)">'
                f'<rect width="1" height="1"/></g></svg>'
            ),
        ),
        (
            "transform-rotate-wrap",
            (
                f'<svg {ns} viewBox="0 0 24 24"><g transform="rotate(-100)"><rect width="1" height="1"/></g>'
                f'<g transform="rotate(-720)"><rect width="1" height="1"/></g><g transform="rotate(280)">'
                f'<rect width="1" height="1"/></g></svg>'
            ),
        ),
        (
            "transform-skew-zero",
            (
                f'<svg {ns} viewBox="0 0 24 24"><g transform="skewX(0) skewY(0) rotate(0) translate(1 1)">'
                f'<rect width="1" height="1"/></g></svg>'
            ),
        ),
        (
            "transform-matrix-identity",
            (
                f'<svg {ns} viewBox="0 0 24 24"><g transform="matrix(1 0 0 1 0 0)">'
                f'<rect width="1" height="1"/></g></svg>'
            ),
        ),
        (
            "transform-matrix-translate",
            (
                f'<svg {ns} viewBox="0 0 24 24"><g transform="matrix(1 0 0 1 10 20)">'
                f'<rect width="1" height="1"/></g></svg>'
            ),
        ),
        (
            "transform-matrix-scale",
            (
                f'<svg {ns} viewBox="0 0 24 24"><g transform="matrix(2 0 0 3 0 0)">'
                f'<rect width="1" height="1"/></g></svg>'
            ),
        ),
        (
            "transform-matrix-rotate",
            (
                f'<svg {ns} viewBox="0 0 24 24">'
                f'<g transform="matrix(0.5 0.8660254037844386 -0.8660254037844386 0.5 0 0)">'
                f'<rect width="1" height="1"/></g></svg>'
            ),
        ),
        (
            "transform-matrix-rotate-back",
            (
                f'<svg {ns} viewBox="0 0 24 24">'
                f'<g transform="matrix(-0.5 0.8660254037844386 -0.8660254037844386 -0.5 0 0)">'
                f'<rect width="1" height="1"/></g></svg>'
            ),
        ),
        (
            "transform-matrix-rotate-neg",
            (
                f'<svg {ns} viewBox="0 0 24 24">'
                f'<g transform="matrix(-0.5 -0.8660254037844386 0.8660254037844386 -0.5 0 0)">'
                f'<rect width="1" height="1"/></g></svg>'
            ),
        ),
        (
            "transform-matrix-general",
            (
                f'<svg {ns} viewBox="0 0 24 24"><g transform="matrix(1 2 3 4 5 6)">'
                f'<rect width="1" height="1"/></g></svg>'
            ),
        ),
        (
            "transform-pattern-gradient",
            (
                f'<svg {ns} viewBox="0 0 24 24"><defs><pattern id="p" patternTransform="translate(0 0)"/>'
                f'<linearGradient id="g" gradientTransform="translate(1 2) translate(2 3)"/></defs>'
                f'<rect fill="url(#p)" stroke="url(#g)" width="1" height="1"/></svg>'
            ),
        ),
        (
            "transform-longer-kept",
            (f'<svg {ns} viewBox="0 0 24 24"><g transform="translate(1,2)"><rect width="1" height="1"/></g></svg>'),
        ),
        (
            "transform-nested",
            (
                f'<svg {ns} viewBox="0 0 24 24"><g transform="translate(0 0)"><g transform="scale(1 1)">'
                f'<rect width="1" height="1" transform="rotate(360)"/></g></g></svg>'
            ),
        ),
        (
            "transform-exponent",
            (
                f'<svg {ns} viewBox="0 0 24 24"><g transform="translate(1e1 2e-1) scale(.5)">'
                f'<rect width="1" height="1"/></g></svg>'
            ),
        ),
        (
            "transform-invalid",
            (f'<svg {ns} viewBox="0 0 24 24"><g transform="translate(1 2"><rect width="1" height="1"/></g></svg>'),
        ),
        (
            "transform-unknown",
            (f'<svg {ns} viewBox="0 0 24 24"><g transform="wobble(1)"><rect width="1" height="1"/></g></svg>'),
        ),
        (
            "gradient-collapse",
            (
                f'<svg {ns} {xl} viewBox="0 0 24 24"><defs><linearGradient id="a"><stop offset="0" stop-color="#f00"/>'
                f'<stop offset="1" stop-color="#00f"/></linearGradient>'
                f'<linearGradient id="b" xlink:href="#a" x1="0" x2="1"/></defs>'
                f'<rect fill="url(#b)" width="4" height="4"/></svg>'
            ),
        ),
        (
            "gradient-collapse-attributes",
            (
                f'<svg {ns} {xl} viewBox="0 0 24 24"><defs>'
                f'<radialGradient id="a" cx="1" cy="2" r="3" fx="1" fy="2"'
                f' gradientUnits="userSpaceOnUse" spreadMethod="reflect" gradientTransform="scale(2)">'
                f'<stop offset="0" stop-color="#f00"/><stop offset="1" stop-color="#00f"/></radialGradient>'
                f'<radialGradient id="b" xlink:href="#a" r="9"/></defs>'
                f'<rect fill="url(#b)" width="4" height="4"/></svg>'
            ),
        ),
        (
            "gradient-collapse-chain",
            (
                f'<svg {ns} {xl} viewBox="0 0 24 24"><defs><linearGradient id="a">'
                f'<stop offset="0" stop-color="#f00"/></linearGradient>'
                f'<linearGradient id="b" xlink:href="#a"/><linearGradient id="c" xlink:href="#b" x1="1"/></defs>'
                f'<rect fill="url(#c)" width="4" height="4"/></svg>'
            ),
        ),
        (
            "gradient-collapse-kind-mismatch",
            (
                f'<svg {ns} {xl} viewBox="0 0 24 24"><defs><linearGradient id="a" x1="1" cx="5">'
                f'<stop offset="0" stop-color="#f00"/></linearGradient>'
                f'<radialGradient id="b" xlink:href="#a"/></defs><rect fill="url(#b)" width="4" height="4"/></svg>'
            ),
        ),
        (
            "gradient-collapse-own-stops",
            (
                f'<svg {ns} {xl} viewBox="0 0 24 24"><defs><linearGradient id="a">'
                f'<stop offset="0" stop-color="#f00"/></linearGradient>'
                f'<linearGradient id="b" xlink:href="#a"><stop offset="1" stop-color="#0f0"/></linearGradient></defs>'
                f'<rect fill="url(#b)" width="4" height="4"/></svg>'
            ),
        ),
        (
            "gradient-collapse-no-href",
            (
                f'<svg {ns} {xl} viewBox="0 0 24 24"><defs><linearGradient id="a">'
                f'<stop offset="0" stop-color="#f00"/></linearGradient>'
                f'<linearGradient id="b" xlink:href="#a"/></defs><rect fill="url(#b)" width="4" height="4"/></svg>'
            ),
        ),
        (
            "gradient-collapse-forwarded-href",
            (
                f'<svg {ns} {xl} viewBox="0 0 24 24"><defs><linearGradient id="a">'
                f'<stop offset="0" stop-color="#f00"/></linearGradient>'
                f'<linearGradient id="m" xlink:href="#a"/><linearGradient id="b" xlink:href="#m" x1="1"/>'
                f'<linearGradient id="c" xlink:href="#m" x1="2"/></defs>'
                f'<rect fill="url(#b)" stroke="url(#c)" width="4" height="4"/></svg>'
            ),
        ),
        (
            "gradient-dedupe",
            (
                f'<svg {ns} viewBox="0 0 24 24"><defs>'
                f'<linearGradient id="a" x1="0" x2="1"><stop offset="0" stop-color="#f00"/>'
                f'<stop offset="1" stop-color="#00f"/></linearGradient>'
                f'<linearGradient id="b" x1="0" x2="1"><stop offset="0" stop-color="#f00"/>'
                f'<stop offset="1" stop-color="#00f"/></linearGradient>'
                f'<linearGradient id="c" x1="0" x2="1"><stop offset="0" stop-color="#f00"/>'
                f'<stop offset="1" stop-color="#00f"/></linearGradient>'
                f'</defs><rect fill="url(#a)" width="4" height="4"/>'
                f'<rect fill="url(#b)" stroke="url(\'#c\')" width="4" height="4"/>'
                f'<rect style="fill:url(&quot;#c&quot;);stroke:url(#b)" width="4" height="4"/></svg>'
            ),
        ),
        (
            "gradient-dedupe-radial",
            (
                f'<svg {ns} viewBox="0 0 24 24"><defs>'
                f'<radialGradient id="a" cx="1"><stop offset="0" stop-color="#f00"/></radialGradient>'
                f'<radialGradient id="b" cx="1"><stop offset="0" stop-color="#f00"/></radialGradient></defs>'
                f'<rect fill="url(#b)" width="4" height="4"/></svg>'
            ),
        ),
        (
            "gradient-dedupe-master-no-id",
            (
                f'<svg {ns} viewBox="0 0 24 24"><defs>'
                f'<linearGradient><stop offset="0" stop-color="#f00"/></linearGradient>'
                f'<linearGradient id="b"><stop offset="0" stop-color="#f00"/></linearGradient></defs>'
                f'<rect fill="url(#b)" width="4" height="4"/></svg>'
            ),
        ),
        (
            "gradient-dedupe-href",
            (
                f'<svg {ns} {xl} viewBox="0 0 24 24"><defs>'
                f'<linearGradient id="base"><stop offset="0" stop-color="#f00"/></linearGradient>'
                f'<linearGradient id="a" xlink:href="#base" x1="1"/><linearGradient id="b" xlink:href="#base" x1="1"/>'
                f'<linearGradient id="c" xlink:href="#a"/><linearGradient id="d" xlink:href="#b"/></defs>'
                f'<rect fill="url(#c)" stroke="url(#d)" width="4" height="4"/>'
                f'<rect fill="url(#a)" stroke="url(#b)" width="4" height="4"/></svg>'
            ),
        ),
        (
            "gradient-different-kept",
            (
                f'<svg {ns} viewBox="0 0 24 24"><defs>'
                f'<linearGradient id="a"><stop offset="0" stop-color="#f00"/></linearGradient>'
                f'<linearGradient id="b"><stop offset="0" stop-color="#00f"/></linearGradient></defs>'
                f'<rect fill="url(#a)" stroke="url(#b)" width="4" height="4"/></svg>'
            ),
        ),
        (
            "group-merge",
            (
                f'<svg {ns} viewBox="0 0 24 24"><g fill="red"><rect width="1" height="1"/></g><g fill="red">'
                f'<rect width="2" height="2"/></g>'
                f'<g fill="red"><rect width="3" height="3"/></g></svg>'
            ),
        ),
        (
            "group-merge-text-between",
            (
                f'<svg {ns} viewBox="0 0 24 24"><g fill="red"><rect width="1" height="1"/>'
                f'</g> text <g fill="red"><rect width="2" height="2"/></g></svg>'
            ),
        ),
        (
            "group-merge-different",
            (
                f'<svg {ns} viewBox="0 0 24 24"><g fill="red"><rect width="1" height="1"/></g><g fill="blue">'
                f'<rect width="2" height="2"/></g>'
                f'<g fill="blue"><rect width="3" height="3"/></g></svg>'
            ),
        ),
        (
            "group-merge-title",
            (
                f'<svg {ns} viewBox="0 0 24 24"><g fill="red"><rect width="1" height="1"/></g><g fill="red">'
                f'<title>t</title><rect width="2" height="2"/></g></svg>'
            ),
        ),
        (
            "group-merge-no-attributes",
            f'<svg {ns} viewBox="0 0 24 24"><g><rect width="1" height="1"/></g><g><rect width="2" height="2"/>'
            f"</g></svg>",
        ),
        (
            "group-merge-nested",
            (
                f'<svg {ns} viewBox="0 0 24 24"><g id="o"><g fill="red"><rect width="1" height="1"/></g>'
                f'<g fill="red"><rect width="2" height="2"/></g></g></svg>'
            ),
        ),
        (
            "group-merge-interrupted",
            (
                f'<svg {ns} viewBox="0 0 24 24"><g fill="red"><rect width="1" height="1"/></g>'
                f'<rect width="5" height="5"/>'
                f'<g fill="red"><rect width="2" height="2"/></g><g fill="red"><rect width="3" height="3"/></g></svg>'
            ),
        ),
        (
            "common-attributes",
            (
                f'<svg {ns} viewBox="0 0 24 24"><g><rect width="1" height="1" fill="red" stroke="blue"/>'
                f'<rect width="2" height="2" fill="red" stroke="green"/>'
                f'<circle r="3" fill="red" stroke="blue"/></g></svg>'
            ),
        ),
        (
            "common-attributes-referenced-child",
            (
                f'<svg {ns} viewBox="0 0 24 24"><defs><rect id="r" width="1" height="1" fill="red"/></defs><g>'
                f'<use href="#r" fill="red"/>'
                f'<rect width="2" height="2" fill="red"/><rect width="3" height="3" fill="red"/></g></svg>'
            ),
        ),
        (
            "common-attributes-text",
            f'<svg {ns} viewBox="0 0 24 24"><g> x <rect width="1" height="1" fill="red"/>'
            f'<rect width="2" height="2" fill="red"/></g></svg>',
        ),
        (
            "common-attributes-animation",
            (
                f'<svg {ns} viewBox="0 0 24 24"><g><rect width="1" height="1" fill="red"/>'
                f'<animate attributeName="x" fill="freeze"/>'
                f'<rect width="2" height="2" fill="red"/></g></svg>'
            ),
        ),
        (
            "unused-parent-attributes",
            (
                f'<svg {ns} viewBox="0 0 24 24"><g fill="red" stroke="blue" opacity="0.5">'
                f'<rect width="1" height="1" fill="green"/>'
                f'<rect width="2" height="2" fill="inherit"/></g></svg>'
            ),
        ),
        (
            "style-repair-inherited",
            (
                f'<svg {ns} viewBox="0 0 24 24"><g style="fill:red;stroke:blue">'
                f'<rect width="1" height="1" style="fill:red;stroke:green"/></g></svg>'
            ),
        ),
        (
            "style-repair-defaults",
            (
                f'<svg {ns} viewBox="0 0 24 24"><g style="fill:red"><g style="fill:red;opacity:1">'
                f'<rect width="1" height="1" style="fill:inherit"/></g></g></svg>'
            ),
        ),
        (
            "style-repair-child-overrides",
            (
                f'<svg {ns} viewBox="0 0 24 24"><g style="fill:red">'
                f'<rect width="1" height="1" style="fill:blue"/>'
                f'<rect width="2" height="2" style="fill:green"/></g></svg>'
            ),
        ),
        (
            "style-repair-non-inherited",
            (
                f'<svg {ns} viewBox="0 0 24 24"><g style="opacity:0.5;display:none">'
                f'<rect width="1" height="1" style="opacity:0.5;display:none"/></g></svg>'
            ),
        ),
        (
            "style-repair-text",
            (
                f'<svg {ns} viewBox="0 0 24 24"><g style="font-size:12px">'
                f'<text style="font-size:12px;fill:red">a<tspan style="font-size:12px">b</tspan></text></g>'
                f"</svg>"
            ),
        ),
        (
            "doctype-public",
            (
                '<?xml version="1.0"?>\n'
                '<!DOCTYPE svg PUBLIC "-//W3C//DTD SVG 1.1//EN"'
                ' "http://www.w3.org/Graphics/SVG/1.1/DTD/svg11.dtd">\n'
                f'<svg {ns} viewBox="0 0 24 24"><rect width="1" height="1"/></svg>'
            ),
        ),
        (
            "doctype-system",
            (f'<!DOCTYPE svg SYSTEM "x.dtd"><svg {ns} viewBox="0 0 24 24"><rect width="1" height="1"/></svg>'),
        ),
        ("doctype-plain", f'<!DOCTYPE svg><svg {ns} viewBox="0 0 24 24"><rect width="1" height="1"/></svg>'),
        (
            "processing-instructions",
            f'<?xml-stylesheet href="a.css"?><svg {ns} viewBox="0 0 24 24"><rect width="1" height="1"/></svg>'
            f"<?after data?>",
        ),
        (
            "style-element-references",
            (
                f'<svg {ns} viewBox="0 0 24 24"><style>.a {{ fill: url(#g); clip-path: url(#c) }}</style><defs>'
                f'<linearGradient id="g"><stop offset="0" stop-color="#f00"/></linearGradient>'
                f'<clipPath id="c"><rect width="1" height="1"/></clipPath>'
                f'<linearGradient id="unused"><stop offset="0" stop-color="#f00"/></linearGradient></defs>'
                f'<rect class="a" width="4" height="4"/></svg>'
            ),
        ),
        (
            "style-element-unreferenced",
            (
                f'<svg {ns} viewBox="0 0 24 24"><style>.a {{ fill: red }}</style><defs>'
                f'<rect id="gone" width="1" height="1"/></defs>'
                f'<rect class="a" width="4" height="4"/></svg>'
            ),
        ),
        (
            "references-animation",
            (
                f'<svg {ns} {xl} viewBox="0 0 24 24"><defs><linearGradient id="g">'
                f'<stop offset="0" stop-color="#f00"/></linearGradient></defs>'
                f'<rect width="1" height="1">'
                f'<animate attributeName="fill" values="url(#g);red" begin="a.begin"/></rect></svg>'
            ),
        ),
    ]
    return documents


def _simplifier_documents(inputs: list[tuple[str, str]]) -> list[dict[str, Any]]:
    documents: list[dict[str, Any]] = []
    for name, text in inputs:
        entry: dict[str, Any] = {"name": name, "input": _text_or_hash(text), "nodes": count_svg_path_nodes(text)}
        entry["scour"] = _text_or_hash(run_scour_pass(text))
        entry["quantize"] = _text_or_hash(quantize_svg_coordinates(text))
        entry["quantize3"] = _text_or_hash(quantize_svg_coordinates(text, precision=3))
        entry["final"] = _text_or_hash(simplify_svg_document(text))
        entry["geometricOnly"] = _text_or_hash(simplify_svg_document(text, use_scour=False, precision=2))
        documents.append(entry)
    return documents


def _simplifier_oracle(out: Path) -> None:
    out.mkdir(parents=True, exist_ok=True)
    finals = out / "finals"
    finals.mkdir(exist_ok=True)

    numbers = [
        {"value": value, "precision": precision, "text": PathCommand("H", [value]).serialize(precision)}
        for value in SIMPLIFIER_NUMBERS
        for precision in (0, 1, 2, 3, 4)
    ]
    paths = []
    for d in SIMPLIFIER_PATHS:
        subpaths = SVGPathTokenizer.extract_subpaths(d)
        paths.append(
            {
                "d": d,
                "commands": [[c.command, c.args] for c in parse_path_d(d)],
                "serialized": {str(p): " ".join(c.serialize(p) for c in parse_path_d(d)) for p in (0, 2, 3)},
                "subpaths": [{"closed": sp.is_closed, "commands": len(sp.commands)} for sp in subpaths],
            }
        )
    geometry = [
        {
            "name": name,
            "d": d,
            "results": {str(t): simplify_path_geometry(d, tolerance=t) for t in SIMPLIFIER_GEOMETRY_TOLERANCES},
        }
        for d, name in SIMPLIFIER_GEOMETRY
    ]

    real: list[tuple[str, str]] = []
    real_meta = []
    for rel in SIMPLIFIER_INPUTS:
        raw = (ROOT / rel).read_bytes()
        real.append((rel, raw.decode("utf-8")))
        real_meta.append({"path": rel, "sha256": hashlib.sha256(raw).hexdigest()})

    real_docs = []
    for rel, text in real:
        entry: dict[str, Any] = {
            "path": rel,
            "nodes": count_svg_path_nodes(text),
            "tolerances": [],
        }
        for tolerance in SIMPLIFIER_TOLERANCES:
            geometric = simplify_svg_document(text, tolerance=tolerance, use_scour=False, precision=2)
            final = simplify_svg_document(text, tolerance=tolerance)
            entry["tolerances"].append(
                {
                    "tolerance": tolerance,
                    "geometric": _text_or_hash(geometric),
                    "final": _text_or_hash(final),
                    "finalNodes": count_svg_path_nodes(final),
                }
            )
            if tolerance == SIMPLIFIER_TOLERANCES[0] and "autotraced" not in rel:
                (finals / rel.replace("/", "__")).write_text(final, encoding="utf-8", newline="")
        entry["scour"] = _text_or_hash(run_scour_pass(text))
        entry["quantize"] = _text_or_hash(quantize_svg_coordinates(text))
        entry["geometricOnly"] = _text_or_hash(simplify_svg_document(text, use_scour=False))
        entry["scourOnly"] = _text_or_hash(simplify_svg_document(text, use_geometric=False))
        entry["noGeometricPass"] = _text_or_hash(simplify_svg_document(text, tolerance=0.0))
        entry["precision3"] = _text_or_hash(simplify_svg_document(text, precision=3))
        real_docs.append(entry)

    document = {
        "provenance": {
            "generator": "tools/image-drift/oracle/dump_oracle.py --simplifier-out",
            "python": platform.python_version(),
            "scour": metadata.version("scour"),
            "shapely": metadata.version("shapely"),
            "geos": __import__("shapely").geos_version_string,
            "image_drift": metadata.version("image-drift"),
        },
        "numbers": numbers,
        "paths": paths,
        "geometry": geometry,
        "inputs": real_meta,
        "realDocuments": real_docs,
        "syntheticDocuments": _simplifier_documents(_synthetic_documents()),
    }
    target = out / "simplifier-oracle.json"
    target.write_text(json.dumps(document, indent=1, ensure_ascii=False) + "\n", encoding="utf-8", newline="")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--simplifier-out", type=Path, help="Freeze only the SVG simplifier's outputs here.")
    parser.add_argument("--out", type=Path)
    parser.add_argument("--regions-out", type=Path)
    parser.add_argument("--chromium", type=Path)
    parser.add_argument("--normalised-dir", type=Path, help="Also write Pillow's normalised references here.")
    args = parser.parse_args()
    if args.simplifier_out is not None:
        _simplifier_oracle(args.simplifier_out.resolve())
        return 0
    if args.out is None or args.regions_out is None or args.chromium is None:
        parser.error("--out, --regions-out and --chromium are required unless --simplifier-out is given")
    out: Path = args.out.resolve()
    if out.exists():
        shutil.rmtree(out)
    out.mkdir(parents=True)

    renders: list[dict[str, Any]] = []
    metrics: list[dict[str, Any]] = []
    promotion: list[dict[str, Any]] = []
    normalised: list[dict[str, Any]] = []
    icons: list[dict[str, Any]] = []

    with tempfile.TemporaryDirectory() as scratch_name, sync_playwright() as playwright:
        scratch = Path(scratch_name)
        browser = playwright.chromium.launch(executable_path=str(args.chromium))
        browser_version = browser.version

        def render(render_id: str, svg: Path, size: int) -> Path:
            png = out / "renders" / f"{render_id}.png"
            _render(browser, svg, png, size, scratch)
            digest = _sha256(png)
            with Image.open(png) as image:
                rgba_digest = hashlib.sha256(image.convert("RGBA").tobytes()).hexdigest()
            renders.append(
                {
                    "id": render_id,
                    "svg": _display(svg, out),
                    "size": size,
                    "file": f"renders/{render_id}.png" if size <= MAX_CHECKED_IN_SIZE else None,
                    "rgba_sha256": rgba_digest,
                }
            )
            if size > MAX_CHECKED_IN_SIZE:
                png.unlink()
            _ = digest
            return png

        for project, info in PROJECTS.items():
            master, favicon = ROOT / info["master"], ROOT / info["favicon"]
            master_png = Path()
            for size in MASTER_SIZES:
                png = render(f"{project}.master.{size}", master, size)
                if size == NORMALISED:
                    master_png = png
            favicon_48 = Path()
            for size in FAVICON_SIZES:
                png = render(f"{project}.favicon.{size}", favicon, size)
                if size == 48:
                    favicon_48 = png
            icons.append(_ico(out, favicon_48, project))

            reference_dir = ROOT / "assets" / project / "reference"
            regions: list[FeatureRegion] = info["regions"]
            for ref_name in info["refs"]:
                ref = reference_dir / ref_name
                digest, image = _normalised_hash(ref)
                normalised.append(
                    {
                        "project": project,
                        "reference": str(ref.relative_to(ROOT)).replace("\\", "/"),
                        "rgb_sha256": digest,
                    }
                )
                if args.normalised_dir:
                    args.normalised_dir.mkdir(parents=True, exist_ok=True)
                    image.save(args.normalised_dir / f"{project}.{ref_name}.png")
                metrics.append(
                    {
                        "id": f"{project}.master.{NORMALISED}.vs.{ref_name}",
                        "project": project,
                        "reference": str(ref.relative_to(ROOT)).replace("\\", "/"),
                        "render_id": f"{project}.master.{NORMALISED}",
                        "drift": _drift(ref, master_png),
                        "score": _score(score_candidate(ref, master_png, feature_boxes(regions))),
                    }
                )

            # Promotion pairs: the governed master is the incumbent, each pathway a candidate.
            ref = reference_dir / info["refs"][0]
            master_text = master.read_text(encoding="utf-8")
            candidates: dict[str, str] = {
                "autotraced": pathways.trace_reference(ref),
                "regularized": regularize_svg(master_text),
            }
            if project == "rtkelly-labs":
                candidates["parametric"] = labs_parametric.generate_svg()
            incumbent_score = score_candidate(ref, master_png, feature_boxes(regions))
            incumbent_drift = compute_drift(ref, master_png)
            for name, text in candidates.items():
                svg_path = out / "candidates" / f"{project}.{name}.svg"
                svg_path.parent.mkdir(parents=True, exist_ok=True)
                svg_path.write_text(text, encoding="utf-8", newline="\n")
                png = render(f"{project}.{name}.{NORMALISED}", svg_path, NORMALISED)
                score = score_candidate(ref, png, feature_boxes(regions))
                drift = compute_drift(ref, png)
                verdict = evaluate_promotion(incumbent_score, incumbent_drift, score, drift)
                promotion.append(
                    {
                        "project": project,
                        "reference": str(ref.relative_to(ROOT)).replace("\\", "/"),
                        "incumbent_render_id": f"{project}.master.{NORMALISED}",
                        "candidate": name,
                        "candidate_svg": f"candidates/{project}.{name}.svg",
                        "candidate_render_id": f"{project}.{name}.{NORMALISED}",
                        "incumbent_score": _score(incumbent_score),
                        "incumbent_ssim": incumbent_drift.ssim,
                        "candidate_score": _score(score),
                        "candidate_ssim": drift.ssim,
                        "promotable": verdict.promotable,
                        "composite_delta": verdict.composite_delta,
                        "ssim_delta": verdict.ssim_delta,
                        "worst_feature_delta": verdict.worst_feature_delta,
                        "worst_feature_key": verdict.worst_feature_key,
                        "reasons": verdict.reasons,
                    }
                )
        browser.close()

    regions_document = {
        project: [{"key": r.key, "x0": r.x0, "y0": r.y0, "x1": r.x1, "y1": r.y1} for r in info["regions"]]
        for project, info in PROJECTS.items()
    }
    document = {
        "provenance": {
            "browser": browser_version,
            "os": platform.system(),
            "arch": platform.machine(),
            "python": platform.python_version(),
            "pillow": metadata.version("pillow"),
            "numpy": np.__version__,
            "playwright": metadata.version("playwright"),
            "image_drift": metadata.version("image-drift"),
        },
        "regions": regions_document,
        "renders": renders,
        "normalised_references": normalised,
        "icons": icons,
        "metrics": metrics,
        "promotion": promotion,
    }
    (out / "score-oracle.json").write_text(
        json.dumps(document, indent=2, allow_nan=False) + "\n", encoding="utf-8", newline="\n"
    )
    args.regions_out.parent.mkdir(parents=True, exist_ok=True)
    args.regions_out.write_text(
        json.dumps(regions_document, indent=2, allow_nan=False) + "\n", encoding="utf-8", newline="\n"
    )
    sys.stdout.write(f"wrote {out / 'score-oracle.json'} ({len(renders)} renders)\n")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
