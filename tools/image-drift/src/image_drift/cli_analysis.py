"""Image-drift commands that build, compare and diagnose a single SVG."""

from __future__ import annotations

from pathlib import Path
from typing import TYPE_CHECKING, Annotated

import typer
from rich.panel import Panel
from rich.table import Table

from image_drift.cli_base import (
    DEFAULT_PROJECT,
    app,
    console,
    feature_map,
    load_builder_module,
    resolve_project,
)
from image_drift.imaging import (
    comparison_sheet,
    crop,
    load_normalised,
    render_svg,
)
from image_drift.metrics import (
    compute_drift,
)

# FeatureRegion is the shared contract type — always imported from svg_builder.

if TYPE_CHECKING:
    from image_drift.diagnostics import DecomposedDriftReport


@app.command()
def build(
    project: Annotated[
        str,
        typer.Option(
            "--project",
            "-p",
            help="Target project brand ('rtkelly-labs' or 'parquet-source-generator').",
        ),
    ] = DEFAULT_PROJECT,
    variant: Annotated[
        str | None,
        typer.Option("--variant", "-v", help="Builder variant key. Defaults to the builder's own default."),
    ] = None,
    svg: Annotated[Path | None, typer.Option("--svg", "-s", help="Path where the SVG will be written.")] = None,
    builder_module: Annotated[
        Path | None, typer.Option("--builder-module", "-B", help="Path to the SVG builder Python module.")
    ] = None,
    list_variants: Annotated[
        bool, typer.Option("--list-variants", help="List the builder's variants and exit.")
    ] = False,
) -> None:
    """Generate the SVG deterministically — no rendering, diffing, or preview.

    Running this twice with the same arguments produces byte-identical output.
    """
    cfg = resolve_project(project)
    target_svg = svg or cfg.svg_path
    b_mod = builder_module or cfg.builder_module
    builder = load_builder_module(b_mod)

    if list_variants:
        table = Table(title="Builder Variants")
        table.add_column("Key", style="bold cyan")
        table.add_column("Name")
        table.add_column("Description")
        for key, info in builder.variants.items():
            marker = " [green](default)[/]" if key == builder.default_variant else ""
            table.add_row(key + marker, info.name, info.description)
        console.print(table)
        return

    if variant is not None and builder.variants and variant not in builder.variants:
        console.print(f"[bold red]Error:[/] Unknown variant '{variant}'. Available: {list(builder.variants.keys())}")
        raise typer.Exit(code=1)

    chosen = variant or builder.default_variant
    builder.render(target_svg, chosen)
    label = chosen or "builder default"
    console.print(f"[green]✓ Built[/] [bold cyan]{label}[/] [green]→[/] {target_svg.resolve()}")


@app.command(name="extract-feature")
def extract_feature(
    image: Annotated[Path, typer.Option("--image", "-i", help="Path to reference or rendered image")],
    feature: Annotated[str, typer.Option("--feature", "-f", help="Pre-defined feature region")],
    output: Annotated[Path, typer.Option("--output", "-o", help="Output PNG path for extracted feature crop")],
    project: Annotated[
        str,
        typer.Option(
            "--project",
            "-p",
            help="Target project brand ('rtkelly-labs' or 'parquet-source-generator').",
        ),
    ] = DEFAULT_PROJECT,
    reference_box: Annotated[
        bool,
        typer.Option(
            "--reference-box/--target-box",
            help="Crop using the region's reference box rather than its target box.",
        ),
    ] = False,
    builder_module: Annotated[
        Path | None, typer.Option("--builder-module", "-B", help="Path to the SVG builder Python module.")
    ] = None,
) -> None:
    """Extract an isolated sub-region element crop from an image for fast independent analysis."""
    if not image.exists():
        console.print(f"[bold red]Error:[/] Image file '{image}' does not exist.")
        raise typer.Exit(code=1)

    cfg = resolve_project(project)
    b_mod = builder_module or cfg.builder_module
    regions = feature_map(load_builder_module(b_mod).feature_regions)
    if feature not in regions:
        console.print(f"[bold red]Error:[/] Unknown feature region '{feature}'. Available: {list(regions)}")
        raise typer.Exit(code=1)

    region = regions[feature]
    bbox = region.ref_bbox if reference_box else region.bbox

    if image.suffix.lower() == ".svg":
        temp_png = image.parent / f"_temp_extract_{image.stem}.png"
        render_svg(image, temp_png)
        try:
            crop(load_normalised(temp_png), bbox).save(output)
        finally:
            if temp_png.exists():
                temp_png.unlink()
    else:
        crop(load_normalised(image), bbox).save(output)

    console.print(f"[green]✨ Extracted feature region '[bold cyan]{feature}[/]' to:[/] {output.resolve()}")


