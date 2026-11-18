"""Dump the Python/Playwright reference for the image-stack evaluation (issue #317).

Run from the repository root:

    uv run --frozen --project tools/image-drift python -I \
        experiments/image-stack-eval/reference/reference_dump.py --out temp/image-stack-eval/reference \
        --chromium <path to the chrome-headless-shell binary>

It renders every SVG the C# stacks are compared on with image-drift's own HTML wrapper, scores them with
image-drift's own metrics, and writes the numbers as `reference.json`. The C# console project reads that file
and the PNGs; it never calls Python.

`--chromium` pins the browser binary so the .NET Playwright run can launch the very same executable. Without it
Playwright would want its own pinned revision, which is a download and a different browser.
"""

from __future__ import annotations

import argparse
import json
import sys
import time
from dataclasses import asdict
from pathlib import Path
from typing import Any

from image_drift import labs_builder, labs_parametric, pathways
from image_drift.builder_contract import FeatureRegion
from image_drift.cli_base import feature_boxes
from image_drift.diagnostics import analyze_decomposed_drift
from image_drift.diamond_parquet_builder import FEATURE_REGIONS as DIAMOND_REGIONS
from image_drift.edge_regularity import regularize_svg
from image_drift.metrics import compute_drift, score_candidate
from image_drift.promotion import evaluate_promotion
from playwright.sync_api import sync_playwright

ROOT = Path.cwd()
BUILDER_ASSETS = ROOT / "tools" / "image-drift" / "src" / "image_drift" / "builder_assets"
NORMALISED = 500

MASTER_SIZES = (500, 180, 1000, 2000)
FAVICON_SIZES = (500, 512, 48, 32, 16)

