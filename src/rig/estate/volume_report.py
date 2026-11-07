"""`estate volume`: scan a drive with WizTree and report where its space went.

The command in `cli.py` parses options; this module runs the scan and renders
the three views from `volume` (drill-down, dev-artefact partition, known hogs)
as tables or JSON.
"""

from __future__ import annotations

import json
import pathlib
import shutil
import tempfile
from dataclasses import dataclass

import typer
from rich.console import Console
from rich.table import Table

from rig.estate import volume as volume_mod
from rig.estate.reclaim import human

_SAFETY_STYLE = {"safe": "green", "review": "yellow", "never": "red"}


@dataclass(slots=True, frozen=True)
class VolumeRequest:
    """What `estate volume` was asked to scan and show."""

    drive: str
    #: An existing WizTree folder export to reuse instead of scanning.
    csv: pathlib.Path | None
    keep_csv: bool
    code_root: pathlib.Path
    min_bytes: int
    #: The `--min` text as typed, for the table title.
    min_label: str
    depth: int


def _load(request: VolumeRequest, err: Console) -> list[volume_mod.Folder]:
    if request.csv is not None:
        return volume_mod.load(request.csv, floor=1024 * 1024)
    # resolve() expands 8.3 short names, which the elevated WizTree child may not.
    export = pathlib.Path(tempfile.gettempdir()).resolve() / f"wiztree-{request.drive.strip(':').lower()}.csv"
    err.print(f"[dim]scanning {request.drive}: with WizTree (approve the UAC prompt) -> {export}[/dim]")
    volume_mod.run_wiztree(request.drive, export)
    try:
        return volume_mod.load(export, floor=1024 * 1024)
    finally:
        if not request.keep_csv:
            export.unlink(missing_ok=True)


def run(request: VolumeRequest, *, json_output: bool, console: Console, err: Console) -> None:
    """Scan (or reuse an export) and print the drill-down, partition and hogs."""
    folders = _load(request, err)
    drill = volume_mod.tree(folders, min_bytes=request.min_bytes, max_depth=request.depth)
    parts = volume_mod.partition(folders, request.code_root)
    found = volume_mod.hogs(folders)
    at_root = volume_mod.root_files(folders)
    usage = shutil.disk_usage(f"{request.drive.strip(':')}:\\")

    if json_output:
        typer.echo(
            json.dumps(
                {
                    "drive": request.drive,
                    "total": usage.total,
                    "free": usage.free,
                    "tree": [
                        {
                            "level": row.level,
                            "path": row.folder.path,
                            "allocated": row.folder.allocated,
                            "size": row.folder.size,
                        }
                        for row in drill
                    ],
                    "code_root": str(request.code_root),
                    "partition": parts,
                    "hogs": [
                        {
                            "label": hit.hog.label,
                            "allocated": hit.allocated,
                            "paths": list(hit.paths),
                            "remedy": hit.hog.remedy,
                            "safety": hit.hog.safety,
                        }
                        for hit in found
                    ],
                    "root_files": at_root,
                },
                indent=2,
            )
        )
        return

    console.print(
        f"[bold]{request.drive.strip(':')}:[/bold] {human(usage.used)} used of {human(usage.total)} · "
        f"[bold]{human(usage.free)} free[/bold] ({usage.free / usage.total:.1%})\n"
    )
    table = Table(title=f"Largest folders (>= {request.min_label})", title_style="bold")
    table.add_column("Folder")
    table.add_column("Allocated", justify="right")
    table.add_column("Logical", justify="right", style="dim")
    for row in drill:
        f = row.folder
        label = f.path if row.level == 0 else "  " * row.level + f.path.rpartition("\\")[2]
        table.add_row(label, human(f.allocated), human(f.size))
    console.print(table)

    table = Table(title=f"Dev artefacts under {request.code_root} (outermost match, no overlap)", title_style="bold")
    table.add_column("Category")
    table.add_column("Allocated", justify="right")
    for name, size in parts.items():
        table.add_row(name, human(size))
    console.print(table)

    table = Table(title="Known Windows hogs", title_style="bold")
    table.add_column("Hog")
    table.add_column("Allocated", justify="right")
    table.add_column("Remedy", style="dim")
    table.add_column("Safety")
    for hit in found:
        if hit.allocated < 100 * 1024 * 1024:
            continue
        style = _SAFETY_STYLE[hit.hog.safety]
        table.add_row(hit.hog.label, human(hit.allocated), hit.hog.remedy, f"[{style}]{hit.hog.safety}[/{style}]")
    if at_root:
        table.add_row(
            "Files at drive root",
            human(at_root),
            "powercfg /h off frees hiberfil.sys",
            "[yellow]review[/yellow]",
        )
    console.print(table)