def _render_diagnostics_report(report: DecomposedDriftReport) -> None:
    """Render a Rich formatted report for decomposed drift diagnostics."""
    diag_table = Table(title="[ DECOMPOSED_DRIFT_DIAGNOSTICS ]", border_style="cyan")
    diag_table.add_column("Channel / Axis", style="bold cyan")
    diag_table.add_column("Measurement", justify="right")
    diag_table.add_column("Interpretation & Architectural Guidance", style="dim")

    diag_table.add_row(
        "Total Pixel Drift",
        f"{report.total_rmse_pct:.2f}% RMSE",
        f"PSNR: {report.psnr_db:.1f} dB, SSIM: {report.ssim * 100.0:.2f}%",
    )
    diag_table.add_row(
        "Luminance / Geometry Error (Y)",
        f"{report.luminance_rmse_pct:.2f}%",
        "Measures edge contour and vertex shape displacement",
    )
    diag_table.add_row(
        "Chrominance / Color Error (CbCr)",
        f"{report.chrominance_rmse_pct:.2f}%",
        f"Color/Geometry ratio: {report.color_to_geometry_ratio:.2f}x",
    )
    diag_table.add_row(
        "Edge Field Correlation",
        f"{report.edge_correlation:.1f}%",
        "Sobel gradient tangent alignment along contours",
    )
    bias_label = "DILATION (+)" if report.boundary_dilation_bias > 0 else "EROSION (-)"
    diag_table.add_row(
        "Boundary Contour Bias",
        f"{report.boundary_dilation_bias:+.2f} ({bias_label})",
        "Feature thickness relative to raster ground-truth",
    )
    console.print(diag_table)

    if report.hotspots:
        hot_table = Table(title="[ LOCALIZED_ERROR_HOTSPOTS ]", border_style="yellow")
        hot_table.add_column("Rank", justify="center")
        hot_table.add_column("Coordinate Box (X, Y)", justify="left")
        hot_table.add_column("Error", justify="right")
        hot_table.add_column("Dominant Cause", style="bold")
        hot_table.add_column("Localized Guidance", style="dim")
        for h in report.hotspots:
            hot_table.add_row(
                f"#{h.rank}",
                f"X:[{h.x0}..{h.x1}], Y:[{h.y0}..{h.y1}]",
                f"{h.error_pct:.2f}%",
                h.dominant_mode,
                h.summary,
            )
        console.print(hot_table)

    rec_lines = "\n".join(f"• {rec}" for rec in report.recommendations)
    console.print(Panel(rec_lines, title="[ ACTIONABLE_BUILDER_REFINEMENTS ]", border_style="green"))


