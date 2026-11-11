"""Tests for GitHub organization-level governance tooling (e.g. rtkelly-labs)."""

from __future__ import annotations

import json
import pathlib
from unittest.mock import MagicMock, patch

import pytest

from rig.repo_governance.align_repo_conventions import (
    discover_org_repos,
    resolve_target_directories,
)
from rig.repo_governance.policy_config import ManageDecision, PolicyConfig, load_policy_config, should_manage_repo


@pytest.fixture
def org_policy() -> PolicyConfig:
    return PolicyConfig.parse(
        {
            "defaults": {
                "expected_owner": "rtkelly13",
                "allowed_owners": ["rtkelly13", "rtkelly-labs"],
                "copyright_holder": "Ryan Kelly",
                "contact_email": "ryan@ryankelly.dev",
            },
            "profiles": {
                "rtkelly_labs": {
                    "expected_owner": "rtkelly-labs",
                    "allowed_owners": ["rtkelly-labs"],
                    "copyright_holder": "rtkelly-labs",
                    "contact_email": "ryan@ryankelly.dev",
                    "license": {"type": "MIT"},
                    "policies": ["squash_merge_only", "license", "community_templates"],
                    "required_checks": ["build", "pr-title"],
                    "repos": [".github", "experimental-tool"],
                },
                "personal": {
                    "expected_owner": "rtkelly13",
                    "allowed_owners": ["rtkelly13"],
                    "repos": ["blog"],
                },
            },
            "repo_overrides": {
                "Resultful": {
                    "expected_owner": "Resultful",
                    "copyright_holder": "Resultful Contributors",
                }
            },
        }
    )


@pytest.mark.ci
def test_policy_config_org_accessors(org_policy: PolicyConfig):
    # rtkelly_labs repo
    assert org_policy.expected_owner("experimental-tool") == "rtkelly-labs"
    assert org_policy.copyright_holder("experimental-tool") == "rtkelly-labs"
    assert org_policy.contact_email("experimental-tool") == "ryan@ryankelly.dev"
    assert org_policy.license_type("experimental-tool") == "MIT"
    assert org_policy.is_repo_in_org("experimental-tool", "rtkelly-labs") is True
    assert org_policy.is_repo_in_org("blog", "rtkelly-labs") is False

    # Default personal repo
    assert org_policy.expected_owner("blog") == "rtkelly13"
    assert org_policy.copyright_holder("blog") == "Ryan Kelly"

    # Repo override
    assert org_policy.copyright_holder("Resultful") == "Resultful Contributors"

    # repos_for_org
    repos = org_policy.repos_for_org("rtkelly-labs")
    assert set(repos) == {".github", "experimental-tool"}


@pytest.mark.ci
def test_shipped_config_rtkelly_labs_profile():
    cfg = load_policy_config()
    org_repos = cfg.repos_for_org("rtkelly-labs")
    assert ".github" in org_repos
    assert "design-system" in org_repos
    assert "Parquet.SourceGenerator" in org_repos
    assert "Parquet.TypeProvider" in org_repos
    assert cfg.copyright_holder(".github") == "rtkelly-labs"
    assert cfg.copyright_holder("design-system") == "rtkelly-labs"
    assert cfg.copyright_holder("Parquet.SourceGenerator") == "rtkelly-labs"
    assert cfg.has_policy(".github", "license")
    assert cfg.has_policy(".github", "community_templates")
    assert cfg.has_policy(".github", "security_policy")


@pytest.mark.ci
def test_shipped_config_demos_are_exempt_and_docs_is_governed():
    cfg = load_policy_config()
    for demo in ("MySQLEFConcurrencyBug", "AWSBatch-MapReduce"):
        decision = should_manage_repo(
            rules=cfg.repo_selection(demo), registered=cfg.is_registered(demo), is_fork=False, permission="ADMIN"
        )
        assert decision.managed is False
        assert "exempt" in decision.reason
    docs = should_manage_repo(
        rules=cfg.repo_selection("docs"), registered=cfg.is_registered("docs"), is_fork=False, permission="ADMIN"
    )
    assert docs.managed is True
    # docs' only pull_request job is `build`; the fallback names would make it unmergeable.
    assert cfg.required_checks("docs") == ("build",)


@pytest.mark.ci
def test_discover_org_repos_gh_cli(org_policy: PolicyConfig):
    mock_gh_output = json.dumps(
        [
            {"nameWithOwner": "rtkelly-labs/tool-a"},
            {"nameWithOwner": "rtkelly-labs/tool-b"},
        ]
    )
    with patch("subprocess.run") as mock_run:
        mock_run.return_value = MagicMock(stdout=mock_gh_output, returncode=0)
        repos = discover_org_repos("rtkelly-labs")
        assert repos == ["rtkelly-labs/tool-a", "rtkelly-labs/tool-b"]


@pytest.mark.ci
def test_discover_org_repos_fallback(org_policy: PolicyConfig):
    with (
        patch("subprocess.run", side_effect=FileNotFoundError("gh not found")),
        patch("rig.repo_governance.repo_discovery.load_policy_config", return_value=org_policy),
    ):
        repos = discover_org_repos("rtkelly-labs")
        assert "rtkelly-labs/.github" in repos
        assert "rtkelly-labs/experimental-tool" in repos


@pytest.mark.ci
def test_resolve_target_directories_filters_by_org(tmp_path: pathlib.Path, org_policy: PolicyConfig):
    personal_dir = tmp_path / "personal"
    personal_dir.mkdir()

    repo_personal = personal_dir / "blog"
    repo_personal.mkdir()
    (repo_personal / ".git").mkdir()

    repo_org = personal_dir / "experimental-tool"
    repo_org.mkdir()
    (repo_org / ".git").mkdir()

    with (
        patch("rig.repo_governance.repo_discovery.personal_repos_dir", return_value=personal_dir),
        patch("rig.repo_governance.repo_discovery.load_policy_config", return_value=org_policy),
        patch("rig.repo_governance.repo_discovery.is_managed_repo", return_value=ManageDecision(managed=True)),
    ):
        # Without org: both returned
        all_targets = resolve_target_directories(None, all_repos=True)
        assert len(all_targets) == 2

        # With org: only org repo returned
        org_targets = resolve_target_directories(None, all_repos=False, org="rtkelly-labs")
        assert len(org_targets) == 1
        assert org_targets[0].name == "experimental-tool"
