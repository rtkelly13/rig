"""Image-drift commands that vectorize, export icons and render HTML galleries."""

from __future__ import annotations

import shutil
from dataclasses import dataclass, replace
from pathlib import Path
from typing import TYPE_CHECKING, Annotated

import typer
from jinja2 import Environment, FileSystemLoader, select_autoescape
from PIL.Image import Image as ImageType
from rich.panel import Panel

from rig.image_drift.builder_contract import variant_style
from rig.image_drift.cli_base import (
    APPLE_TOUCH_SIZE,
    CHOICE_SIZES,
    DEFAULT_OUTPUT_DIR,
    DEFAULT_PROJECT,
    FAVICON_ICO_SIZES,
    FAVICON_MASTER_SIZE,
    FAVICON_PNG_SIZES,
    PROJECT_NAME,
    TEMPLATE_DIR,
    Builder,
    app,
    console,
    feature_boxes,
    feature_map,
    load_builder_module,
    resolve_project,
)
from rig.image_drift.imaging import (
    Panel as SheetPanel,
)
from rig.image_drift.imaging import (
    comparison_sheet,
    contact_sheet,
    crop,
    data_uri,
    image_data_uri,
    load_normalised,
    mime_for,
    render_svg,
    screenshot_page,
    thumbnail,
)
from rig.image_drift.metrics import (
    FRAME_WEIGHT,
    TIE_THRESHOLD_PCT,
    compute_drift,
    score_candidate,
)
from rig.image_drift.variants import Variant

if TYPE_CHECKING:
    pass


@app.command()
def vectorize(
    image: Annotated[Path, typer.Option("--image", "-i", help="Path to input raster image (PNG/JPG)")],
    output: Annotated[Path, typer.Option("--output", "-o", help="Path to output SVG file")] = Path("vectorized.svg"),
    mode: Annotated[
        str,
        typer.Option(
            "--mode",
            "-m",
            help="Vectorization geometry mode: 'polygon' (isometric/sharp edges) or 'spline' (curves)",
        ),
    ] = "polygon",
    color_precision: Annotated[
        int,
        typer.Option(
            "--color-precision",
            "-c",
            help="Color precision bits (1-8). Default 5 for clean, low-drift SVGs.",
        ),
    ] = 5,
    layer_difference: Annotated[
        int,
        typer.Option("--layer-difference", "-l", help="Color difference threshold between layers. Default 20."),
    ] = 20,
    filter_speckle: Annotated[
        int,
        typer.Option("--filter-speckle", help="Discard noise speckles below this pixel area"),
    ] = 4,
    corner_threshold: Annotated[
        int,
        typer.Option("--corner-threshold", help="Corner smoothness threshold in degrees"),
    ] = 60,
    length_threshold: Annotated[
        float,
        typer.Option("--length-threshold", help="Minimum segment length threshold"),
    ] = 4.0,
    max_iterations: Annotated[
        int,
        typer.Option("--max-iterations", help="Curve fitting iteration limit"),
    ] = 10,
    path_precision: Annotated[
        int,
        typer.Option("--path-precision", help="Coordinate decimal precision (1-3)"),
    ] = 2,
    hierarchical: Annotated[
        str,
        typer.Option("--hierarchical", help="'stacked' (layered) or 'cutout' (non-overlapping)"),
    ] = "stacked",
    compare_result: Annotated[
        bool,
        typer.Option("--compare/--no-compare", help="Immediately render and score output SVG against input image"),
    ] = True,
    output_sheet: Annotated[
        Path | None,
        typer.Option("--output-sheet", help="Optional path to write a 3-panel comparative diagnostic sheet"),
    ] = None,
) -> None:
    """Deterministically vectorize a raster PNG/JPG to SVG using the VisionCortex VTracer engine."""
    if not image.exists():
        console.print(f"[bold red]Error:[/] Image file '{image}' does not exist.")
        raise typer.Exit(code=1)

    import vtracer

    output.parent.mkdir(parents=True, exist_ok=True)
    msg = (
        f"[cyan]⚡ Vectorizing[/] [bold]{image.name}[/] "
        f"[cyan]in mode='{mode}', CP={color_precision}, LD={layer_difference}...[/]"
    )
    console.print(msg)

    vtracer.convert_image_to_svg_py(
        str(image.resolve()),
        str(output.resolve()),
        colormode="color",
        hierarchical=hierarchical,
        mode=mode,
        filter_speckle=filter_speckle,
        color_precision=color_precision,
        layer_difference=layer_difference,
        corner_threshold=corner_threshold,
        length_threshold=length_threshold,
        max_iterations=max_iterations,
        path_precision=path_precision,
    )
    svg_size_kb = output.stat().st_size / 1024.0
    console.print(f"[bold green]✓ Vectorized SVG written to:[/] {output.resolve()} ({svg_size_kb:.1f} KB)")

    if compare_result:
        temp_png = output.parent / f"_temp_rendered_{output.stem}.png"
        render_svg(output, temp_png)
        temp_diff: Path | None = None
        diff_path = None
        if output_sheet:
            temp_diff = output.parent / f"_temp_diff_{output.stem}.png"
            diff_path = temp_diff

        try:
            res = compute_drift(image, temp_png, diff_output_path=diff_path)
            psnr_str = f"{res.psnr_db:.1f} dB" if res.psnr_db != float("inf") else "∞"
            console.print(
                Panel(
                    f"[bold cyan]Extraction Quality:[/] [yellow]{res.rmse_pct:.2f}%[/] RMSE | "
                    f"[green]{res.ssim * 100.0:.2f}%[/] SSIM ({res.ssim:.3f}) | "
                    f"[magenta]{psnr_str}[/] PSNR\n"
                    f"[bold cyan]Deterministic SVG Size:[/] [green]{svg_size_kb:.1f} KB[/]",
                    title="Vectorization Scoring",
                )
            )
            if output_sheet and diff_path and diff_path.exists():
                ref_img = load_normalised(image)
                rnd_img = load_normalised(temp_png)
                d_img = load_normalised(diff_path)
                sheet = comparison_sheet(ref_img, rnd_img, d_img, rmse_pct=res.rmse_pct, ssim=res.ssim)
                output_sheet.parent.mkdir(parents=True, exist_ok=True)
                sheet.save(output_sheet)
                console.print(f"[green]✓ Comparison sheet saved to:[/] {output_sheet.resolve()}")
        finally:
            if temp_png.exists():
                temp_png.unlink()
            if temp_diff and temp_diff.exists():
                temp_diff.unlink()