@app.command()
def compare(
    project: Annotated[
        str,
        typer.Option(
            "--project",
            "-p",
            help="Target project brand ('rtkelly-labs' or 'parquet-source-generator').",
        ),
    ] = DEFAULT_PROJECT,
    reference: Annotated[
        Path | None,
        typer.Option("--reference", "-r", help="Path to reference image (JPG/PNG). Defaults to project reference."),
    ] = None,
    svg: Annotated[
        Path | None,
        typer.Option("--svg", "-s", help="Path to target SVG file. Defaults to project SVG."),
    ] = None,
    render: Annotated[
        Path | None,
        typer.Option("--render", help="Path to pre-rendered image (alternative to --svg)"),
    ] = None,
    feature: Annotated[str | None, typer.Option("--feature", "-f", help="Optional feature region name")] = None,
    output_diff: Annotated[
        Path | None,
        typer.Option("--output-diff", "-d", help="Write a diff heatmap PNG here (off by default)."),
    ] = None,
    output_sheet: Annotated[
        Path | None,
        typer.Option(
            "--output-sheet",
            help="Write a 3-panel comparative diagnostic sheet [Reference | Render | Diff].",
        ),
    ] = None,
    builder_module: Annotated[
        Path | None, typer.Option("--builder-module", "-B", help="Path to the SVG builder Python module.")
    ] = None,
    decompose: Annotated[
        bool,
        typer.Option(
            "--decompose",
            "-D",
            help="Decompose drift into structural/geometric vs chromatic/color channels with reasoning guidance.",
        ),
    ] = False,
) -> None:
    """Run pixel-by-pixel drift, SSIM, and PSNR comparison."""
    cfg = resolve_project(project)
    ref_path = reference or cfg.reference_path
    target_svg = svg or (cfg.svg_path if render is None else None)
    b_mod = builder_module or cfg.builder_module

    if not ref_path.exists():
        console.print(f"[bold red]Error:[/] Reference image '{ref_path}' does not exist.")
        raise typer.Exit(code=1)

    if render is None and target_svg is not None and not target_svg.exists():
        console.print(f"[bold red]Error:[/] SVG file '{target_svg}' does not exist.")
        raise typer.Exit(code=1)
    if render is not None and not render.exists():
        console.print(f"[bold red]Error:[/] Render image '{render}' does not exist.")
        raise typer.Exit(code=1)

    builder = load_builder_module(b_mod)
    regions = feature_map(builder.feature_regions)

    crop_bbox = None
    if feature:
        if feature not in regions:
            console.print(f"[bold red]Error:[/] Unknown feature region '{feature}'. Available: {list(regions)}")
            raise typer.Exit(code=1)
        crop_bbox = regions[feature].bbox

    temp_rendered_png: Path | None = None
    if render is not None:
        rendered_png = render
    elif target_svg is not None:
        rendered_png = target_svg.parent / f"_rendered_{target_svg.stem}.png"
        temp_rendered_png = rendered_png
        render_svg(target_svg, rendered_png)
    else:
        console.print("[bold red]Error:[/] Either --svg or --render must be specified.")
        raise typer.Exit(code=1)

    temp_diff_png: Path | None = None
    diff_path = output_diff
    if diff_path is None and output_sheet is not None:
        diff_path = rendered_png.parent / f"_diff_{rendered_png.stem}.png"
        temp_diff_png = diff_path

    try:
        result = compute_drift(ref_path, rendered_png, diff_path, crop_bbox)

        title_suffix = f" (Isolated Feature: {feature})" if feature else ""
        table = Table(title=f"Top Spatial Drift Regions{title_suffix}")
        table.add_column("Rank", justify="center")
        table.add_column("Grid Bounds (Y, X)", justify="left")
        table.add_column("Region Mean Error", justify="right")

        for rank, cell in enumerate(result.worst_cells[:5], start=1):
            bounds = cell.bounds
            table.add_row(
                f"#{rank}",
                f"Row {cell.row + 1}, Col {cell.column + 1} "
                f"(Y:[{bounds.row_start}:{bounds.row_end}], X:[{bounds.col_start}:{bounds.col_end}])",
                f"{cell.error:.2f}",
            )

        psnr_str = f"{result.psnr_db:.1f} dB" if result.psnr_db != float("inf") else "∞"
        console.print(
            Panel(
                f"[bold cyan]Pixel Drift Result:[/] [yellow]{result.rmse_pct:.2f}%[/] RMSE | "
                f"[green]{result.ssim * 100.0:.2f}%[/] SSIM ({result.ssim:.3f}) | "
                f"[magenta]{psnr_str}[/] PSNR{title_suffix}",
                title=f"Drift Summary — {cfg.name}",
            )
        )
        console.print(table)

        if not feature:
            feature_table = Table(title="Per-Feature Drift")
            feature_table.add_column("Feature", style="bold cyan")
            feature_table.add_column("RMSE", justify="right")
            for fr in builder.feature_regions:
                pct = compute_drift(ref_path, rendered_png, crop_bbox=fr.bbox).rmse_pct
                feature_table.add_row(fr.key, f"{pct:.2f}%")
            console.print(feature_table)

        if decompose:
            from image_drift.diagnostics import analyze_decomposed_drift

            diag_report = analyze_decomposed_drift(ref_path, rendered_png)
            _render_diagnostics_report(diag_report)

        if output_diff and diff_path:
            console.print(f"\n[green]✓ Diff heatmap saved to:[/] {diff_path.resolve()}")

        if output_sheet and diff_path and diff_path.exists():
            ref_img = load_normalised(ref_path)
            rnd_img = load_normalised(rendered_png)
            d_img = load_normalised(diff_path)
            sheet = comparison_sheet(ref_img, rnd_img, d_img, rmse_pct=result.rmse_pct, ssim=result.ssim)
            output_sheet.parent.mkdir(parents=True, exist_ok=True)
            sheet.save(output_sheet)
            console.print(f"[green]✓ Comparison sheet saved to:[/] {output_sheet.resolve()}")

    finally:
        if temp_rendered_png and temp_rendered_png.exists():
            temp_rendered_png.unlink()
        if temp_diff_png and temp_diff_png.exists():
            temp_diff_png.unlink()


