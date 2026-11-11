"""SVG Image Drift Reconciler & Visual Feature Isolation Engine.

Generic pipeline engine — decoupled from any specific SVG builder.
Any Python module that exports:

    FEATURE_REGIONS: list[FeatureRegion]
    build_svg_logo(output_path: Path, variant: str = ...) -> None
    VARIANTS: dict[str, Variant] (optional candidate variants for wave generation)
    DEFAULT_VARIANT: str (optional tie-break variant)

can be passed via ``--builder-module`` and this engine will drive
rendering, feature-crop extraction, drift analysis, multi-candidate wave generation,
and preview generation without any code changes here.
"""

from __future__ import annotations

import contextlib
from pathlib import Path
from typing import TYPE_CHECKING, Annotated

import typer
from rich.panel import Panel
from rich.table import Table

from rig.image_drift import cli_analysis, cli_explore, cli_gallery
from rig.image_drift.cli_base import app, console

if TYPE_CHECKING:
    pass

_COMMAND_MODULES = (cli_analysis, cli_gallery, cli_explore)
"""Imported so their commands register on `app`."""

STUDIO_PAGE = "http://127.0.0.1:8090/studio"
"""The rig web host's studio page, which calls this server's API."""


@app.command(name="serve")
def serve_command(
    port: Annotated[
        int,
        typer.Option(
            "--port",
            "-p",
            help="Port to bind the Asset Studio HTTP server to.",
        ),
    ] = 8092,
    host: Annotated[
        str,
        typer.Option(
            "--host",
            "-h",
            help="Host address to bind the Asset Studio HTTP server to.",
        ),
    ] = "127.0.0.1",
    open_browser: Annotated[
        bool,
        typer.Option(
            "--open/--no-open",
            help="Open the rig web host's studio page in the default web browser.",
        ),
    ] = True,
    assets_dir: Annotated[
        Path,
        typer.Option(
            "--assets-dir",
            help="Directory containing project assets.",
        ),
    ] = Path("assets"),
    variations_dir: Annotated[
        Path,
        typer.Option(
            "--variations-dir",
            help="Directory for ephemeral snapshots and variations (git-ignored).",
        ),
    ] = Path("temp/variations"),
) -> None:
    """Serve Asset Studio's API for the rig web host's studio page (`/studio`)."""
    import webbrowser

    from rig.image_drift.server import AssetStudioServer

    server_url = f"http://{host}:{port}"
    console.print(
        Panel(
            f"[bold cyan]Asset Studio // Estate Visual Workbench[/]\n\n"
            f"API running at: [bold green]{server_url}[/]\n"
            f"Studio page: [bold green]{STUDIO_PAGE}[/] (the rig web host)\n"
            f"Assets Root: [dim]{assets_dir.resolve()}[/]\n"
            f"Variations Root (Git-Ignored): [dim]{variations_dir.resolve()}[/]\n\n"
            f"[dim]Press Ctrl+C to terminate the server.[/]",
            border_style="cyan",
            title="[ ASSET_STUDIO.exe ]",
        )
    )

    server = AssetStudioServer(
        assets_root=assets_dir,
        variations_root=variations_dir,
        host=host,
        port=port,
    )

    if open_browser:
        with contextlib.suppress(Exception):
            webbrowser.open(STUDIO_PAGE)

    try:
        server.serve_forever()
    except KeyboardInterrupt:
        console.print("\n[yellow]Shutting down Asset Studio server...[/]")
        server.shutdown()
        server.server_close()


