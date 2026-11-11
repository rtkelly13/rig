"""Tests for repository owner and organization governance enforcement."""

from __future__ import annotations

import pathlib
from unittest.mock import patch

import pytest
from typer.testing import CliRunner

from rig.repo_governance.align_repo_conventions import (
    app,
    inspect_repo_owner,
)
from rig.repo_governance.github_rulesets import (
    CheckNamesResult,
    RepoSettingsResult,
    RulesetInspection,
)
from rig.repo_governance.gitignore_manager import GitignoreInspection
from rig.repo_governance.policy_config import PolicyConfig
from rig.repo_governance.repo_discovery import RepoFound
from rig.repo_governance.repo_hygiene import HygieneResult

runner = CliRunner()


@pytest.fixture
def mock_policy() -> PolicyConfig:
    return PolicyConfig.parse(
        {
            "defaults": {
                "expected_owner": "rtkelly13",
                "allowed_owners": ["rtkelly13", "rtkelly-labs"],
            },
            "profiles": {
                "rtkelly_labs": {
                    "expected_owner": "rtkelly-labs",
                    "allowed_owners": ["rtkelly-labs"],
                    "copyright_holder": "rtkelly-labs",
                    "required_checks": ["build"],
                    "repos": [".github", "experimental-tool"],
                },
                "community": {
                    "expected_owner": None,
                    "allowed_owners": ["rtkelly13", "rtkelly-labs", "Resultful"],
                    "repos": ["collab-repo"],
                },
            },
            "owner_profiles": {"rtkelly-labs": "rtkelly_labs"},
            "repo_overrides": {
                "Resultful": {
                    "expected_owner": "Resultful",
                    "allowed_owners": ["Resultful"],
                }
            },
        }
    )


@pytest.mark.ci
def test_inspect_repo_owner_aligned(mock_policy: PolicyConfig):
    res = inspect_repo_owner(repo_name="rtkelly13/blog", config=mock_policy)
    assert res.is_aligned is True
    assert res.status == "ALIGNED"
    assert res.actual_owner == "rtkelly13"
    assert res.expected_owner == "rtkelly13"


@pytest.mark.ci
def test_inspect_repo_owner_case_insensitive(mock_policy: PolicyConfig):
    res = inspect_repo_owner(repo_name="RTKELLY13/blog", config=mock_policy)
    assert res.is_aligned is True
    assert res.status == "ALIGNED"
    assert res.actual_owner == "RTKELLY13"


@pytest.mark.ci
def test_inspect_repo_owner_org_profile_aligned(mock_policy: PolicyConfig):
    res = inspect_repo_owner(repo_name="rtkelly-labs/.github", config=mock_policy)
    assert res.is_aligned is True
    assert res.status == "ALIGNED"
    assert res.actual_owner == "rtkelly-labs"
    assert res.expected_owner == "rtkelly-labs"


@pytest.mark.ci
def test_inspect_repo_owner_wrong_owner_fails(mock_policy: PolicyConfig):
    # .github is configured to expect rtkelly-labs, but is under rtkelly13
    res = inspect_repo_owner(repo_name="rtkelly13/.github", config=mock_policy)
    assert res.is_aligned is False
    assert res.status == "WRONG_OWNER"
    assert "Expected owner 'rtkelly-labs'" in res.message


@pytest.mark.ci
def test_inspect_repo_owner_unauthorized_owner_fails(mock_policy: PolicyConfig):
    # collab-repo has expected_owner=None, and allowed_owners=["rtkelly13", "rtkelly-labs", "Resultful"]
    res = inspect_repo_owner(repo_name="rogue-user/collab-repo", config=mock_policy)
    assert res.is_aligned is False
    assert res.status == "UNAUTHORIZED_OWNER"
    assert "not in allowed owners" in res.message


@pytest.mark.ci
def test_inspect_repo_owner_repo_override(mock_policy: PolicyConfig):
    res = inspect_repo_owner(repo_name="Resultful/Resultful", config=mock_policy)
    assert res.is_aligned is True
    assert res.status == "ALIGNED"
    assert res.expected_owner == "Resultful"

    # If Resultful was under rtkelly13, override expected_owner should flag it
    res_wrong = inspect_repo_owner(repo_name="rtkelly13/Resultful", config=mock_policy)
    assert res_wrong.is_aligned is False
    assert res_wrong.status == "WRONG_OWNER"


@pytest.mark.ci
def test_inspect_repo_owner_unknown_owner(mock_policy: PolicyConfig):
    res = inspect_repo_owner(repo_name="invalid-no-slash", config=mock_policy)
    assert res.is_aligned is False
    assert res.status == "UNKNOWN_OWNER"