@app.command(name="diagnose")
def diagnose(
    project: Annotated[
        str,
        typer.Option(
            "--project",
            "-p",
            help="Target project brand ('rtkelly-labs' or 'parquet-source-generator').",
        ),
    ] = DEFAULT_PROJECT,
    reference: Annotated[
        Path | None,
        typer.Option("--reference", "-r", help="Path to reference image (JPG/PNG). Defaults to project reference."),
    ] = None,
    svg: Annotated[
        Path | None,
        typer.Option("--svg", "-s", help="Path to target SVG file. Defaults to project SVG."),
    ] = None,
    render: Annotated[
        Path | None,
        typer.Option("--render", help="Path to pre-rendered image (alternative to --svg)"),
    ] = None,
    size: Annotated[
        int,
        typer.Option("--size", help="Canvas size for rendering and evaluation (default 1000)."),
    ] = 1000,
) -> None:
    """Decompose visual drift into structural/geometric vs chromatic/color channels with actionable guidance."""
    from image_drift.diagnostics import analyze_decomposed_drift

    cfg = resolve_project(project)
    ref_path = reference or cfg.reference_path
    target_svg = svg or (cfg.svg_path if render is None else None)

    if not ref_path.exists():
        console.print(f"[bold red]Error:[/] Reference image '{ref_path}' does not exist.")
        raise typer.Exit(code=1)

    if render is None and target_svg is not None and not target_svg.exists():
        console.print(f"[bold red]Error:[/] SVG file '{target_svg}' does not exist.")
        raise typer.Exit(code=1)
    if render is not None and not render.exists():
        console.print(f"[bold red]Error:[/] Render image '{render}' does not exist.")
        raise typer.Exit(code=1)

    temp_rendered_png: Path | None = None
    if render is not None:
        rendered_png = render
    elif target_svg is not None:
        rendered_png = target_svg.parent / f"_rendered_{target_svg.stem}_{size}px.png"
        temp_rendered_png = rendered_png
        render_svg(target_svg, rendered_png, width=size, height=size)
    else:
        console.print("[bold red]Error:[/] Either --svg or --render must be specified.")
        raise typer.Exit(code=1)

    try:
        report = analyze_decomposed_drift(ref_path, rendered_png, canvas_size=size)
        _render_diagnostics_report(report)
    finally:
        if temp_rendered_png and temp_rendered_png.exists():
            temp_rendered_png.unlink()


@app.command(name="export-avatar")
def export_avatar(
    project: Annotated[
        str,
        typer.Option(
            "--project",
            "-p",
            help="Target project brand ('rtkelly-labs' or 'parquet-source-generator').",
        ),
    ] = DEFAULT_PROJECT,
    output_dir: Annotated[
        Path | None,
        typer.Option(
            "--output-dir",
            "-o",
            help="Directory where avatar and icon cuts are saved (defaults to assets/<project>/production-cuts/).",
        ),
    ] = None,
) -> None:
    """Export every production cut declared in the project's production-cuts.csv.

    Writes to assets/<project>/production-cuts/ (or --output-dir if specified).
    """
    from image_drift.production_cuts import PRODUCTION_CUTS_CSV, export_production_cuts, read_cut_specs

    cfg = resolve_project(project)
    cuts = read_cut_specs(cfg.asset_dir / "production-cuts" / PRODUCTION_CUTS_CSV)
    target_dir = output_dir or (cfg.asset_dir / "production-cuts")
    builder = load_builder_module(cfg.builder_module)
    written = export_production_cuts(cuts, target_dir, master_svg=cfg.svg_path, build_variant=builder.render)
    console.print(f"[bold green]✓ Exported {len(written)} {cfg.name} cuts to:[/] {target_dir.resolve()}")
