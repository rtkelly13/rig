"""Vercel project names in managed-projects.json and the ``*.vercel.app`` hosts they claim."""

from __future__ import annotations

import json
from pathlib import Path

import pytest

from rig.workspace.engine import config_file_path
from rig.workspace.vercel_names import claim_for_host, load_vercel_claims

pytestmark = pytest.mark.ci


def config(tmp_path: Path, *projects: dict[str, object]) -> Path:
    path = tmp_path / "managed-projects.json"
    path.write_text(json.dumps({"projects": list(projects)}), encoding="utf-8")
    return path


def test_committed_declarations_are_valid() -> None:
    claims = load_vercel_claims(config_file_path())
    storybook = claim_for_host(claims, "design-system-storybook-diffra.vercel.app")
    assert storybook is not None and storybook.project == "design-system"


def test_a_name_declared_twice_is_rejected(tmp_path: Path) -> None:
    cfg = config(
        tmp_path,
        {"name": "a", "repo": "git@github.com:o/a.git", "vercel": ["site"]},
        {"name": "b", "repo": "git@github.com:o/b.git", "vercel": ["site"]},
    )
    with pytest.raises(ValueError, match="'site' is already declared by a"):
        load_vercel_claims(cfg)


@pytest.mark.parametrize("name", ["Site", "-site", "site_x", ""])
def test_names_must_be_vercel_names(tmp_path: Path, name: str) -> None:
    with pytest.raises(ValueError, match="not a Vercel project name"):
        load_vercel_claims(config(tmp_path, {"name": "a", "repo": "git@github.com:o/a.git", "vercel": [name]}))