@dataclass
class _Candidate:
    key: str
    name: str
    description: str
    composite_pct: float
    full_pct: float
    per_feature: dict[str, float]
    hero_b64: str
    feature_crops: dict[str, str]
    svg_path: Path


@app.command()
def favicon(
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
            help="Directory for the favicon SVG and raster set (defaults to assets/<project>/production-cuts/).",
        ),
    ] = None,
    variant: Annotated[str, typer.Option("--variant", "-v", help="Builder variant to rasterise.")] = "favicon",
    builder_module: Annotated[
        Path | None, typer.Option("--builder-module", "-B", help="Path to the SVG builder Python module.")
    ] = None,
) -> None:
    """Emit the small-size logo cut plus the raster set a docs site needs.

    Below roughly 48px the full logo's emblems become smudges and its circuit
    turns to mud, so the favicon is a distinct variant -- fewer, heavier marks --
    rather than the same drawing scaled down.

    Routes cuts to assets/<project>/production-cuts/ (or --output-dir if given),
    so exported cuts never pollute the project root.
    """
    cfg = resolve_project(project)
    target_dir = output_dir or (cfg.asset_dir / "production-cuts")
    b_mod = builder_module or cfg.builder_module
    builder = load_builder_module(b_mod)
    if builder.variants and variant not in builder.variants:
        console.print(f"[bold red]Error:[/] Unknown variant '{variant}'. Available: {list(builder.variants)}")
        raise typer.Exit(code=1)

    target_dir.mkdir(parents=True, exist_ok=True)
    svg_path = target_dir / "favicon.svg"
    builder.render(svg_path, variant)
    console.print(f"[green]✓ SVG[/] {svg_path.name}")

    # Rasterise once at high resolution, then downsample with a good filter.
    # Rendering tiny sizes directly makes the browser scale the glow badly.
    master = target_dir / "_favicon_master.png"
    render_svg(svg_path, master, FAVICON_MASTER_SIZE, FAVICON_MASTER_SIZE)
    try:
        source = load_normalised(master, FAVICON_MASTER_SIZE)
        for size in FAVICON_PNG_SIZES:
            name = "apple-touch-icon.png" if size == APPLE_TOUCH_SIZE else f"favicon-{size}.png"
            thumbnail(source, size).save(target_dir / name)
            console.print(f"[green]✓ PNG[/] {name}")
        ico_path = target_dir / "favicon.ico"
        thumbnail(source, max(FAVICON_ICO_SIZES)).save(ico_path, sizes=[(size, size) for size in FAVICON_ICO_SIZES])
        console.print(f"[green]✓ ICO[/] {ico_path.name} ({', '.join(str(s) for s in FAVICON_ICO_SIZES)})")
    finally:
        if master.exists():
            master.unlink()

    console.print(f"\n[bold green]✅ Favicon set written to:[/] {target_dir.resolve()}")