@app.command(name="snapshot")
def snapshot_command(
    project: Annotated[
        str,
        typer.Option(
            "--project",
            "-p",
            help="Project brand name to snapshot (e.g. rtkelly-labs, parquet-source-generator).",
        ),
    ],
    name: Annotated[
        str,
        typer.Option(
            "--name",
            "-n",
            help="Name of the snapshot step (e.g. initial-geometry, shadow-refinement).",
        ),
    ],
    run_id: Annotated[
        str,
        typer.Option(
            "--run-id",
            "-r",
            help="Variation run identifier grouping this sequence.",
        ),
    ] = "run-1",
    step_idx: Annotated[
        int,
        typer.Option(
            "--step-idx",
            "-s",
            help="Integer index of the step within the variation run.",
        ),
    ] = 1,
    svg_path: Annotated[
        Path | None,
        typer.Option(
            "--svg",
            help="Path to an SVG file to snapshot. Defaults to project master SVG.",
        ),
    ] = None,
    notes: Annotated[
        str,
        typer.Option(
            "--notes",
            help="Descriptive notes or design rationale for this step.",
        ),
    ] = "",
    variations_dir: Annotated[
        Path,
        typer.Option(
            "--variations-dir",
            help="Directory for ephemeral snapshots and variations (git-ignored).",
        ),
    ] = Path("temp/variations"),
) -> None:
    """Capture an ephemeral vector pipeline snapshot into git-ignored temp/variations/."""
    from rig.image_drift.snapshots import SnapshotManager

    # Resolve svg source
    svg_content: str | None = None
    if svg_path and svg_path.exists():
        svg_content = svg_path.read_text(encoding="utf-8")
    else:
        # Check assets/<project>/<project>.svg
        default_svg = Path("assets") / project / f"{project}.svg"
        if default_svg.exists():
            svg_content = default_svg.read_text(encoding="utf-8")
        else:
            # Check diamond / other names
            candidates = list((Path("assets") / project).glob("*.svg"))
            if candidates:
                svg_content = candidates[0].read_text(encoding="utf-8")

    if svg_content is None:
        console.print(f"[bold red]Error:[/] Could not find an SVG to snapshot for project '{project}'.")
        raise typer.Exit(code=1)

    manager = SnapshotManager(variations_root=variations_dir.resolve())
    step = manager.capture_step(
        project=project,
        run_id=run_id,
        step_idx=step_idx,
        step_name=name,
        svg_content=svg_content,
        notes=notes,
    )

    console.print(
        Panel(
            f"[bold green]Snapshot Captured Successfully![/]\n\n"
            f"Project: [cyan]{step.project}[/]\n"
            f"Run ID: [cyan]{step.run_id}[/]\n"
            f"Step {step.step_idx}: [cyan]{step.step_name}[/]\n"
            f"Timestamp: [dim]{step.timestamp}[/]\n"
            f"SVG File: [dim]{step.svg_path}[/]\n"
            f"Metadata: [dim]{step.metadata_path}[/]",
            title="[ SNAPSHOT_MANAGER ]",
            border_style="green",
        )
    )


