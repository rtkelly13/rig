"""Typed request and response models for Asset Studio's API."""

from __future__ import annotations

from dataclasses import dataclass, field
from typing import ClassVar

from pydantic import BaseModel, ConfigDict, StrictInt, StrictStr

from image_drift.typed_json import JsonValue


@dataclass(frozen=True, slots=True)
class DiffQuery:
    ref: tuple[StrictStr, ...] = ()
    cand: tuple[StrictStr, ...] = ()


@dataclass(frozen=True, slots=True)
class SnapshotRequest:
    project: StrictStr | None = None
    run_id: StrictStr | None = None
    step_idx: StrictInt = 1
    step_name: StrictStr = "checkpoint"
    svg_content: StrictStr | None = None
    metrics: dict[str, JsonValue] = field(default_factory=dict[str, JsonValue])
    notes: StrictStr = ""


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