@app.command(name="contact-sheet")
def contact_sheet_command(
    project: Annotated[
        str,
        typer.Option(
            "--project",
            "-p",
            help="Target project brand ('rtkelly-labs' or 'parquet-source-generator').",
        ),
    ] = DEFAULT_PROJECT,
    feature: Annotated[
        str | None,
        typer.Option("--feature", "-f", help="Feature region to crop. Omitted, uses the whole logo."),
    ] = None,
    output: Annotated[Path, typer.Option("--output", "-o", help="Where to write the sheet PNG.")] = Path(
        "contact_sheet.png"
    ),
    reference: Annotated[Path | None, typer.Option("--reference", "-r", help="Canonical reference image.")] = None,
    variants: Annotated[
        str | None,
        typer.Option("--variants", help="Comma-separated variant keys. Omitted, uses all of them."),
    ] = None,
    cell: Annotated[int, typer.Option("--cell", help="Pixel size of each panel.")] = 200,
    builder_module: Annotated[
        Path | None, typer.Option("--builder-module", "-B", help="Path to the SVG builder Python module.")
    ] = None,
) -> None:
    """Compose a reference-vs-candidates comparison sheet as a single PNG.

    The same composition the preview gallery uses, available standalone so
    inspecting a detail does not mean writing another throwaway script.
    """
    cfg = resolve_project(project)
    ref_path = reference or cfg.reference_path
    b_mod = builder_module or cfg.builder_module
    builder = load_builder_module(b_mod)

    if not ref_path.exists():
        console.print(f"[bold red]Error:[/] Reference image '{ref_path}' does not exist.")
        raise typer.Exit(code=1)

    regions = feature_map(builder.feature_regions)
    if feature is not None and feature not in regions:
        console.print(f"[bold red]Error:[/] Unknown feature '{feature}'. Available: {list(regions)}")
        raise typer.Exit(code=1)

    keys = [key.strip() for key in variants.split(",")] if variants else list(builder.variants)
    unknown = [key for key in keys if key not in builder.variants]
    if unknown:
        console.print(f"[bold red]Error:[/] Unknown variant(s): {unknown}")
        raise typer.Exit(code=1)

    region = regions.get(feature) if feature else None
    scratch = output.parent / "_sheet_tmp"
    scratch.mkdir(parents=True, exist_ok=True)
    try:
        ref_img = load_normalised(ref_path)
        panels = [
            SheetPanel(
                crop(ref_img, region.ref_bbox) if region else ref_img,
                "reference",
                highlight=True,
            )
        ]
        for key in keys:
            cand_svg = scratch / f"sheet_{key}.svg"
            cand_png = scratch / f"sheet_{key}.png"

            builder.render(cand_svg, key)
            render_svg(cand_svg, cand_png)
            image = load_normalised(cand_png)
            panels.append(SheetPanel(crop(image, region.bbox) if region else image, key))
            console.print(f"  [cyan]▸ {key}[/]")

        sheet = contact_sheet(panels, cell=cell, columns=len(panels))
        output.parent.mkdir(parents=True, exist_ok=True)
        sheet.save(output)
    finally:
        shutil.rmtree(scratch, ignore_errors=True)

    label = feature or "full logo"
    console.print(f"\n[green]✨ Contact sheet ({label}, {len(panels)} panels) written to:[/] {output.resolve()}")