@app.command(name="simplify")
def simplify_command(
    svg_path: Annotated[
        Path,
        typer.Argument(
            help="Path to canonical SVG file to simplify and optimize.",
            exists=True,
            file_okay=True,
            dir_okay=False,
            readable=True,
        ),
    ],
    output: Annotated[
        Path | None,
        typer.Option(
            "--output",
            "-o",
            help="Destination output path for simplified SVG (defaults to sidecar or in-place).",
        ),
    ] = None,
    tolerance: Annotated[
        float,
        typer.Option(
            "--tolerance",
            "-t",
            help="Geometric error tolerance in pixels for RDP and Bézier knot decimation.",
        ),
    ] = 0.5,
    min_ssim: Annotated[
        float,
        typer.Option(
            "--min-ssim",
            help="Minimum allowed visual SSIM threshold against unsimplified render.",
        ),
    ] = 0.995,
    max_rmse: Annotated[
        float,
        typer.Option(
            "--max-rmse",
            help="Maximum allowed visual RMSE % drift against unsimplified render.",
        ),
    ] = 1.0,
    in_place: Annotated[
        bool,
        typer.Option(
            "--in-place",
            "-i",
            help="Overwrite input SVG directly with optimized output.",
        ),
    ] = False,
    snapshot: Annotated[
        bool,
        typer.Option(
            "--snapshot",
            "-s",
            help="Publish optimization candidate into Asset Studio variations tree.",
        ),
    ] = False,
    project: Annotated[
        str | None,
        typer.Option(
            "--project",
            "-p",
            help="Project name for Asset Studio snapshot (defaults to parent dir name).",
        ),
    ] = None,
    variations_dir: Annotated[
        Path,
        typer.Option(
            "--variations-dir",
            help="Directory for ephemeral snapshots and variations (git-ignored).",
        ),
    ] = Path("temp/variations"),
) -> None:
    """Multi-pass SVG simplification: geometric decimation, Scour scrubbing,
    coordinate quantization, and visual drift gating.
    """
    from rig.image_drift.simplifier.pipeline import (
        DriftGateNotMetError,
        optimize_svg_with_drift_gate,
    )
    from rig.image_drift.snapshots import SnapshotManager

    if in_place:
        target_output = svg_path
    elif output:
        target_output = output
    else:
        target_output = svg_path.with_name(f"{svg_path.stem}.optimized.svg")

    console.print(f"[cyan]⚡ Running multi-pass SVG simplification on:[/] [bold]{svg_path}[/]")
    console.print(f"  Tolerance Budget: [yellow]±{tolerance}px[/] | Target SSIM: [green]≥{min_ssim}[/]")

    try:
        gated = optimize_svg_with_drift_gate(
            svg_path=svg_path,
            output_path=target_output,
            tolerance=tolerance,
            min_ssim=min_ssim,
            max_rmse=max_rmse,
        )
    except DriftGateNotMetError as exc:
        console.print(f"[bold red]✗ Drift gate not met:[/] {exc}")
        raise typer.Exit(code=1) from exc
    out_file, report = gated.path, gated.report

    table = Table(title="[ SVG_SIMPLIFICATION_REPORT ]", border_style="cyan")
    table.add_column("Metric", style="dim")
    table.add_column("Original", justify="right")
    table.add_column("Optimized", justify="right")
    table.add_column("Change", justify="right", style="bold green")

    table.add_row(
        "File Size",
        f"{report.original_size_bytes:,} B",
        f"{report.final_size_bytes:,} B",
        f"-{report.size_reduction_pct:.1f}%",
    )
    table.add_row(
        "Path Node Count",
        f"{report.original_node_count:,}",
        f"{report.final_node_count:,}",
        f"-{report.node_reduction_pct:.1f}%",
    )
    ssim_val = f"{report.ssim:.4f}" if report.ssim is not None else "N/A"
    psnr_val = f"{report.psnr_db:.2f} dB" if report.psnr_db is not None else "N/A"
    rmse_val = f"{report.rmse_pct:.3f}%" if report.rmse_pct is not None else "N/A"
    table.add_row("Visual SSIM", "-", ssim_val, "PASS" if (report.ssim or 0) >= min_ssim else "WARN")
    table.add_row("Visual PSNR", "-", psnr_val, "-")
    table.add_row("Visual RMSE", "-", rmse_val, "PASS" if (report.rmse_pct or 0) <= max_rmse else "WARN")
    table.add_row("Active Tolerance", f"±{tolerance}px", f"±{report.tolerance:.4f}px", "-")

    console.print(table)

    if not report.gate_passed:
        # Previously this printed a WARN row and exited 0, so a caller could not
        # tell a met budget from a missed one.
        console.print(
            f"[bold red]✗ Drift gate not met[/] after backoff: "
            f"SSIM {report.ssim} (need ≥{min_ssim}), RMSE {report.rmse_pct}% (need ≤{max_rmse}%). "
            f"Candidate written to [bold]{out_file}[/] for inspection, but it is not fit to promote."
        )
        raise typer.Exit(code=1)

    console.print(f"[bold green]✓[/] Wrote optimized SVG to: [bold]{out_file}[/]")

    if snapshot:
        proj_name = project or svg_path.parent.name
        manager = SnapshotManager(variations_root=variations_dir.resolve())
        step = manager.capture_step(
            project=proj_name,
            run_id="simplification-run",
            step_idx=1,
            step_name="svg-optimized",
            svg_content=report.output_svg,
            notes=(
                f"Automated multi-pass simplification (tolerance={report.tolerance:.2f}px, "
                f"SSIM={ssim_val}, -{report.size_reduction_pct}% bytes)"
            ),
            metrics={
                "ssim": report.ssim,
                "psnr": report.psnr_db,
                "rmse": report.rmse_pct,
                "cand_size": report.final_size_bytes,
            },
        )
        svg_filename: str = step.svg_path.name if step.svg_path is not None else step.step_name
        msg = f"[cyan]✓ Snapshot published to Asset Studio for project [[bold]{proj_name}[/]]:[/] {svg_filename}"
        console.print(msg)


def main() -> None:
    app()


if __name__ == "__main__":
    main()
