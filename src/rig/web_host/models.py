"""Typed API response models for the rig web host."""

from __future__ import annotations

from dataclasses import dataclass, field
from pathlib import Path
from typing import ClassVar

from pydantic import BaseModel, ConfigDict


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
class ChangeTimestamps:
    overall: float
    css: float


class ResponseModel(BaseModel):
    model_config: ClassVar[ConfigDict] = ConfigDict(frozen=True)


class MtimeResponse(ResponseModel):
    mtime: float
    css_mtime: float
    is_preview: bool


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