@app.command(name="build-preview")
def build_preview(
    project: Annotated[
        str,
        typer.Option(
            "--project",
            "-p",
            help="Target project brand ('rtkelly-labs' or 'parquet-source-generator').",
        ),
    ] = DEFAULT_PROJECT,
    builder_module: Annotated[
        Path | None,
        typer.Option("--builder-module", "-B", help="Path to the SVG builder Python module."),
    ] = None,
    svg: Annotated[
        Path | None,
        typer.Option("--svg", "-s", help="Path where the target SVG file will be written."),
    ] = None,
    reference: Annotated[
        Path | None,
        typer.Option("--reference", "-r", help="Path to the canonical reference image to diff against."),
    ] = None,
    output_dir: Annotated[
        Path,
        typer.Option("--output-dir", "-o", help="Directory where preview.html and screenshot.png are written."),
    ] = DEFAULT_OUTPUT_DIR,
    waves: Annotated[
        bool,
        typer.Option("--waves/--single", help="Generate the multi-candidate Wave Preview Gallery."),
    ] = True,
    take_screenshot: Annotated[
        bool,
        typer.Option("--screenshot/--no-screenshot", help="Capture screenshot.png of the rendered preview page."),
    ] = True,
    variants: Annotated[
        str | None,
        typer.Option(
            "--variants",
            help="Comma-separated variant keys to include. Omitted, uses all of them.",
        ),
    ] = None,
    mode: Annotated[
        str,
        typer.Option(
            "--mode",
            "-m",
            help="'choose' for a selection gallery (no scores), 'drift' for the scored wave gallery.",
        ),
    ] = "drift",
) -> None:
    """Render every candidate, score it against the reference, and build the gallery."""
    cfg = resolve_project(project)
    b_mod = builder_module or cfg.builder_module
    target_svg = svg or cfg.svg_path
    ref_path = reference or cfg.reference_path

    if not ref_path.exists():
        console.print(f"[bold red]Error:[/] Reference image '{ref_path}' does not exist.")
        raise typer.Exit(code=1)

    builder = load_builder_module(b_mod)
    output_dir.mkdir(parents=True, exist_ok=True)
    tmp = output_dir / "_preview_tmp"
    tmp.mkdir(exist_ok=True)

    ref_img = load_normalised(ref_path)
    ref_hero_b64 = data_uri(ref_path, mime_for(ref_path))

    if variants:
        keys = [key.strip() for key in variants.split(",")]
        unknown = [key for key in keys if key not in builder.variants]
        if unknown:
            console.print(f"[bold red]Error:[/] Unknown variant(s): {unknown}")
            raise typer.Exit(code=1)
        builder = replace(builder, variants={key: builder.variants[key] for key in keys})

    if mode not in ("choose", "drift"):
        console.print(f"[bold red]Error:[/] Unknown mode '{mode}'. Use 'choose' or 'drift'.")
        raise typer.Exit(code=1)

    try:
        if mode == "choose":
            html = _build_choose_gallery(builder, ref_path, ref_img, ref_hero_b64, builder.default_variant, tmp)
        elif waves and builder.variants:
            html = _build_wave_gallery(builder, ref_path, ref_img, ref_hero_b64, target_svg, tmp)
        else:
            html = _build_single_gallery(builder, ref_path, ref_hero_b64, target_svg, tmp)

        preview_html = output_dir / "preview.html"
        preview_html.write_text(html, encoding="utf-8")
        console.print(f"[green]✨ preview.html written to:[/] {preview_html.resolve()}")

        if take_screenshot:
            console.print("[bold blue]📸 Capturing screenshot.png …[/]")
            screenshot_path = output_dir / "screenshot.png"

            screenshot_page(preview_html, screenshot_path)
            console.print(f"[green]✨ screenshot.png written to:[/] {screenshot_path.resolve()}")
    finally:
        shutil.rmtree(tmp, ignore_errors=True)

    console.print("\n[bold green]✅ Preview complete — all outputs are deterministic.[/]")


