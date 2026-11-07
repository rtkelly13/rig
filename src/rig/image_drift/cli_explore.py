"""Image-drift commands that explore, promote and inspect SVG variations."""

from __future__ import annotations

import hashlib
from dataclasses import dataclass, replace
from datetime import UTC, datetime
from pathlib import Path
from typing import TYPE_CHECKING, Annotated

import typer
from rich.table import Table

from rig.image_drift.builder_contract import FeatureRegion
from rig.image_drift.cli_base import (
    DEFAULT_PROJECT,
    PROJECT_NAME,
    SCRATCH_DIR,
    ProjectConfig,
    app,
    console,
    feature_boxes,
    load_builder_module,
    resolve_project,
)
from rig.image_drift.imaging import (
    chromium_available,
    render_svg,
)
from rig.image_drift.labs_builder import (
    FEATURE_REGIONS as _DEFAULT_FEATURE_REGIONS,
)
from rig.image_drift.metrics import (
    CandidateScore,
    DriftResult,
    compute_drift,
    score_candidate,
)

if TYPE_CHECKING:
    pass


@dataclass(frozen=True, slots=True)
class SvgScore:
    """A rendered SVG's composite score and its full-frame drift."""

    score: CandidateScore
    drift: DriftResult


def _score_svg(
    svg_text: str,
    reference: Path,
    regions: list[FeatureRegion],
    scratch: Path,
    label: str,
) -> SvgScore:
    """Render `svg_text` and score it against `reference` in one pass.

    Every candidate and the incumbent go through this same function so their
    figures are comparable: `score_candidate` normalises onto a fixed canvas, so
    scores from different passes or sizes are not.
    """
    scratch.mkdir(parents=True, exist_ok=True)
    svg_path = scratch / f"{label}.svg"
    png_path = scratch / f"{label}.png"
    svg_path.write_text(svg_text, encoding="utf-8", newline="\n")
    render_svg(svg_path, png_path)
    score = score_candidate(reference, png_path, feature_boxes(regions))
    drift = compute_drift(reference, png_path)
    return SvgScore(score=score, drift=drift)


def _labs_project(project: str) -> ProjectConfig:
    """Resolve `project`, refusing anything but the labs mark; explore and promote only drive labs_builder."""
    cfg = resolve_project(project)
    if cfg.name != PROJECT_NAME:
        console.print(f"[bold red]Error:[/] Only '{PROJECT_NAME}' supports explore and promote, not '{cfg.name}'.")
        raise typer.Exit(code=1)
    return cfg


