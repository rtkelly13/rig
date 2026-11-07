"""Typed API response and request models for Asset Studio / Rig Control Plane server."""

from __future__ import annotations

from dataclasses import dataclass, field
from pathlib import Path
from typing import ClassVar

from pydantic import BaseModel, ConfigDict, StrictInt, StrictStr

from rig.typed_json import JsonValue


@dataclass(frozen=True, slots=True)
class DiffQuery:
    ref: tuple[StrictStr, ...] = ()
    cand: tuple[StrictStr, ...] = ()


@dataclass(frozen=True, slots=True)
class _LogoCandidate:
    rank: int
    url: str


@dataclass(frozen=True, slots=True)
class ProjectBrandAssets:
    logo_url: str | None
    banner_url: str | None


@dataclass(frozen=True, slots=True)
class OrcaRepoBadge:
    color: str | None = None
    icon: str | None = None


@dataclass(frozen=True, slots=True)
class OrcaBadgeConfig:
    repos: dict[str, OrcaRepoBadge] = field(default_factory=dict)


def find_project_dir(assets_root: Path, project_name: str) -> Path:
    direct = assets_root / project_name
    if direct.is_dir():
        return direct
    clean_target = project_name.lower().replace(".", "").replace("-", "").replace("_", "")
    if assets_root.exists():
        for d in assets_root.iterdir():
            if d.is_dir() and d.name.lower().replace(".", "").replace("-", "").replace("_", "") == clean_target:
                return d
    return direct


def find_project_brand_assets(assets_root: Path, project_name: str) -> ProjectBrandAssets:
    proj_dir = find_project_dir(assets_root, project_name)
    if not proj_dir.is_dir():
        return ProjectBrandAssets(logo_url=None, banner_url=None)
    logo_url: str | None = None
    banner_url: str | None = None
    dirs_to_check = [proj_dir]
    cuts_sub = proj_dir / "production-cuts"
    if cuts_sub.is_dir():
        dirs_to_check.append(cuts_sub)

    candidates: list[_LogoCandidate] = []
    for d in dirs_to_check:
        for f in sorted(d.iterdir()):
            if not f.is_file() or f.name.startswith("."):
                continue
            name_lower = f.name.lower()
            rel_url = f"/media/assets/{f.relative_to(assets_root).as_posix()}"
            is_logo = "logo" in name_lower
            is_banner = "banner" in name_lower or any(p in name_lower for p in ("og-", "og_", "-og.", "_og."))
            if banner_url is None and not is_logo and is_banner:
                banner_url = rel_url

            if is_logo:
                candidates.append(_LogoCandidate(0, rel_url))
            elif "1000x1000" in name_lower or "1000px" in name_lower:
                candidates.append(_LogoCandidate(1, rel_url))
            elif name_lower.endswith(".svg") and "favicon" not in name_lower:
                candidates.append(_LogoCandidate(2, rel_url))
            elif "icon" in name_lower or "avatar" in name_lower:
                candidates.append(_LogoCandidate(3, rel_url))
            elif name_lower.endswith(".svg"):
                candidates.append(_LogoCandidate(4, rel_url))

    if candidates:
        candidates.sort(key=lambda c: c.rank)
        logo_url = candidates[0].url

    return ProjectBrandAssets(logo_url=logo_url, banner_url=banner_url)


@dataclass(frozen=True, slots=True)
class ProjectQuery:
    project: tuple[StrictStr, ...] = ()


@dataclass(frozen=True, slots=True)
class Export3DRequest:
    project: StrictStr
    cut_type: StrictStr
    image_base64: StrictStr


@dataclass(frozen=True, slots=True)
class SavePresetRequest:
    name: StrictStr
    project: StrictStr
    ior: float
    dispersion: float
    roughness: float
    thickness: float
    distortion: float
    caustic_color: StrictStr


@dataclass(frozen=True, slots=True)
class SnapshotRequest:
    project: StrictStr | None = None
    run_id: StrictStr | None = None
    step_idx: StrictInt = 1
    step_name: StrictStr = "checkpoint"
    svg_content: StrictStr | None = None
    metrics: dict[str, JsonValue] = field(default_factory=dict[str, JsonValue])
    notes: StrictStr = ""


@dataclass(frozen=True, slots=True)
class FeedbackRequest:
    project: StrictStr = "unknown-project"
    reference: StrictStr = ""
    candidate: StrictStr = ""
    region: StrictStr = "Overall"
    issue: StrictStr = "General"
    verdict: StrictStr = "NEEDS_ITERATION"
    notes: StrictStr = ""
    prompt: StrictStr = ""


@dataclass(frozen=True, slots=True)
class ChangeTimestamps:
    overall: float
    css: float


class MtimeResponse(BaseModel):
    model_config: ClassVar[ConfigDict] = ConfigDict(frozen=True)

    mtime: float
    css_mtime: float
    is_preview: bool


class ResponseModel(BaseModel):
    model_config: ClassVar[ConfigDict] = ConfigDict(frozen=True)


class ReferenceResponse(ResponseModel):
    sequence_number: int
    filename: str
    description: str
    url: str


class ProductionCutResponse(ResponseModel):
    cut_type: str
    filename: str
    size_bytes: int
    width: int | None
    height: int | None
    format: str
    purpose: str
    description: str
    url: str


class SnapshotStepResponse(ResponseModel):
    step_idx: int
    step_name: str
    timestamp: str
    svg_url: str | None
    png_url: str | None
    metrics: dict[str, JsonValue]
    notes: str


class VariationRunResponse(ResponseModel):
    run_id: str
    project: str
    steps: list[SnapshotStepResponse]


class CatalogResponse(ResponseModel):
    """One project's assets as `/api/projects` serves them, with media URLs."""

    name: str
    master_svg: str | None
    master_png: str | None
    references: list[ReferenceResponse]
    production_cuts: list[ProductionCutResponse]
    variation_runs: list[VariationRunResponse]


class ProjectsResponse(ResponseModel):
    """`/api/projects`: every indexed project's catalog."""

    projects: list[CatalogResponse]


class EstateProjectResponse(ResponseModel):
    name: str
    repo: str
    description: str
    domain: str
    stack: str
    enabled: bool
    logo_url: str | None = None
    banner_url: str | None = None
    orca_group: str | None = None
    color: str | None = None
    icon: str | None = None


class EstateResponse(ResponseModel):
    projects: list[EstateProjectResponse]


class StatusResponse(ResponseModel):
    environment: str
    port: int
    is_preview: bool
    db_path: str


class BackupResponse(ResponseModel):
    status: str
    backup_file: str


class OpticalPresetResponseItem(ResponseModel):
    id: int
    name: str
    project: str
    ior: float
    dispersion: float
    roughness: float
    thickness: float
    distortion: float
    caustic_color: str
    created_at: str


class PresetsListResponse(ResponseModel):
    presets: list[OpticalPresetResponseItem]


class HotspotResponse(ResponseModel):
    rank: int
    x0: int
    y0: int
    x1: int
    y1: int
    error_pct: float
    dominant_mode: str
    summary: str


class DecomposedResponse(ResponseModel):
    luminance_rmse_pct: float
    chrominance_rmse_pct: float
    color_to_geometry_ratio: float
    edge_correlation: float
    boundary_dilation_bias: float
    hotspots: list[HotspotResponse]
    recommendations: list[str]


class DriftResponse(ResponseModel):
    """`/api/drift`'s comparison of a candidate against its reference."""

    rmse_pct: float
    ssim: float
    psnr_db: float
    diff_heatmap_data: str
    ref_size_bytes: int
    cand_size_bytes: int
    decomposed: DecomposedResponse | None