def _template_env() -> Environment:
    """Jinja environment over the packaged template directory.

    Autoescaping is on: candidate names, descriptions and region labels are
    plain text, so escaping is the template's job rather than the data's.
    """
    return Environment(
        loader=FileSystemLoader(TEMPLATE_DIR),
        autoescape=select_autoescape(default_for_string=True, default=True),
        trim_blocks=True,
        lstrip_blocks=True,
        keep_trailing_newline=True,
    )


def _badge_class(composite_pct: float) -> str:
    """Tailwind component class for a score badge (see styles/gallery.src.css)."""
    if composite_pct < 20.0:
        return "badge-good"
    return "badge-warn" if composite_pct < 28.0 else "badge-bad"


def _render_gallery(template_name: str, **context: object) -> str:
    """Render a preview page to a self-contained HTML string."""
    return _template_env().get_template(template_name).render(**context)


@dataclass(frozen=True, slots=True)
class _SizeChip:
    """One small-size render of an image."""

    size: int
    uri: str


def _size_chips(image: ImageType, tmp: Path, prefix: str) -> list[_SizeChip]:
    """Small-size renders, the decisive test for a logo."""
    return [_SizeChip(size, image_data_uri(thumbnail(image, size), tmp, f"{prefix}_{size}")) for size in CHOICE_SIZES]


@dataclass(frozen=True, slots=True)
class _ChoiceView:
    """One candidate as the choice gallery shows it."""

    key: str
    name: str
    description: str
    hero: str
    sizes: list[_SizeChip]
    spec: list[_SpecRow]
    is_current: bool


@dataclass(frozen=True, slots=True)
class _SpecRow:
    """One labelled setting in a variant's summary."""

    label: str
    value: str


def _variant_spec(builder: Builder, key: str) -> list[_SpecRow]:
    """Human-readable summary of what makes this variant different."""
    info = builder.variants.get(key, Variant())
    style = variant_style(info)
    spec: list[_SpecRow] = []
    if info.tiling is not None:
        spec.append(_SpecRow("tiling", info.tiling))
    if info.cell is not None:
        spec.append(_SpecRow("cell", f"{info.cell:g}"))
    if info.tone is not None:
        spec.append(_SpecRow("tone", info.tone))
    spec.extend(
        _SpecRow(label, f"{value:g}")
        for label, value in (
            ("frame", style.frame_width),
            ("trace", style.trace_width),
            ("ring", style.ring_ratio),
            ("dots", style.node_scale),
            ("bloom", style.glow_wide),
        )
    )
    return spec


def _build_choose_gallery(
    builder: Builder,
    reference: Path,
    ref_img: ImageType,
    ref_hero_b64: str,
    current_variant: str | None,
    tmp: Path,
) -> str:
    """Selection-focused gallery: no scores, no ranking, one adopt command each.

    Deliberately omits drift. Drift measures similarity to a raster the tiling
    departs from by design, so surfacing it here would rank the options by the
    one thing that must not decide them.
    """
    console.print(f"\n[bold magenta]🎨 Building choice gallery ({len(builder.variants)} candidates)…[/]")

    socket_region = next(
        (fr for fr in builder.feature_regions if fr.key == "top_right_socket"),
        builder.feature_regions[0] if builder.feature_regions else None,
    )
    candidates: list[_ChoiceView] = []
    detail_panels: list[SheetPanel] = []
    if socket_region is not None:
        detail_panels.append(SheetPanel(crop(ref_img, socket_region.ref_bbox), "reference", highlight=True))

    for key, info in builder.variants.items():
        cand_svg = tmp / f"choice_{key}.svg"
        cand_png = tmp / f"choice_{key}.png"
        builder.render(cand_svg, key)
        render_svg(cand_svg, cand_png)
        image = load_normalised(cand_png)

        candidates.append(
            _ChoiceView(
                key=key,
                name=info.name or key,
                description=info.description,
                hero=data_uri(cand_png),
                sizes=_size_chips(image, tmp, f"chip_{key}"),
                spec=_variant_spec(builder, key),
                is_current=key == current_variant,
            )
        )
        if socket_region is not None:
            label = (info.name or key).split(":", maxsplit=1)[0]
            detail_panels.append(SheetPanel(crop(image, socket_region.bbox), label))
        console.print(f"  [cyan]▸ {info.name or key}[/]")

    socket_sheet = ""
    if detail_panels:
        sheet = contact_sheet(detail_panels, cell=170, columns=len(detail_panels))
        socket_sheet = image_data_uri(sheet, tmp, "socket_sheet")

    return _render_gallery(
        "choose_gallery.html.jinja",
        project_name=PROJECT_NAME,
        reference_name=reference.name,
        reference_hero=ref_hero_b64,
        reference_sizes=_size_chips(ref_img, tmp, "chip_reference"),
        candidates=candidates,
        socket_sheet=socket_sheet,
    )


