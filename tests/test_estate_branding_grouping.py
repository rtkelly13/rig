"""Tests for estate project configuration. Brand asset lookup is tested in tests/Rig.Web.Tests."""

from __future__ import annotations

import json
from pathlib import Path

import pytest

from rig.workspace.engine import load_managed_specs

pytestmark = pytest.mark.ci

REPO_ROOT = Path(__file__).resolve().parents[1]


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
