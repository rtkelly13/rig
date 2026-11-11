"""Asset Studio asset indexer, catalog generator, and naming regularizer."""

from __future__ import annotations

import csv
import re
from dataclasses import dataclass, field
from pathlib import Path

from pydantic import JsonValue, StrictInt, StrictStr

from image_drift.typed_json import Lenient, decode, decode_json

# Regex pattern matching standardized snapshot file naming:
# {project}__{run_id}__{step_idx:02d}_{step_name}__{timestamp}.{ext}
SNAPSHOT_NAME_PATTERN = re.compile(
    r"^(?P<project>[a-zA-Z0-9_\-]+)__(?P<run_id>[a-zA-Z0-9_\-]+)__(?P<step_idx>\d+)_(?P<step_name>[a-zA-Z0-9_\-]+)__(?P<timestamp>[a-zA-Z0-9_]+)\.(?P<ext>svg|png|json)$"
)


@dataclass(frozen=True)
class SnapshotParsedToken:
    """Parsed components of a standardized snapshot filename."""

    project: str
    run_id: str
    step_idx: int
    step_name: str
    timestamp: str
    extension: str


def parse_snapshot_filename(filename: str) -> SnapshotParsedToken | None:
    """Validate and extract metadata tokens from a snapshot filename."""
    m = SNAPSHOT_NAME_PATTERN.match(filename)
    if not m:
        return None
    return SnapshotParsedToken(
        project=m.group("project"),
        run_id=m.group("run_id"),
        step_idx=int(m.group("step_idx")),
        step_name=m.group("step_name"),
        timestamp=m.group("timestamp"),
        extension=m.group("ext"),
    )


@dataclass(frozen=True)
class ReferenceAsset:
    """A historical or approved ground-truth reference image."""

    sequence_number: int
    path: Path
    filename: str
    description: str = ""
    is_primary: bool = False
    role: str = "historical"


@dataclass
class ProductionCut:
    """An exported production asset cut (SVG master, raster master, avatar, favicon)."""

    cut_type: str
    path: Path
    filename: str
    size_bytes: int = 0
    width: int | None = None
    height: int | None = None
    format: str = ""
    purpose: str = ""
    description: str = ""


@dataclass
class SnapshotStep:
    """An individual step or variation within an experimental pipeline run."""

    project: str
    run_id: str
    step_idx: int
    step_name: str
    timestamp: str
    svg_path: Path | None = None
    png_path: Path | None = None
    metadata_path: Path | None = None
    metrics: dict[str, JsonValue] = field(default_factory=dict[str, JsonValue])
    notes: str = ""


@dataclass(frozen=True, slots=True)
class _StepKey:
    step_idx: int
    step_name: str


@dataclass(frozen=True, slots=True)
class _ManifestReference:
    filename: StrictStr = ""
    sequence_number: StrictInt = 99
    role: StrictStr = "historical"
    description: StrictStr = ""


@dataclass(frozen=True, slots=True)
class _Manifest:
    """`references/manifest.json`: the primary reference's filename and every reference's metadata."""

    primary: StrictStr = ""
    references: tuple[_ManifestReference, ...] = ()


@dataclass(frozen=True, slots=True)
class _StepMetadata:
    """A snapshot's `.json` sidecar; older sidecars hold the metrics at the top level."""

    metrics: Lenient[dict[str, JsonValue]] = None
    notes: Lenient[str] = None


@dataclass(slots=True, frozen=True)
class _CutRow:
    """One row of a project's `production-cuts.csv`; absent or blank cells read as None."""

    filename: Lenient[str] = None
    cut_type: Lenient[str] = None
    width: Lenient[str] = None
    height: Lenient[str] = None
    format: Lenient[str] = None
    purpose: Lenient[str] = None
    description: Lenient[str] = None


@dataclass
class VariationRun:
    """A named sequence of pipeline steps stored in the git-ignored variations directory."""

    run_id: str
    project: str
    steps: list[SnapshotStep] = field(default_factory=list[SnapshotStep])
    created_at: str = ""