@pytest.mark.ci
def test_unregistered_org_repo_takes_its_owners_profile(mock_policy: PolicyConfig):
    res = inspect_repo_owner(repo_name="rtkelly-labs/new-tool", config=mock_policy)
    assert res.status == "ALIGNED"
    assert res.expected_owner == "rtkelly-labs"
    assert res.inferred_profile == "rtkelly_labs"
    assert "not adopted: add it to profile rtkelly_labs" in res.message
    # Identity comes from the owner's profile; rules and policies stay registration-only.
    assert mock_policy.copyright_holder("rtkelly-labs/new-tool") == "rtkelly-labs"
    assert mock_policy.required_checks("rtkelly-labs/new-tool") == ()
    assert mock_policy.required_checks("rtkelly-labs/experimental-tool") == ("build",)
    assert mock_policy.is_registered("rtkelly-labs/new-tool") is False


@pytest.mark.ci
def test_owner_profile_match_ignores_case(mock_policy: PolicyConfig):
    res = inspect_repo_owner(repo_name="RTKelly-Labs/new-tool", config=mock_policy)
    assert res.is_aligned is True
    assert res.inferred_profile == "rtkelly_labs"


@pytest.mark.ci
def test_explicit_registration_wins_over_the_owner_profile(mock_policy: PolicyConfig):
    # collab-repo is named by `community`, so living in rtkelly-labs does not pull it into rtkelly_labs.
    res = inspect_repo_owner(repo_name="rtkelly-labs/collab-repo", config=mock_policy)
    assert res.is_aligned is True
    assert res.inferred_profile is None
    assert res.expected_owner is None
    assert mock_policy.owner_profile_for("rtkelly-labs/collab-repo") is None


@pytest.mark.ci
def test_an_unmapped_owner_falls_back_to_defaults(mock_policy: PolicyConfig):
    assert mock_policy.owner_profile_for("Resultful/something") is None
    res = inspect_repo_owner(repo_name="Resultful/something", config=mock_policy)
    assert res.status == "WRONG_OWNER"
    assert res.expected_owner == "rtkelly13"
    assert res.inferred_profile is None


@pytest.mark.ci
@pytest.mark.parametrize("key", ["Experimental-Tool", "RTKELLY-LABS/experimental-tool", "someone/EXPERIMENTAL-TOOL"])
def test_registration_matches_a_remote_name_spelled_with_other_casing(mock_policy: PolicyConfig, key: str):
    # GitHub names ignore case, so a remote-derived key keeps its profile however it is spelled.
    assert mock_policy.profile_for(key) == "rtkelly_labs"
    assert mock_policy.required_checks(key) == ("build",)
    assert mock_policy.owner_profile_for(key) is None


@pytest.mark.ci
def test_repo_overrides_match_a_remote_name_spelled_with_other_casing(mock_policy: PolicyConfig):
    assert mock_policy.expected_owner("resultful/resultful") == "Resultful"
    assert mock_policy.is_repo_in_org("RESULTFUL", "resultful")


@pytest.mark.ci
def test_a_bare_name_never_infers_an_owner_profile(mock_policy: PolicyConfig):
    assert mock_policy.owner_profile_for("new-tool") is None
    assert mock_policy.expected_owner("new-tool") == "rtkelly13"


@pytest.mark.ci
def test_owner_profiles_naming_an_undeclared_profile_is_rejected():
    with pytest.raises(ValueError, match="rtkelly-labs -> missing_profile"):
        PolicyConfig.parse({"profiles": {}, "owner_profiles": {"rtkelly-labs": "missing_profile"}})