def _build_wave_gallery(
    builder: Builder,
    reference: Path,
    ref_img: ImageType,
    ref_hero_b64: str,
    svg: Path,
    tmp: Path,
) -> str:
    """Render all candidates, score them honestly, and return gallery HTML."""
    console.print(f"\n[bold magenta]🌊 Candidate Wave Generation ({len(builder.variants)} candidates)…[/]")

    candidates: list[_Candidate] = []
    for idx, (v_key, v_info) in enumerate(builder.variants.items(), start=1):
        cand_svg = tmp / f"candidate_{v_key}.svg"
        cand_png = tmp / f"candidate_{v_key}.png"

        builder.render(cand_svg, v_key)
        render_svg(cand_svg, cand_png)
        score = score_candidate(reference, cand_png, feature_boxes(builder.feature_regions))
        composite, full_pct, per_feature = score.composite_pct, score.full_frame_pct, score.per_feature_pct

        cand_img = load_normalised(cand_png)
        feature_crops: dict[str, str] = {}
        for fr in builder.feature_regions:
            crop_p = tmp / f"crop_{v_key}_{fr.key}.png"
            fr.bbox.crop(cand_img).save(crop_p)
            feature_crops[fr.key] = data_uri(crop_p)

        candidates.append(
            _Candidate(
                key=v_key,
                name=v_info.name or f"Candidate {idx}",
                description=v_info.description,
                composite_pct=composite,
                full_pct=full_pct,
                per_feature=per_feature,
                hero_b64=data_uri(cand_png),
                feature_crops=feature_crops,
                svg_path=cand_svg,
            )
        )
        console.print(f"  [cyan]▸ {v_info.name}:[/] composite [yellow]{composite:.2f}%[/] (frame {full_pct:.2f}%)")

    candidates.sort(key=lambda c: (c.composite_pct, c.key))
    spread = candidates[-1].composite_pct - candidates[0].composite_pct
    tied = spread < TIE_THRESHOLD_PCT

    if tied:
        # Do not present noise as a ranking. Fall back to the builder's own
        # default so the choice is deterministic and explicit.
        chosen = next((c for c in candidates if c.key == builder.default_variant), candidates[0])
        console.print(
            f"\n[bold yellow]⚖️  Candidates are statistically indistinguishable[/] "
            f"(composite spread {spread:.2f}pp < {TIE_THRESHOLD_PCT}pp threshold)."
        )
        console.print(
            f"[bold green]→ Using builder default:[/] [bold cyan]{chosen.name}[/] "
            f"([yellow]{chosen.composite_pct:.2f}%[/] composite)"
        )
    else:
        chosen = candidates[0]
        console.print(
            f"\n[bold green]🏆 Lowest drift:[/] [bold cyan]{chosen.name}[/] "
            f"([yellow]{chosen.composite_pct:.2f}%[/] composite, {spread:.2f}pp ahead of last)"
        )

    # Drift is a diagnostic, not the design target, and the gallery never writes the
    # target: the traced master changes only through `image-reconcile promote` (ADR 0018).
    console.print(
        f"[dim]{svg.name} left untouched. Use[/] [cyan]build --variant {chosen.key}[/] [dim]to adopt this one.[/]"
    )

    ref_crops: dict[str, str] = {}
    for fr in builder.feature_regions:
        ref_crop_p = tmp / f"ref_crop_{fr.key}.png"
        fr.ref_bbox.crop(ref_img).save(ref_crop_p)
        ref_crops[fr.key] = data_uri(ref_crop_p)

    return _render_gallery(
        "wave_gallery.html.jinja",
        project_name=PROJECT_NAME,
        reference_name=reference.name,
        reference_hero=ref_hero_b64,
        reference_crops=ref_crops,
        regions=builder.feature_regions,
        candidates=[_candidate_context(c, is_chosen=c.key == chosen.key) for c in candidates],
        tied=tied,
        spread=spread,
        tie_threshold=TIE_THRESHOLD_PCT,
        frame_weight=int(FRAME_WEIGHT * 100),
        feature_weight=int((1.0 - FRAME_WEIGHT) * 100),
    )