PROJECTS: dict[str, dict[str, Any]] = {
    "rtkelly-labs": {
        "regions": labs_builder.FEATURE_REGIONS,
        "refs": ["rtkelly-labs.png", "02-smooth-crossover-baseline.jpg"],
    },
    "parquet-source-generator": {
        "regions": DIAMOND_REGIONS,
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


def _render(browser: Any, svg: Path, png: Path, size: int, scratch: Path) -> float:
    png.parent.mkdir(parents=True, exist_ok=True)
    html = scratch / f"_{png.stem}.html"
    html.write_text(_html(svg, size), encoding="utf-8")
    started = time.perf_counter()
    page = browser.new_page(viewport={"width": size, "height": size})
    page.goto(f"file://{html.resolve()}")
    page.screenshot(path=png, omit_background=True)
    page.close()
    elapsed = time.perf_counter() - started
    html.unlink()
    return elapsed


def _binding_trace(reference: Path, target: Path) -> None:
    """The raw vtracer-binding output for the default pathway config, before image_drift patches a viewBox in."""
    import vtracer

    cfg = pathways.VtracerConfig()
    target.parent.mkdir(parents=True, exist_ok=True)
    vtracer.convert_image_to_svg_py(
        str(reference),
        str(target),
        colormode=cfg.colormode,
        hierarchical=cfg.hierarchical,
        mode=cfg.mode,
        filter_speckle=cfg.filter_speckle,
        color_precision=cfg.color_precision,
        layer_difference=cfg.layer_difference,
        corner_threshold=cfg.corner_threshold,
        length_threshold=cfg.length_threshold,
        max_iterations=cfg.max_iterations,
        path_precision=cfg.path_precision,
    )


def _drift(reference: Path, render: Path) -> dict[str, Any]:
    drift = compute_drift(reference, render)
    report = analyze_decomposed_drift(reference, render)
    return {
        "rmse_pct": drift.rmse_pct,
        "ssim": drift.ssim,
        "psnr_db": drift.psnr_db,
        "grid_cells": [cell.error for cell in drift.grid],
        "luminance_rmse_pct": report.luminance_rmse_pct,
        "chrominance_rmse_pct": report.chrominance_rmse_pct,
        "color_to_geometry_ratio": report.color_to_geometry_ratio,
        "edge_correlation": report.edge_correlation,
        "boundary_dilation_bias": report.boundary_dilation_bias,
        "hotspots": [asdict(spot) for spot in report.hotspots],
    }


def _score(reference: Path, render: Path, regions: list[FeatureRegion]) -> dict[str, Any]:
    score = score_candidate(reference, render, feature_boxes(regions))
    return {
        "composite_pct": score.composite_pct,
        "full_frame_pct": score.full_frame_pct,
        "per_feature_pct": score.per_feature_pct,
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--out", type=Path, required=True)
    parser.add_argument("--chromium", type=Path, required=True)
    args = parser.parse_args()
    out: Path = args.out.resolve()
    scratch = out / "scratch"
    scratch.mkdir(parents=True, exist_ok=True)
    (out / "svg").mkdir(exist_ok=True)

    svgs: dict[str, Path] = {}
    renders: list[dict[str, Any]] = []
    metrics: list[dict[str, Any]] = []
    promotion: list[dict[str, Any]] = []

    with sync_playwright() as playwright:
        launch_started = time.perf_counter()
        browser = playwright.chromium.launch(executable_path=str(args.chromium))
        launch_seconds = time.perf_counter() - launch_started

        def render(render_id: str, svg: Path, size: int) -> Path:
            png = out / "render" / f"{render_id}.png"
            seconds = _render(browser, svg, png, size, scratch)
            renders.append(
                {
                    "id": render_id,
                    "svg": str(svg.relative_to(ROOT) if svg.is_relative_to(ROOT) else svg),
                    "size": size,
                    "seconds": seconds,
                }
            )
            return png

        for project, info in PROJECTS.items():
            assets = BUILDER_ASSETS / project
            master, favicon = assets / "master.svg", assets / "favicon.svg"
            for size in MASTER_SIZES:
                render(f"{project}.master.{size}", master, size)
            for size in FAVICON_SIZES:
                render(f"{project}.favicon.{size}", favicon, size)

            reference_dir = ROOT / "assets" / project / "reference"
            regions: list[FeatureRegion] = info["regions"]
            master_png = out / "render" / f"{project}.master.{NORMALISED}.png"
            for ref_name in info["refs"]:
                ref = reference_dir / ref_name
                metrics.append(
                    {
                        "id": f"{project}.master.{NORMALISED}.vs.{ref_name}",
                        "project": project,
                        "reference": str(ref.relative_to(ROOT)),
                        "render_id": f"{project}.master.{NORMALISED}",
                        "drift": _drift(ref, master_png),
                        "score": _score(ref, master_png, regions),
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
            _binding_trace(ref, out / "vtracer" / f"{project}.binding.svg")
            incumbent_score = score_candidate(ref, master_png, feature_boxes(regions))
            incumbent_drift = compute_drift(ref, master_png)
            for name, text in candidates.items():
                svg_path = out / "svg" / f"{project}.{name}.svg"
                svg_path.write_text(text, encoding="utf-8", newline="\n")
                svgs[f"{project}.{name}"] = svg_path
                png = render(f"{project}.{name}.{NORMALISED}", svg_path, NORMALISED)
                score = score_candidate(ref, png, feature_boxes(regions))
                drift = compute_drift(ref, png)
                verdict = evaluate_promotion(incumbent_score, incumbent_drift, score, drift)
                promotion.append(
                    {
                        "project": project,
                        "reference": str(ref.relative_to(ROOT)),
                        "incumbent_render_id": f"{project}.master.{NORMALISED}",
                        "candidate": name,
                        "candidate_render_id": f"{project}.{name}.{NORMALISED}",
                        "incumbent_score": _score(ref, master_png, regions),
                        "incumbent_ssim": incumbent_drift.ssim,
                        "candidate_score": _score(ref, png, regions),
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

    document = {
        "regions": {
            name: [{"key": r.key, "x0": r.x0, "y0": r.y0, "x1": r.x1, "y1": r.y1} for r in info["regions"]]
            for name, info in PROJECTS.items()
        },
        "chromium": str(args.chromium),
        "chromium_launch_seconds": launch_seconds,
        "renders": renders,
        "metrics": metrics,
        "promotion": promotion,
    }
    (out / "reference.json").write_text(json.dumps(document, indent=2), encoding="utf-8")
    shutil_target = out / "scratch"
    if shutil_target.exists() and not any(shutil_target.iterdir()):
        shutil_target.rmdir()
    sys.stdout.write(f"wrote {out / 'reference.json'} ({len(renders)} renders)\n")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
