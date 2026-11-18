"""Freeze image_drift's scores into the C# test oracle (rig issue #324, ADR 0024).

Run from the repository root, with Playwright's headless shell for the revision the .NET Playwright package pins:

    cd tools/image-drift && uv run --locked python -I oracle/dump_oracle.py \
        --out ../../tests/Rig.Image.Tests/Oracle \
        --regions-out ../../src/Rig.Image/feature-regions.json \
        --chromium <path to chrome-headless-shell>

It renders every brand master and cut with image_drift's own HTML wrapper, scores them with image_drift's own
metrics and promotion gate, builds the ICO the way production_cuts.py does, and writes the numbers as
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


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--out", type=Path, required=True)
    parser.add_argument("--regions-out", type=Path, required=True)
    parser.add_argument("--chromium", type=Path, required=True)
    parser.add_argument("--normalised-dir", type=Path, help="Also write Pillow's normalised references here.")
    args = parser.parse_args()
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