@app.command()
def explore(
    project: Annotated[str, typer.Option("--project", "-p", help="Target project brand.")] = DEFAULT_PROJECT,
    preset: Annotated[str, typer.Option("--preset", help="Explorer preset to start from.")] = "baseline",
    param: Annotated[
        list[str] | None,
        typer.Option("--param", help="Override one parameter as KEY=VALUE. Repeatable."),
    ] = None,
    sweep: Annotated[
        list[str] | None,
        typer.Option("--sweep", help="Sweep a parameter as KEY=A,B,C. Repeatable; combinations are crossed."),
    ] = None,
    max_candidates: Annotated[int, typer.Option("--max-candidates", help="Cap on the sweep product.")] = 24,
    run_id: Annotated[str | None, typer.Option("--run-id", help="Snapshot run id. Defaults to a timestamp.")] = None,
    variations_dir: Annotated[Path, typer.Option("--variations-dir", help="Git-ignored snapshot root.")] = Path(
        "temp/variations"
    ),
) -> None:
    """Propose parametric candidates and score them against the incumbent master.

    Read-only with respect to `assets/` and the packaged builder assets: every
    candidate lands in the git-ignored variations tree. Promotion is a separate,
    explicit step.

    Exit codes: 0 a candidate beat the incumbent, 2 none did, 1 an error.
    """
    from itertools import product

    from rig.image_drift import labs_parametric
    from rig.image_drift.promotion import evaluate_promotion
    from rig.image_drift.snapshots import SnapshotManager

    cfg = _labs_project(project)
    if not chromium_available():
        console.print("[bold red]Error:[/] Chromium is required to score candidates. Install it, then re-run.")
        raise typer.Exit(code=1)
    if preset not in labs_parametric.PRESETS:
        known = ", ".join(sorted(labs_parametric.PRESETS))
        console.print(f"[bold red]Error:[/] Unknown preset '{preset}'. Available: {known}")
        raise typer.Exit(code=1)

    base = labs_parametric.PRESETS[preset]
    try:
        if param:
            base = base.with_overrides(dict(item.split("=", 1) for item in param))
        axes: dict[str, list[str]] = {}
        for item in sweep or []:
            key, values = item.split("=", 1)
            axes[key] = [v.strip() for v in values.split(",") if v.strip()]
    except (KeyError, ValueError) as exc:
        console.print(f"[bold red]Error:[/] Bad parameter: {exc}")
        raise typer.Exit(code=1) from exc

    combos: list[dict[str, str]] = [{}]
    if axes:
        keys = list(axes)
        combos = [dict(zip(keys, values, strict=True)) for values in product(*(axes[k] for k in keys))]
    if len(combos) > max_candidates:
        console.print(f"[bold red]Error:[/] Sweep yields {len(combos)} candidates, over --max-candidates.")
        raise typer.Exit(code=1)

    rid = run_id or f"explore-{datetime.now(UTC).strftime('%Y%m%d-%H%M%S')}"
    manager = SnapshotManager(variations_root=variations_dir.resolve())
    scratch = SCRATCH_DIR / "explore" / rid
    regions = labs_parametric.FEATURE_REGIONS

    incumbent_svg = cfg.svg_path.read_text(encoding="utf-8")
    inc_scored = _score_svg(incumbent_svg, cfg.reference_path, regions, scratch, "incumbent")
    inc_score, inc_drift = inc_scored.score, inc_scored.drift
    manager.capture_step(
        project=cfg.name,
        run_id=rid,
        step_idx=0,
        step_name="incumbent",
        svg_content=incumbent_svg,
        metrics={"composite_pct": inc_score.composite_pct, "ssim": inc_drift.ssim},
        notes="current governed master",
    )
    console.print(
        f"[cyan]Incumbent:[/] composite [bold]{inc_score.composite_pct:.3f}%[/] · SSIM [bold]{inc_drift.ssim:.4f}[/]"
    )

    table = Table(title=f"[ PARAMETRIC_EXPLORATION — {rid} ]", border_style="cyan")
    table.add_column("#", justify="right")
    table.add_column("Overrides")
    table.add_column("Composite", justify="right")
    table.add_column("SSIM", justify="right")
    table.add_column("Verdict", justify="center")

    winners: list[int] = []
    for idx, overrides in enumerate(combos, start=1):
        try:
            params = base.with_overrides(overrides) if overrides else base
        except (KeyError, ValueError) as exc:
            console.print(f"[bold red]Error:[/] Bad sweep value: {exc}")
            raise typer.Exit(code=1) from exc

        svg_text = labs_parametric.generate_svg(params=params)
        scored = _score_svg(svg_text, cfg.reference_path, regions, scratch, f"candidate-{idx}")
        score, drift = scored.score, scored.drift
        verdict = evaluate_promotion(inc_score, inc_drift, score, drift)
        manager.capture_step(
            project=cfg.name,
            run_id=rid,
            step_idx=idx,
            step_name=f"candidate-{idx}",
            svg_content=svg_text,
            metrics={
                "composite_pct": score.composite_pct,
                "ssim": drift.ssim,
                "promotable": verdict.promotable,
            },
            notes=f"preset={preset} overrides={overrides or '{}'}",
        )
        if verdict.promotable:
            winners.append(idx)
        table.add_row(
            str(idx),
            ", ".join(f"{k}={v}" for k, v in overrides.items()) or f"preset:{preset}",
            f"{score.composite_pct:.3f}%",
            f"{drift.ssim:.4f}",
            "[green]PROMOTABLE[/]" if verdict.promotable else "[dim]rejected[/]",
        )

    console.print(table)
    console.print(f"Snapshots: [bold]{(variations_dir / cfg.name / rid).resolve()}[/]")

    if not winners:
        console.print("[yellow]No candidate beat the incumbent. The master stands.[/]")
        raise typer.Exit(code=2)
    console.print(
        f"[bold green]✓[/] Promotable candidates: {winners}. "
        f"Review, then: [bold]image-reconcile promote -p {cfg.name} --run-id {rid} --candidate <N> --confirm[/]"
    )


