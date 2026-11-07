"""Local port ranges in managed-projects.json: the governance rules and the committed claims."""

from __future__ import annotations

import json
from pathlib import Path

import pytest

from rig.workspace.engine import config_file_path
from rig.workspace.ports import PortRange, claim_for, load_port_claims

pytestmark = pytest.mark.ci


def config(tmp_path: Path, *projects: dict[str, object]) -> Path:
    path = tmp_path / "managed-projects.json"
    path.write_text(json.dumps({"projects": list(projects)}), encoding="utf-8")
    return path


def project(name: str, start: int, end: int, *, enabled: bool = True) -> dict[str, object]:
    return {
        "name": name,
        "repo": f"git@github.com:rtkelly13/{name}.git",
        "enabled": enabled,
        "ports": {"start": start, "end": end},
    }


def test_committed_claims_are_valid_and_rig_owns_its_triage_port() -> None:
    claims = load_port_claims(config_file_path())
    rig = claim_for(claims, 8094)
    assert rig is not None and rig.slug == "rtkelly13/rig"


def test_claims_map_ports_to_repos(tmp_path: Path) -> None:
    claims = load_port_claims(config(tmp_path, project("rig", 8090, 8099), project("blog", 8100, 8109)))
    assert [(claim.project, claim.slug, claim.ports) for claim in claims] == [
        ("rig", "rtkelly13/rig", PortRange(8090, 8099)),
        ("blog", "rtkelly13/blog", PortRange(8100, 8109)),
    ]
    found = claim_for(claims, 8105)
    assert found is not None and found.project == "blog"
    assert claim_for(claims, 8110) is None


def test_overlapping_ranges_are_rejected_naming_both(tmp_path: Path) -> None:
    with pytest.raises(ValueError, match="blog: ports 8095-8105 overlap rig's 8090-8099"):
        load_port_claims(config(tmp_path, project("rig", 8090, 8099), project("blog", 8095, 8105)))


@pytest.mark.parametrize(("start", "end"), [(80, 90), (8099, 8090), (65530, 65540)])
def test_out_of_bounds_or_reversed_ranges_are_rejected(tmp_path: Path, start: int, end: int) -> None:
    with pytest.raises(ValueError, match="must lie within"):
        load_port_claims(config(tmp_path, project("rig", start, end)))


def test_disabled_projects_and_projects_without_ports_claim_nothing(tmp_path: Path) -> None:
    plain = {"name": "docs", "repo": "git@github.com:rtkelly13/docs.git"}
    claims = load_port_claims(config(tmp_path, plain, project("old", 8090, 8099, enabled=False)))
    assert claims == []


def test_ports_must_belong_to_a_github_repo(tmp_path: Path) -> None:
    local = {"name": "x", "repo": "/srv/x.git", "ports": {"start": 9000, "end": 9009}}
    with pytest.raises(ValueError, match="need a GitHub repo"):
        load_port_claims(config(tmp_path, local))