@dataclass
class ProjectAssetCatalog:
    """Comprehensive catalog of production cuts, references, and variations for a project."""

    name: str
    asset_dir: Path
    reference_dir: Path
    master_svg: Path | None = None
    master_png: Path | None = None
    primary_reference: ReferenceAsset | None = None
    references: list[ReferenceAsset] = field(default_factory=list[ReferenceAsset])
    production_cuts: list[ProductionCut] = field(default_factory=list[ProductionCut])
    variation_runs: list[VariationRun] = field(default_factory=list[VariationRun])


class AssetIndexer:
    """Indexes assets directories and ephemeral variations into structured project catalogs."""

    def __init__(self, assets_root: Path, variations_root: Path) -> None:
        self.assets_root: Path = assets_root
        self.variations_root: Path = variations_root

    def list_projects(self) -> list[str]:
        """List all managed project subdirectories under assets root."""
        if not self.assets_root.exists():
            return []
        projects: list[str] = []
        for item in sorted(self.assets_root.iterdir()):
            if item.is_dir() and not item.name.startswith((".", "_")) and item.name not in ("testing", "temp"):
                projects.append(item.name)
        return projects

    def index_project(self, project_name: str) -> ProjectAssetCatalog:
        """Scan and index all assets, references, and variations for a specific project."""
        proj_dir = self.assets_root / project_name
        ref_dir = proj_dir / "reference"

        master_svg: Path | None = None
        master_png: Path | None = None
        production_cuts: list[ProductionCut] = []
        references: list[ReferenceAsset] = []
        variation_runs: list[VariationRun] = []
        primary_ref: ReferenceAsset | None = None

        if proj_dir.exists():
            # Check for canonical production cuts CSV specification
            cuts_dir = proj_dir / "production-cuts" if (proj_dir / "production-cuts").is_dir() else proj_dir
            cuts_csv = cuts_dir / "production-cuts.csv"
            if not cuts_csv.exists():
                cuts_csv = cuts_dir / "cuts.csv"

            if cuts_csv.exists():
                # Derive production cuts strictly from CSV specification
                with cuts_csv.open("r", encoding="utf-8") as f:
                    reader = csv.DictReader(f)
                    for raw_row in reader:
                        row = decode(_CutRow, raw_row)
                        fname = (row.filename or "").strip()
                        if not fname:
                            continue
                        cut_file = cuts_dir / fname
                        size = cut_file.stat().st_size if cut_file.exists() else 0
                        ctype = ("raster" if row.cut_type is None else row.cut_type).strip()
                        w_str = (row.width or "").strip()
                        h_str = (row.height or "").strip()
                        width_val = int(w_str) if w_str.isdigit() else None
                        height_val = int(h_str) if h_str.isdigit() else None
                        fmt_val = (cut_file.suffix.lstrip(".") if row.format is None else row.format).strip()
                        purpose_val = (row.purpose or "").strip()
                        desc_val = (row.description or "").strip()

                        if ctype in ("master_svg", "svg") and (master_svg is None or "master" in ctype):
                            master_svg = cut_file
                        if ctype in ("master_png", "cut_1000") and master_png is None:
                            master_png = cut_file

                        production_cuts.append(
                            ProductionCut(
                                cut_type=ctype,
                                path=cut_file,
                                filename=fname,
                                size_bytes=size,
                                width=width_val,
                                height=height_val,
                                format=fmt_val,
                                purpose=purpose_val,
                                description=desc_val,
                            )
                        )

                if master_svg is None or not master_svg.exists():
                    for cand in [
                        proj_dir / f"{project_name}.svg",
                        proj_dir / f"{project_name}-diamond.svg",
                        cuts_dir / f"{project_name}.svg",
                        cuts_dir / f"{project_name}-diamond.svg",
                    ]:
                        if cand.exists():
                            master_svg = cand
                            break

                if master_png is None or not master_png.exists():
                    for cand in [
                        cuts_dir / f"{project_name}-logo-1000x1000.png",
                        cuts_dir / f"{project_name}-1000x1000.png",
                        proj_dir / f"{project_name}-logo-1000x1000.png",
                        proj_dir / f"{project_name}-1000x1000.png",
                    ]:
                        if cand.exists():
                            master_png = cand
                            break

                cut_order = {
                    "cut_2000": 1,
                    "master_png": 2,
                    "cut_1000": 2,
                    "master_svg": 3,
                    "svg": 3,
                    "avatar": 4,
                    "apple_touch": 5,
                    "favicon_svg": 6,
                    "favicon": 7,
                    "reference_raster": 8,
                    "raster": 9,
                }
                production_cuts.sort(key=lambda c: (cut_order.get(c.cut_type, 99), c.filename))
            else:
                # Fallback to directory scanning if no CSV specification exists
                for file in sorted(proj_dir.iterdir()):
                    if not file.is_file() or file.name.startswith("."):
                        continue

                    size = file.stat().st_size
                    if file.suffix.lower() == ".svg":
                        if file.name == f"{project_name}.svg" or file.name.endswith("-diamond.svg"):
                            master_svg = file
                        production_cuts.append(
                            ProductionCut(cut_type="svg", path=file, filename=file.name, size_bytes=size)
                        )
                    elif file.suffix.lower() == ".png":
                        if "2000x2000" in file.name or "2000px" in file.name:
                            production_cuts.append(
                                ProductionCut(cut_type="cut_2000", path=file, filename=file.name, size_bytes=size)
                            )
                        elif "1000x1000" in file.name or "1000px" in file.name or "logo" in file.name.lower():
                            if master_png is None:
                                master_png = file
                            production_cuts.append(
                                ProductionCut(cut_type="master_png", path=file, filename=file.name, size_bytes=size)
                            )
                        elif "banner" in file.name.lower():
                            production_cuts.append(
                                ProductionCut(cut_type="banner", path=file, filename=file.name, size_bytes=size)
                            )
                        elif "avatar" in file.name:
                            production_cuts.append(
                                ProductionCut(cut_type="avatar", path=file, filename=file.name, size_bytes=size)
                            )
                        elif "favicon" in file.name or "apple-touch" in file.name:
                            production_cuts.append(
                                ProductionCut(cut_type="favicon", path=file, filename=file.name, size_bytes=size)
                            )
                        elif file.name == f"{project_name}.png":
                            # Legacy root reference fallback
                            production_cuts.append(
                                ProductionCut(
                                    cut_type="reference_raster",
                                    path=file,
                                    filename=file.name,
                                    size_bytes=size,
                                )
                            )
                        else:
                            production_cuts.append(
                                ProductionCut(cut_type="raster", path=file, filename=file.name, size_bytes=size)
                            )
                    elif file.suffix.lower() in (".jpg", ".jpeg", ".webp"):
                        if "logo" in file.name.lower() or "icon" in file.name.lower():
                            if master_png is None:
                                master_png = file
                            production_cuts.append(
                                ProductionCut(cut_type="logo", path=file, filename=file.name, size_bytes=size)
                            )
                        elif "banner" in file.name.lower() or "og" in file.name.lower():
                            production_cuts.append(
                                ProductionCut(cut_type="banner", path=file, filename=file.name, size_bytes=size)
                            )
                        else:
                            production_cuts.append(
                                ProductionCut(cut_type="raster", path=file, filename=file.name, size_bytes=size)
                            )
                    elif file.suffix.lower() == ".ico":
                        production_cuts.append(
                            ProductionCut(cut_type="favicon", path=file, filename=file.name, size_bytes=size)
                        )

                # Prioritize cuts: 2000px -> 1000px -> logo -> SVG -> banner -> avatar -> raster -> favicon
                cut_order = {
                    "cut_2000": 1,
                    "master_png": 2,
                    "logo": 2,
                    "svg": 3,
                    "banner": 4,
                    "avatar": 5,
                    "reference_raster": 6,
                    "raster": 7,
                    "favicon": 8,
                }
                production_cuts.sort(key=lambda c: (cut_order.get(c.cut_type, 99), c.filename))

            # Scan reference directory
            if ref_dir.exists():
                manifest_file = ref_dir / "manifest.json"
                if manifest_file.exists():
                    try:
                        manifest = decode_json(_Manifest, manifest_file.read_text(encoding="utf-8"))
                        primary_name = manifest.primary.strip()
                        for item in manifest.references:
                            fname = item.filename.strip()
                            if not fname:
                                continue
                            ref_path = ref_dir / fname
                            seq_num = item.sequence_number
                            role = item.role
                            desc = item.description
                            is_prim = (fname == primary_name) or (role == "primary")
                            references.append(
                                ReferenceAsset(
                                    sequence_number=seq_num,
                                    path=ref_path,
                                    filename=fname,
                                    description=desc,
                                    is_primary=is_prim,
                                    role="primary" if is_prim else role,
                                )
                            )
                    except (OSError, ValueError, TypeError, AttributeError):
                        pass

                manifest_filenames = {r.filename for r in references}
                seq_counter = 1
                for ref_file in sorted(ref_dir.iterdir()):
                    if (
                        not ref_file.is_file()
                        or ref_file.name.startswith(".")
                        or ref_file.suffix.lower() in (".md", ".json")
                    ):
                        continue
                    if ref_file.name in manifest_filenames:
                        continue

                    # Extract sequence number from prefix (e.g. 01-..., 02-...)
                    match = re.match(r"^(\d+)-", ref_file.name)
                    if match:
                        seq_num = int(match.group(1))
                    else:
                        seq_num = 90 + seq_counter
                        seq_counter += 1

                    references.append(
                        ReferenceAsset(
                            sequence_number=seq_num,
                            path=ref_file,
                            filename=ref_file.name,
                            description=ref_file.stem,
                            is_primary=False,
                            role="historical",
                        )
                    )
                references.sort(key=lambda r: r.sequence_number)
                primary_ref = next((r for r in references if r.is_primary), None)
                if primary_ref is None and references:
                    primary_ref = next((r for r in references if r.sequence_number == 1), references[0])

        # Scan variation runs under variations_root / project_name
        proj_var_dir = self.variations_root / project_name
        if proj_var_dir.exists():
            for run_dir in sorted(proj_var_dir.iterdir()):
                if not run_dir.is_dir() or run_dir.name.startswith("."):
                    continue

                steps_dict: dict[_StepKey, SnapshotStep] = {}
                for f in sorted(run_dir.iterdir()):
                    parsed = parse_snapshot_filename(f.name)
                    if not parsed:
                        continue

                    key = _StepKey(parsed.step_idx, parsed.step_name)
                    if key not in steps_dict:
                        steps_dict[key] = SnapshotStep(
                            project=parsed.project,
                            run_id=parsed.run_id,
                            step_idx=parsed.step_idx,
                            step_name=parsed.step_name,
                            timestamp=parsed.timestamp,
                        )

                    step_obj = steps_dict[key]
                    if parsed.extension == "svg":
                        step_obj.svg_path = f
                    elif parsed.extension == "png":
                        step_obj.png_path = f
                    elif parsed.extension == "json":
                        step_obj.metadata_path = f
                        try:
                            text = f.read_text(encoding="utf-8")
                            meta = decode_json(_StepMetadata, text)
                            step_obj.metrics = (
                                meta.metrics if meta.metrics is not None else decode_json(dict[str, JsonValue], text)
                            )
                            step_obj.notes = meta.notes or ""
                        except (OSError, ValueError):
                            pass

                sorted_steps = sorted(steps_dict.values(), key=lambda s: s.step_idx)
                variation_runs.append(
                    VariationRun(
                        run_id=run_dir.name,
                        project=project_name,
                        steps=sorted_steps,
                    )
                )

        return ProjectAssetCatalog(
            name=project_name,
            asset_dir=proj_dir,
            reference_dir=ref_dir,
            master_svg=master_svg,
            master_png=master_png,
            primary_reference=primary_ref,
            references=references,
            production_cuts=production_cuts,
            variation_runs=variation_runs,
        )

    def index_all(self) -> dict[str, ProjectAssetCatalog]:
        """Index all managed projects."""
        return {p: self.index_project(p) for p in self.list_projects()}