@app.command()
def promote(
    project: Annotated[str, typer.Option("--project", "-p", help="Target project brand.")] = DEFAULT_PROJECT,
    run_id: Annotated[str, typer.Option("--run-id", help="Exploration run holding the candidate.")] = "",
    candidate: Annotated[int, typer.Option("--candidate", help="Candidate step index within the run.")] = 0,
    confirm: Annotated[bool, typer.Option("--confirm", help="Actually write the new master.")] = False,
    variations_dir: Annotated[Path, typer.Option("--variations-dir", help="Git-ignored snapshot root.")] = Path(
        "temp/variations"
    ),
) -> None:
    """Replace the governed master with an explored candidate that beats it.

    The gate is re-run live against the current master rather than trusted from
    the run's stored report: scores are only comparable within a single scoring
    pass, and the master may have moved since the exploration.

    Exit codes: 0 promoted, 2 the candidate does not (or no longer) win, 1 an error.
    """
    from rig.image_drift.labs_builder import (
        BUILDER_ASSETS,
        DEFAULT_VARIANT,
        Promotion,
        build_svg_logo,
        manifest,
        reload_builder_assets,
    )
    from rig.image_drift.promotion import evaluate_promotion
    from rig.image_drift.snapshots import SnapshotManager

    cfg = _labs_project(project)
    if not run_id:
        console.print("[bold red]Error:[/] --run-id is required.")
        raise typer.Exit(code=1)
    if not chromium_available():
        console.print("[bold red]Error:[/] Chromium is required to re-run the gate. Install it, then re-run.")
        raise typer.Exit(code=1)

    manager = SnapshotManager(variations_root=variations_dir.resolve())
    run = manager.get_run(cfg.name, run_id)
    if run is None:
        console.print(f"[bold red]Error:[/] No run '{run_id}' for project '{cfg.name}'.")
        raise typer.Exit(code=1)
    step = next((s for s in run.steps if s.step_idx == candidate and s.svg_path), None)
    if step is None or step.svg_path is None:
        console.print(f"[bold red]Error:[/] Run '{run_id}' has no candidate {candidate} with an SVG.")
        raise typer.Exit(code=1)

    candidate_svg = step.svg_path.read_text(encoding="utf-8")
    incumbent_svg = cfg.svg_path.read_text(encoding="utf-8")
    scratch = SCRATCH_DIR / "promote" / run_id
    regions = _DEFAULT_FEATURE_REGIONS

    inc_scored = _score_svg(incumbent_svg, cfg.reference_path, regions, scratch, "incumbent")
    inc_score, inc_drift = inc_scored.score, inc_scored.drift
    cand_scored = _score_svg(candidate_svg, cfg.reference_path, regions, scratch, "candidate")
    cand_score, cand_drift = cand_scored.score, cand_scored.drift
    verdict = evaluate_promotion(inc_score, inc_drift, cand_score, cand_drift)

    console.print(
        f"[cyan]Incumbent:[/] composite {inc_score.composite_pct:.3f}% · SSIM {inc_drift.ssim:.4f}\n"
        f"[cyan]Candidate:[/] composite {cand_score.composite_pct:.3f}% · SSIM {cand_drift.ssim:.4f}"
    )
    if not verdict.promotable:
        console.print("[bold red]✗ Candidate does not beat the incumbent:[/]")
        for reason in verdict.reasons:
            console.print(f"  · {reason}")
        raise typer.Exit(code=2)

    master_file = BUILDER_ASSETS / manifest().variants[DEFAULT_VARIANT].file
    if not confirm:
        console.print(
            "[yellow]Dry run.[/] Would replace:\n"
            f"  {master_file}\n  {cfg.svg_path}\n"
            "Re-run with [bold]--confirm[/] to write."
        )
        raise typer.Exit(code=1)

    backup = (variations_dir / cfg.name / run_id / "incumbent-backup.svg").resolve()
    backup.parent.mkdir(parents=True, exist_ok=True)
    backup.write_text(incumbent_svg, encoding="utf-8", newline="\n")

    master_file.write_text(candidate_svg, encoding="utf-8", newline="\n")
    data = manifest()
    promoted = replace(
        data.variants[DEFAULT_VARIANT],
        sha256=hashlib.sha256(master_file.read_bytes()).hexdigest(),
        promoted_from=Promotion(run_id=run_id, candidate=candidate, notes=step.notes),
    )
    updated = replace(data, variants=data.variants | {DEFAULT_VARIANT: promoted})
    (BUILDER_ASSETS / "manifest.json").write_text(updated.to_json(), encoding="utf-8")

    # Keep the committed asset in lockstep with the packaged master.
    reload_builder_assets()
    build_svg_logo(cfg.svg_path, variant=DEFAULT_VARIANT)

    console.print(
        f"[bold green]✓ Promoted.[/] Incumbent backed up to {backup}\n"
        f"Review before committing:\n  git diff -- {master_file} {cfg.svg_path}\n"
        f"Then regenerate cuts: [bold]image-reconcile export-avatar -p {cfg.name}[/]"
    )


