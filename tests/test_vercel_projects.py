"""Vercel project names in managed-projects.json, and the ownership audit against Vercel's facts."""

from __future__ import annotations

import json
from pathlib import Path

import pytest

from rig.repo_governance.vercel_projects import Ownership, commit_source, plan_findings
from rig.workspace.engine import config_file_path
from rig.workspace.vercel_names import VercelClaim, claim_for_host, load_repo_slugs, load_vercel_claims

pytestmark = pytest.mark.ci

REPOS = {"design-system": "rtkelly-labs/design-system", "rig": "rtkelly13/rig", "blog": "rtkelly13/blog"}
CLAIMS = [
    VercelClaim("design-system", "rtkelly-labs/design-system", ("design-system", "ds-review")),
    VercelClaim("rig", "rtkelly13/rig", ("shared-utilities",)),
]


def config(tmp_path: Path, *projects: dict[str, object]) -> Path:
    path = tmp_path / "managed-projects.json"
    path.write_text(json.dumps({"projects": list(projects)}), encoding="utf-8")
    return path


def verdicts(owned: list[Ownership], claims: list[VercelClaim] = CLAIMS) -> dict[str, list[str]]:
    found: dict[str, list[str]] = {}
    for finding in plan_findings(owned, claims, REPOS):
        found.setdefault(finding.vercel_project, []).append(finding.rule)
    return found


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


def test_repo_slugs_skip_disabled_projects(tmp_path: Path) -> None:
    cfg = config(
        tmp_path,
        {"name": "a", "repo": "git@github.com:o/a.git"},
        {"name": "b", "repo": "git@github.com:o/b.git", "enabled": False},
    )
    assert load_repo_slugs(cfg) == {"a": "o/a"}


def test_matching_declarations_are_confirmed() -> None:
    owned = [
        Ownership("shared-utilities", "rtkelly13/rig", "link"),
        Ownership("design-system", REPOS["design-system"], "commit"),
    ]
    assert verdicts(owned)["shared-utilities"] == ["ok"]
    assert verdicts(owned)["design-system"] == ["ok"]


def test_undeclared_projects_name_the_owner_vercel_reports() -> None:
    finding = next(
        f
        for f in plan_findings([Ownership("blog", "rtkelly13/blog", "link")], CLAIMS, REPOS)
        if f.vercel_project == "blog"
    )
    assert finding.rule == "undeclared" and "declare it under blog" in finding.detail


def test_a_link_naming_a_repos_old_owner_is_stale_but_still_confirms() -> None:
    owned = [Ownership("design-system", "rtkelly13/design-system", "link")]
    assert verdicts(owned)["design-system"] == ["stale-link", "ok"]


def test_declared_under_another_repo_is_a_mismatch() -> None:
    owned = [Ownership("shared-utilities", "rtkelly13/blog", "link")]
    assert verdicts(owned)["shared-utilities"] == ["mismatch"]


def test_no_link_or_commit_rests_on_the_declaration() -> None:
    assert verdicts([Ownership("ds-review", None, "none")])["ds-review"] == ["unverified"]
    assert verdicts([Ownership("mystery", None, "none")])["mystery"] == ["undeclared"]


def test_a_declared_name_vercel_lacks_is_missing() -> None:
    assert verdicts([])["ds-review"] == ["missing"]


def test_commit_source_tries_the_declared_repo_first() -> None:
    asked: list[str] = []

    def contains(slug: str, sha: str) -> bool:
        asked.append(slug)
        return slug == "rtkelly13/rig" and sha == "abc"

    assert commit_source("abc", REPOS, "rig", contains) == "rtkelly13/rig"
    assert asked == ["rtkelly13/rig"]
    assert commit_source("zzz", REPOS, None, lambda slug, sha: False) is None