@pytest.mark.ci
def test_audit_reports_an_unadopted_repo_as_advisory_not_drift(tmp_path: pathlib.Path, mock_policy: PolicyConfig):
    aligned = HygieneResult(status="CONFIGURED", is_aligned=True)
    with (
        patch("rig.repo_governance.cli_repo_checks.resolve_target_directories", return_value=[tmp_path]),
        patch(
            "rig.repo_governance.cli_repo_checks.inspect_repo_exists",
            return_value=RepoFound(status="EXISTS", message="", repo_name="rtkelly-labs/new-tool"),
        ),
        patch("rig.repo_governance.cli_repo_checks.load_policy_config", return_value=mock_policy),
        patch(
            "rig.repo_governance.cli_repo_checks.inspect_repo_settings",
            return_value=RepoSettingsResult(is_aligned=True),
        ),
        patch(
            "rig.repo_governance.cli_repo_checks.inspect_github_ruleset",
            return_value=RulesetInspection(status="ACTIVE", is_aligned=True, id=1),
        ),
        patch("rig.repo_governance.cli_repo_checks.inspect_npmrc", return_value=aligned),
        patch("rig.repo_governance.cli_repo_checks.inspect_agents_md", return_value=aligned),
        patch(
            "rig.repo_governance.cli_repo_checks.inspect_gitignore",
            return_value=GitignoreInspection(
                languages=(), missing=(), missing_lines=(), file=".gitignore", status="CONFIGURED", is_aligned=True
            ),
        ),
        patch("rig.repo_governance.cli_repo_checks.inspect_todo_md", return_value=aligned),
        patch("rig.repo_governance.cli_repo_checks.inspect_pinned_shas", return_value=aligned),
        patch("rig.repo_governance.cli_repo_checks.inspect_ls_lint", return_value=aligned),
        patch(
            "rig.repo_governance.cli_repo_checks.inspect_check_names",
            return_value=CheckNamesResult(
                status="ALIGNED", required=(), declared={}, missing=(), warnings={}, registered=True, is_aligned=True
            ),
        ),
    ):
        result = runner.invoke(app, ["audit", str(tmp_path)])
    output = " ".join(result.output.split())
    assert result.exit_code == 0, output
    assert "not adopted: add it to profile rtkelly_labs" in output
    assert "governance failure" not in output


@pytest.mark.ci
def test_audit_fails_on_owner_drift(tmp_path: pathlib.Path, mock_policy: PolicyConfig):
    (tmp_path / ".git").mkdir()
    with (
        patch(
            "rig.repo_governance.cli_repo_checks.resolve_target_directories",
            return_value=[tmp_path],
        ),
        patch(
            "rig.repo_governance.cli_repo_checks.inspect_repo_exists",
            return_value=RepoFound(status="EXISTS", message="", repo_name="wrong-org/blog"),
        ),
        patch("rig.repo_governance.cli_repo_checks.load_policy_config", return_value=mock_policy),
        patch(
            "rig.repo_governance.cli_repo_checks.inspect_repo_settings",
            return_value=RepoSettingsResult(is_aligned=True),
        ),
        patch(
            "rig.repo_governance.cli_repo_checks.inspect_github_ruleset",
            return_value=RulesetInspection(status="ACTIVE", is_aligned=True, id=1),
        ),
        patch(
            "rig.repo_governance.cli_repo_checks.inspect_npmrc",
            return_value=HygieneResult(status="CONFIGURED", is_aligned=True),
        ),
        patch(
            "rig.repo_governance.cli_repo_checks.inspect_agents_md",
            return_value=HygieneResult(status="CONFIGURED", is_aligned=True),
        ),
        patch(
            "rig.repo_governance.cli_repo_checks.inspect_gitignore",
            return_value=GitignoreInspection(
                languages=(), missing=(), missing_lines=(), file=".gitignore", status="CONFIGURED", is_aligned=True
            ),
        ),
        patch(
            "rig.repo_governance.cli_repo_checks.inspect_todo_md",
            return_value=HygieneResult(status="PRESENT", is_aligned=True),
        ),
        patch(
            "rig.repo_governance.cli_repo_checks.inspect_pinned_shas",
            return_value=HygieneResult(status="PINNED", is_aligned=True),
        ),
        patch(
            "rig.repo_governance.cli_repo_checks.inspect_check_names",
            return_value=CheckNamesResult(
                status="ALIGNED", required=(), declared={}, missing=(), warnings={}, registered=True, is_aligned=True
            ),
        ),
    ):
        result = runner.invoke(app, ["audit", str(tmp_path)])
        assert result.exit_code == 1
        assert "Repository owner/org governance failure" in result.output
        assert "AUDIT FAILED: 1/1 repositories have configuration drift." in result.output


@pytest.mark.ci
def test_enforce_blocks_mutation_on_owner_drift(tmp_path: pathlib.Path, mock_policy: PolicyConfig):
    (tmp_path / ".git").mkdir()
    with (
        patch(
            "rig.repo_governance.cli_repo_checks.resolve_target_directories",
            return_value=[tmp_path],
        ),
        patch(
            "rig.repo_governance.cli_repo_checks.inspect_repo_exists",
            return_value=RepoFound(status="EXISTS", message="", repo_name="unauthorized-owner/blog"),
        ),
        patch("rig.repo_governance.cli_repo_checks.load_policy_config", return_value=mock_policy),
        patch("rig.repo_governance.cli_repo_checks.align_github_repo_settings") as mock_settings,
        patch("rig.repo_governance.cli_repo_checks.align_github_ruleset") as mock_ruleset,
    ):
        result = runner.invoke(app, ["enforce", str(tmp_path)])
        mock_settings.assert_not_called()
        mock_ruleset.assert_not_called()
        assert "refusing to enforce conventions" in result.output