@app.command()
def edges(
    project: Annotated[str, typer.Option("--project", "-p", help="Target project brand.")] = DEFAULT_PROJECT,
    svg: Annotated[
        Path | None, typer.Option("--svg", "-s", help="SVG to analyse. Defaults to the project master.")
    ] = None,
    axes: Annotated[
        str,
        typer.Option(
            "--axes", help="Comma-separated canonical edge angles. Default is the pointy-top hexagonal lattice."
        ),
    ] = "0,30,60,90,120,150",
    angle_tol: Annotated[
        float, typer.Option("--angle-tol", help="Max degrees off-axis still treated as jitter to snap.")
    ] = 6.0,
    min_segment: Annotated[
        float, typer.Option("--min-segment", help="Segments shorter than this are stepping artefacts.")
    ] = 5.0,
    limit: Annotated[int, typer.Option("--limit", help="How many individual defects to list.")] = 10,
    min_conformance: Annotated[
        float | None,
        typer.Option(
            "--min-conformance",
            help="Exit non-zero if axis conformance falls below this percentage. For use as a CI ratchet.",
        ),
    ] = None,
    fix: Annotated[
        Path | None, typer.Option("--fix", help="Write a regularized copy to this path and score it.")
    ] = None,
) -> None:
    """Report how well a mark's edges conform to its canonical axes.

    A traced mark inherits the tracer's stepping: edges land a degree or two
    off the true isometric axis and short jogs appear where it stepped around a
    pixel corner. Both read as jagged edges at high zoom. This measures that
    directly, rather than inferring it from drift against the reference.

    Note that conformance and drift are *different* axes of quality. The
    reference raster has imperfect edges of its own, so snapping to exact axes
    usually costs a little SSIM while making the mark geometrically correct.
    That trade is a design decision, which is why --fix scores but never
    promotes.
    """
    from rig.image_drift.edge_regularity import analyze_edges, regularize_svg

    cfg = resolve_project(project)
    target = svg or cfg.svg_path
    if not target.exists():
        console.print(f"[bold red]Error:[/] SVG '{target}' does not exist.")
        raise typer.Exit(code=1)

    try:
        axis_tuple = tuple(float(a.strip()) for a in axes.split(",") if a.strip())
    except ValueError as exc:
        console.print(f"[bold red]Error:[/] Bad --axes value: {exc}")
        raise typer.Exit(code=1) from exc
    if not axis_tuple:
        console.print("[bold red]Error:[/] --axes must name at least one angle.")
        raise typer.Exit(code=1)

    source = target.read_text(encoding="utf-8")
    report = analyze_edges(source, axes=axis_tuple, angle_tol=angle_tol, min_segment=min_segment)

    table = Table(title=f"[ EDGE_REGULARITY — {target.name} ]", border_style="cyan")
    table.add_column("Measure", style="dim")
    table.add_column("Value", justify="right")
    table.add_column("Meaning")
    table.add_row(
        "Axis conformance",
        f"{report.axis_conformance_pct:.1f}%",
        f"edge length lying on {'/'.join(f'{a:g}°' for a in axis_tuple)}",
    )
    table.add_row("Total edges", str(report.total_edges), "polygon segments examined")
    table.add_row(
        "Micro segments", str(report.count("micro_segment")), f"shorter than {min_segment:g}px — tracer stepping"
    )
    table.add_row("Off-axis edges", str(report.count("off_axis")), f"within {angle_tol:g}° of an axis but not on it")
    table.add_row("Staircase runs", str(report.count("staircase")), "short alternating jogs along one line")
    console.print(table)

    if report.defects and limit > 0:
        console.print("\n[bold]Worst offenders[/] (by deviation, then length):")
        ranked = sorted(report.defects, key=lambda d: (-abs(d.deviation), -d.length))
        for defect in ranked[:limit]:
            console.print(f"  · {defect.describe()}")

    if min_conformance is not None and report.axis_conformance_pct < min_conformance:
        console.print(
            f"\n[bold red]✗ Axis conformance {report.axis_conformance_pct:.2f}% "
            f"is below the required {min_conformance:.2f}%.[/]"
        )
        raise typer.Exit(code=1)

    if fix is None:
        return

    fixed_text = regularize_svg(source, axes=axis_tuple, angle_tol=angle_tol, min_segment=min_segment)
    fix.parent.mkdir(parents=True, exist_ok=True)
    fix.write_text(fixed_text, encoding="utf-8")
    after = analyze_edges(fixed_text, axes=axis_tuple, angle_tol=angle_tol, min_segment=min_segment)

    console.print(
        f"\n[bold green]✓[/] Wrote regularized SVG to [bold]{fix}[/]\n"
        f"  Axis conformance: {report.axis_conformance_pct:.1f}% → "
        f"[bold]{after.axis_conformance_pct:.1f}%[/]\n"
        f"  Micro segments:   {report.count('micro_segment')} → {after.count('micro_segment')}\n"
        f"  Off-axis edges:   {report.count('off_axis')} → {after.count('off_axis')}"
    )

    if chromium_available():
        scratch = SCRATCH_DIR / "edges"
        regions = load_builder_module(cfg.builder_module).feature_regions
        before_scored = _score_svg(source, cfg.reference_path, regions, scratch, "before")
        before_score, before_drift = before_scored.score, before_scored.drift
        after_scored = _score_svg(fixed_text, cfg.reference_path, regions, scratch, "after")
        after_score, after_drift = after_scored.score, after_scored.drift
        console.print(
            f"  Drift vs reference: SSIM {before_drift.ssim:.4f} → {after_drift.ssim:.4f}, "
            f"composite {before_score.composite_pct:.3f}% → {after_score.composite_pct:.3f}%"
        )
        if after_drift.ssim < before_drift.ssim:
            console.print(
                "  [yellow]Fidelity fell.[/] The reference raster's own edges are not exactly on-axis, "
                "so geometric correctness and reference fidelity pull apart here. Judge by eye, not by SSIM."
            )
    console.print("\n[dim]Nothing was promoted. Review, then use `promote` if you want this to become the master.[/]")