@dataclass(frozen=True, slots=True)
class _CandidateView:
    """A scored candidate flattened into the plain values the template consumes."""

    key: str
    name: str
    short_name: str
    description: str
    composite_pct: float
    full_pct: float
    per_feature: dict[str, float]
    hero: str
    feature_crops: dict[str, str]
    badge_class: str
    is_chosen: bool


def _candidate_context(candidate: _Candidate, *, is_chosen: bool) -> _CandidateView:
    return _CandidateView(
        key=candidate.key,
        name=candidate.name,
        short_name=candidate.name.split(":")[0],
        description=candidate.description,
        composite_pct=candidate.composite_pct,
        full_pct=candidate.full_pct,
        per_feature=candidate.per_feature,
        hero=candidate.hero_b64,
        feature_crops=candidate.feature_crops,
        badge_class=_badge_class(candidate.composite_pct),
        is_chosen=is_chosen,
    )


def _build_single_gallery(builder: Builder, reference: Path, ref_hero_b64: str, svg: Path, tmp: Path) -> str:
    """Single-candidate reference-vs-target comparison page."""
    console.print("[bold blue]Building single target SVG …[/]")
    variant = builder.default_variant
    builder.render(svg, variant)
    rendered_png = tmp / "rendered_svg.png"
    render_svg(svg, rendered_png)

    score = score_candidate(reference, rendered_png, feature_boxes(builder.feature_regions))
    composite, full_pct, per_feature = score.composite_pct, score.full_frame_pct, score.per_feature_pct
    console.print(f"  [cyan]▸ composite[/] [yellow]{composite:.2f}%[/] (frame {full_pct:.2f}%)")

    ref_img = load_normalised(reference)
    svg_img = load_normalised(rendered_png)

    ref_crops: dict[str, str] = {}
    feature_crops: dict[str, str] = {}
    for fr in builder.feature_regions:
        ref_crop_path = tmp / f"ref_{fr.key}.png"
        svg_crop_path = tmp / f"svg_{fr.key}.png"
        fr.ref_bbox.crop(ref_img).save(ref_crop_path)
        fr.bbox.crop(svg_img).save(svg_crop_path)
        ref_crops[fr.key] = data_uri(ref_crop_path)
        feature_crops[fr.key] = data_uri(svg_crop_path)

    info = builder.variants.get(variant or "", Variant())
    candidate = _Candidate(
        key=variant or "target",
        name=info.name or svg.name,
        description=info.description,
        composite_pct=composite,
        full_pct=full_pct,
        per_feature=per_feature,
        hero_b64=data_uri(rendered_png),
        feature_crops=feature_crops,
        svg_path=svg,
    )

    return _render_gallery(
        "single_gallery.html.jinja",
        project_name=PROJECT_NAME,
        reference_name=reference.name,
        reference_hero=ref_hero_b64,
        reference_crops=ref_crops,
        regions=builder.feature_regions,
        candidates=[_candidate_context(candidate, is_chosen=True)],
    )
