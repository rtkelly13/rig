"""Tests for brand asset normalization, colored item grouping, and estate project configuration."""

from __future__ import annotations

import json
from pathlib import Path

import pytest

from rig.image_drift.server import StudioRequestHandler
from rig.workspace.engine import load_managed_specs

pytestmark = pytest.mark.ci

REPO_ROOT = Path(__file__).resolve().parents[1]


class _TestHandler(StudioRequestHandler):
    """Test double exposing brand asset lookups without starting a network server."""

    def __init__(self, assets_root: Path) -> None:
        self.assets_root = assets_root


def test_brand_asset_normalization_pascal_to_kebab(tmp_path: Path) -> None:
    """Verifies that PascalCase project names resolve to kebab-case directory assets."""
    assets_dir = tmp_path / "assets"
    assets_dir.mkdir(parents=True)

    # Setup mock cuts under parquet-source-generator
    proj_dir = assets_dir / "parquet-source-generator" / "production-cuts"
    proj_dir.mkdir(parents=True)
    (proj_dir / "parquet-diamond-1000x1000.png").write_bytes(b"dummy-png")

    handler = _TestHandler(assets_dir)
    res = handler._find_project_brand_assets("Parquet.SourceGenerator")

    assert res.logo_url is not None
    assert "parquet-diamond-1000x1000.png" in res.logo_url


def test_logo_does_not_falsely_match_as_banner(tmp_path: Path) -> None:
    """Verifies that logo files containing 'og' in 'logo' are not misclassified as banners."""
    assets_dir = tmp_path / "assets"
    proj_dir = assets_dir / "fluent-assertions"
    proj_dir.mkdir(parents=True)
    (proj_dir / "fluentassertions-resultful-logo.jpg").write_bytes(b"dummy-jpg")

    handler = _TestHandler(assets_dir)
    res = handler._find_project_brand_assets("fluent-assertions")
    assert res.logo_url is not None
    assert "fluentassertions-resultful-logo.jpg" in res.logo_url
    assert res.banner_url is None


def test_managed_specs_decode_orca_group() -> None:
    """Verifies that load_managed_specs extracts orca.group metadata correctly."""
    specs = load_managed_specs()
    by_name = {s.name: s for s in specs}

    assert "blog" in by_name
    assert by_name["blog"].orca_group == "personal/knowledge"

    assert "design-system" in by_name
    assert by_name["design-system"].orca_group == "rtkelly-labs"

    assert "Resultful" in by_name
    assert by_name["Resultful"].orca_group == "Resultful"

    assert "rig" in by_name
    assert by_name["rig"].orca_group == ""


def test_repo_policies_covers_active_managed_projects() -> None:
    """Verifies active projects in managed-projects.json are governed by repo_policies.json."""
    policies_path = REPO_ROOT / "config" / "repo_policies.json"
    data = json.loads(policies_path.read_text(encoding="utf-8"))
    profiles = data.get("profiles", {})

    governed_repos = {repo for prof in profiles.values() for repo in prof.get("repos", [])}

    # Ensure key active projects are governed
    key_projects = (
        "blog",
        "Resultful",
        "FluentAssertions.Resultful",
        "ProceduralGeneration3DMazes",
        "ynab-budget-companion",
        "hearth",
    )
    for name in key_projects:
        assert name in governed_repos, f"Active project '{name}' not governed in repo_policies.json"

    # Ensure stale nonexistent projects are removed
    assert "the-vault-system-rewrite" not in governed_repos