@app.command()
def pathways(
    project: Annotated[str, typer.Option("--project", "-p", help="Target project brand.")] = DEFAULT_PROJECT,
    only: Annotated[str | None, typer.Option("--only", help="Comma-separated pathway keys. Defaults to all.")] = None,
    axes: Annotated[str, typer.Option("--axes", help="Canonical edge angles, comma-separated.")] = "0,30,60,90,120,150",
    out_dir: Annotated[
        Path | None, typer.Option("--out-dir", help="Write each pathway's SVG here for inspection.")
    ] = None,
) -> None:
    """Build the mark by every available pathway and compare them on both axes.

    Fidelity and geometric correctness pull against each other: the trace wins
    on drift because it followed the reference's pixels, and loses on edge
    quality for exactly the same reason. Rather than pick one, this builds each
    candidate and puts the trade in one table.

    Nothing is promoted. Use `promote` for that.
    """
    from rig.image_drift.edge_regularity import analyze_edges
    from rig.image_drift.pathways import PATHWAYS, PathwayContext

    cfg = resolve_project(project)
    try:
        axis_tuple = tuple(float(a.strip()) for a in axes.split(",") if a.strip())
    except ValueError as exc:
        console.print(f"[bold red]Error:[/] Bad --axes value: {exc}")
        raise typer.Exit(code=1) from exc

    keys = [k.strip() for k in only.split(",")] if only else list(PATHWAYS)
    unknown = [k for k in keys if k not in PATHWAYS]
    if unknown:
        console.print(f"[bold red]Error:[/] Unknown pathway(s) {unknown}. Available: {list(PATHWAYS)}")
        raise typer.Exit(code=1)

    scoring = chromium_available()
    if not scoring:
        console.print("[yellow]Chromium unavailable:[/] reporting edge quality only; drift columns omitted.")

    scratch = SCRATCH_DIR / "pathways"
    regions = load_builder_module(cfg.builder_module).feature_regions
    master_svg = cfg.svg_path.read_text(encoding="utf-8")

    def objective(svg_text: str) -> float:
        return _score_svg(svg_text, cfg.reference_path, regions, scratch, "fit").score.composite_pct

    ctx = PathwayContext(
        master_svg=master_svg,
        reference_path=cfg.reference_path,
        axes=axis_tuple,
        score=objective if scoring else None,
    )

    table = Table(title=f"[ PATHWAYS — {cfg.name} ]", border_style="cyan")
    table.add_column("Pathway", style="bold cyan")
    table.add_column("Conformance", justify="right")
    table.add_column("Edges", justify="right")
    table.add_column("Defects", justify="right")
    if scoring:
        table.add_column("SSIM", justify="right")
        table.add_column("Composite", justify="right")

    for key in keys:
        pathway = PATHWAYS[key]
        if pathway.needs_scoring and not scoring:
            console.print(f"[dim]Skipping '{key}': needs a renderer.[/]")
            continue
        console.print(f"[dim]Building {key}…[/]")
        svg_text = pathway.generate(ctx)
        report = analyze_edges(svg_text, axes=axis_tuple)
        defects = report.count("micro_segment") + report.count("off_axis") + report.count("staircase")
        row = [
            pathway.name,
            f"{report.axis_conformance_pct:.1f}%",
            str(report.total_edges),
            str(defects),
        ]
        if scoring:
            scored = _score_svg(svg_text, cfg.reference_path, regions, scratch, f"pathway-{key}")
            score, drift = scored.score, scored.drift
            row += [f"{drift.ssim:.4f}", f"{score.composite_pct:.3f}%"]
        table.add_row(*row)

        if out_dir is not None:
            out_dir.mkdir(parents=True, exist_ok=True)
            (out_dir / f"{cfg.name}-{key}.svg").write_text(svg_text, encoding="utf-8", newline="\n")

    console.print(table)
    if out_dir is not None:
        console.print(f"Candidates written to [bold]{out_dir.resolve()}[/]")
    console.print(
        "\n[dim]Nothing promoted. Higher conformance is cleaner geometry; higher SSIM is closer to the reference.[/]"
    )
