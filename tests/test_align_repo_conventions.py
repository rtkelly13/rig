import json
import pathlib
import subprocess
import textwrap
from unittest.mock import MagicMock, patch

import pytest
from typer.testing import CliRunner

from rig.repo_governance.align_repo_conventions import (
    AGENTS_CONVENTIONS_HEADING,
    EXPECTED_AGENTS_CONVENTIONS,
    align_agents_md,
    align_github_repo_settings,
    align_npmrc,
    app,
    build_ruleset_payload,
    get_gh_repo_name,
    inspect_check_names,
    inspect_npmrc,
    inspect_repo_exists,
    inspect_repo_settings,
)
from rig.repo_governance.check_names import (
    discover_pr_check_names,
    lint_check_name,
    suggest_preferred_name,
)
from rig.repo_governance.github_rulesets import (
    RULESET_NAME,
    AlignResult,
    RepoSettingsResult,
    RulesetInspection,
    align_github_ruleset,
    registered_required_checks,
    ruleset_drift,
)
from rig.repo_governance.gitignore_manager import GitignoreInspection
from rig.repo_governance.policy_config import PolicyConfig, load_policy_config
from rig.repo_governance.repo_discovery import OwnerInspection, RepoFound
from rig.repo_governance.repo_hygiene import HygieneResult
from rig.typed_json import load_json
from tests.fakes.github_rest import FakeGitHubApi

runner = CliRunner()

REPO_ROOT = pathlib.Path(__file__).resolve().parents[1]


def _requiring(repo: str, checks: list[str]):
    """Declare *checks* as *repo*'s required checks, the way config/repo_policies.json would."""
    cfg = PolicyConfig.parse({"repo_overrides": {repo.rsplit("/", 1)[-1]: {"required_checks": checks}}})
    return patch("rig.repo_governance.github_rulesets.load_policy_config", return_value=cfg)


def _done(stdout: str = "") -> subprocess.CompletedProcess[str]:
    """A command that exited 0 and printed *stdout*."""
    return subprocess.CompletedProcess([], 0, stdout, "")


def _failed(stderr: str) -> subprocess.CompletedProcess[str]:
    """A command that exited 1 and printed *stderr*."""
    return subprocess.CompletedProcess([], 1, "", stderr)


def test_inspect_repo_exists_success(tmp_path: pathlib.Path):
    (tmp_path / ".git").mkdir()
    with (
        patch(
            "rig.repo_governance.repo_discovery.get_gh_repo_name",
            return_value="rtkelly13/test-repo",
        ),
        patch("subprocess.run") as mock_run,
    ):
        mock_run.return_value = _done("rtkelly13/test-repo\n")
        res = inspect_repo_exists(tmp_path)
        assert res.exists is True
        assert res.status == "EXISTS"
        assert res.repo_name == "rtkelly13/test-repo"


def test_inspect_repo_exists_missing_local(tmp_path: pathlib.Path):
    res = inspect_repo_exists(tmp_path)
    assert res.exists is False
    assert res.status == "MISSING_LOCAL"


def test_inspect_repo_exists_not_linked(tmp_path: pathlib.Path):
    (tmp_path / ".git").mkdir()
    with patch("rig.repo_governance.repo_discovery.get_gh_repo_name", return_value=None):
        res = inspect_repo_exists(tmp_path)
        assert res.exists is False
        assert res.status == "NOT_LINKED_TO_GITHUB"


def test_inspect_repo_exists_not_found_on_github(tmp_path: pathlib.Path):
    (tmp_path / ".git").mkdir()
    err = _failed("Could not resolve")
    with (
        patch(
            "rig.repo_governance.repo_discovery.get_gh_repo_name",
            return_value="rtkelly13/nonexistent",
        ),
        patch("subprocess.run", return_value=err),
    ):
        res = inspect_repo_exists(tmp_path)
        assert res.exists is False
        assert res.status == "NOT_FOUND_ON_GITHUB"


def test_get_gh_repo_name_success(tmp_path: pathlib.Path):
    with patch("subprocess.run") as mock_run:
        mock_run.return_value = _done("rtkelly13/design-system\n")
        repo_name = get_gh_repo_name(tmp_path)
        assert repo_name == "rtkelly13/design-system"


def test_get_gh_repo_name_failure(tmp_path: pathlib.Path):
    with patch("subprocess.run", side_effect=FileNotFoundError("gh command failed")):
        assert get_gh_repo_name(tmp_path) is None


_SETTINGS_REPO = "rtkelly13/design-system"
_ALIGNED_SETTINGS = {
    "default_branch": "main",
    "delete_branch_on_merge": True,
    "allow_squash_merge": True,
    "allow_merge_commit": False,
    "allow_rebase_merge": False,
    "allow_auto_merge": True,
}


def _settings(**overrides: object) -> FakeGitHubApi:
    return FakeGitHubApi({f"repos/{_SETTINGS_REPO}": {**_ALIGNED_SETTINGS, **overrides}})


def test_inspect_repo_settings_aligned():
    res = inspect_repo_settings(_SETTINGS_REPO, api=_settings(), cfg=PolicyConfig.parse({}))
    assert res.is_aligned is True
    assert res.delete_on_merge is True
    assert res.squash_only is True


def test_inspect_repo_settings_drifted():
    api = _settings(delete_branch_on_merge=False, allow_merge_commit=True)
    res = inspect_repo_settings(_SETTINGS_REPO, api=api, cfg=PolicyConfig.parse({}))
    assert res.is_aligned is False


def test_inspect_repo_settings_base_branch_aligned():
    res = inspect_repo_settings(_SETTINGS_REPO, expected_branch="main", api=_settings(), cfg=PolicyConfig.parse({}))
    assert res.is_aligned is True
    assert res.base_branch_aligned is True
    assert res.default_branch == "main"
    assert res.expected_branch == "main"


def test_inspect_repo_settings_base_branch_deviation():
    api = _settings(default_branch="master")
    res = inspect_repo_settings(_SETTINGS_REPO, expected_branch="main", api=api, cfg=PolicyConfig.parse({}))
    assert res.is_aligned is False
    assert res.base_branch_aligned is False
    assert res.default_branch == "master"
    assert res.expected_branch == "main"


def test_align_github_repo_settings_execution():
    cfg = PolicyConfig.parse({})
    api = _settings(default_branch="master", allow_merge_commit=True)
    res = align_github_repo_settings(_SETTINGS_REPO, dry_run=False, api=api, cfg=cfg)
    assert res.status == "success"
    [write] = api.writes
    assert (write.method, write.route) == ("PATCH", f"repos/{_SETTINGS_REPO}")
    assert inspect_repo_settings(_SETTINGS_REPO, api=api, cfg=cfg).is_aligned


def test_align_github_repo_settings_dry_run():
    res = align_github_repo_settings("rtkelly13/design-system", dry_run=True)
    assert res.status == "dry_run"
    assert res.command.startswith("PATCH repos/rtkelly13/design-system ")
    assert '"default_branch":"main"' in res.command

    res_custom = align_github_repo_settings("rtkelly13/design-system", expected_branch="develop", dry_run=True)
    assert '"default_branch":"develop"' in res_custom.command


def test_inspect_npmrc_no_package_json(tmp_path: pathlib.Path):
    res = inspect_npmrc(tmp_path)
    assert res.is_aligned is True
    assert "No package.json" in res.status


def test_inspect_npmrc_configured(tmp_path: pathlib.Path):
    (tmp_path / "package.json").write_text("{}", encoding="utf-8")
    (tmp_path / ".npmrc").write_text("@rtkelly13:registry=https://npm.pkg.github.com\n", encoding="utf-8")
    res = inspect_npmrc(tmp_path)
    assert res.is_aligned is True
    assert res.status == "CONFIGURED"


def test_inspect_npmrc_missing(tmp_path: pathlib.Path):
    (tmp_path / "package.json").write_text("{}", encoding="utf-8")
    res = inspect_npmrc(tmp_path)
    assert res.is_aligned is False
    assert res.status == "MISSING / DRIFTED"


def test_align_npmrc_creates_file(tmp_path: pathlib.Path):
    (tmp_path / "package.json").write_text("{}", encoding="utf-8")
    res = align_npmrc(tmp_path, dry_run=False)
    assert res.status == "aligned"
    assert (tmp_path / ".npmrc").exists()
    assert "@rtkelly13:registry=https://npm.pkg.github.com" in (tmp_path / ".npmrc").read_text(encoding="utf-8")


@pytest.mark.parametrize("ruleset_aligned", [True, False])
def test_status_command_aligned(tmp_path: pathlib.Path, ruleset_aligned: bool):
    (tmp_path / ".git").mkdir()
    with (
        patch(
            "rig.repo_governance.cli_repo_checks.inspect_repo_exists",
            return_value=RepoFound(status="EXISTS", message="", repo_name="rtkelly13/test-repo"),
        ),
        patch(
            "rig.repo_governance.cli_repo_checks.get_gh_repo_name",
            return_value="rtkelly13/test-repo",
        ),
        patch(
            "rig.repo_governance.cli_repo_checks.inspect_repo_settings",
            return_value=RepoSettingsResult(is_aligned=True),
        ),
        patch(
            "rig.repo_governance.cli_repo_checks.inspect_github_ruleset",
            return_value=RulesetInspection(
                status="ACTIVE" if ruleset_aligned else "DRIFTED: review threads", is_aligned=ruleset_aligned, id=1
            ),
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
            "rig.repo_governance.cli_repo_checks.inspect_ls_lint",
            return_value=HygieneResult(status="CONFIGURED", is_aligned=True),
        ),
    ):
        result = runner.invoke(app, ["status", str(tmp_path)])
        assert result.exit_code == 0
        assert "STATUS / PLAN" in result.output
        assert ("ALIGNED" if ruleset_aligned else "DRIFTED") in result.output


def test_status_command_drifted(tmp_path: pathlib.Path):
    (tmp_path / ".git").mkdir()
    with (
        patch(
            "rig.repo_governance.cli_repo_checks.inspect_repo_exists",
            return_value=RepoFound(status="EXISTS", message="", repo_name="rtkelly13/test-repo"),
        ),
        patch(
            "rig.repo_governance.cli_repo_checks.get_gh_repo_name",
            return_value="rtkelly13/test-repo",
        ),
        patch(
            "rig.repo_governance.cli_repo_checks.inspect_repo_settings",
            return_value=RepoSettingsResult(is_aligned=False),
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
    ):
        result = runner.invoke(app, ["status", str(tmp_path)])
        assert result.exit_code == 0
        assert "DRIFTED" in result.output


@pytest.mark.parametrize("ruleset_aligned", [True, False])
def test_audit_command_success(tmp_path: pathlib.Path, ruleset_aligned: bool):
    (tmp_path / ".git").mkdir()
    with (
        patch(
            "rig.repo_governance.cli_repo_checks.inspect_repo_exists",
            return_value=RepoFound(status="EXISTS", message="", repo_name="rtkelly13/test-repo"),
        ),
        patch(
            "rig.repo_governance.cli_repo_checks.get_gh_repo_name",
            return_value="rtkelly13/test-repo",
        ),
        patch(
            "rig.repo_governance.cli_repo_checks.inspect_repo_settings",
            return_value=RepoSettingsResult(is_aligned=True),
        ),
        patch(
            "rig.repo_governance.cli_repo_checks.inspect_github_ruleset",
            return_value=RulesetInspection(
                status="ACTIVE" if ruleset_aligned else "DRIFTED: review threads", is_aligned=ruleset_aligned, id=1
            ),
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
            "rig.repo_governance.cli_repo_checks.inspect_ls_lint",
            return_value=HygieneResult(status="CONFIGURED", is_aligned=True),
        ),
    ):
        result = runner.invoke(app, ["audit", str(tmp_path)])
        assert result.exit_code == (0 if ruleset_aligned else 1)
        assert ("FULLY COMPLIANT" if ruleset_aligned else "GitHub Ruleset: DRIFTED") in result.output


def test_audit_command_failure_dependabot_renovate_conflict(tmp_path: pathlib.Path):
    (tmp_path / ".git").mkdir()
    with (
        patch(
            "rig.repo_governance.cli_repo_checks.inspect_repo_exists",
            return_value=RepoFound(status="EXISTS", message="", repo_name="rtkelly13/test-repo"),
        ),
        patch(
            "rig.repo_governance.cli_repo_checks.get_gh_repo_name",
            return_value="rtkelly13/test-repo",
        ),
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
            "rig.repo_governance.cli_repo_checks.inspect_ls_lint",
            return_value=HygieneResult(status="CONFIGURED", is_aligned=True),
        ),
        patch(
            "rig.repo_governance.cli_repo_checks.inspect_dependabot_renovate",
            return_value=HygieneResult(
                status="CONFLICT",
                is_aligned=False,
                reason="Conflicting dependency automation: found both Renovate config and active Dependabot updates",
            ),
        ),
    ):
        result = runner.invoke(app, ["audit", str(tmp_path)])
        assert result.exit_code == 1
        assert "DRIFT DETECTED" in result.output
        assert "Dependabot / Renovate conflict" in result.output


def test_audit_command_failure_when_drifted(tmp_path: pathlib.Path):
    (tmp_path / ".git").mkdir()
    with (
        patch(
            "rig.repo_governance.cli_repo_checks.inspect_repo_exists",
            return_value=RepoFound(status="EXISTS", message="", repo_name="rtkelly13/test-repo"),
        ),
        patch(
            "rig.repo_governance.cli_repo_checks.get_gh_repo_name",
            return_value="rtkelly13/test-repo",
        ),
        patch(
            "rig.repo_governance.cli_repo_checks.inspect_repo_settings",
            return_value=RepoSettingsResult(is_aligned=False),
        ),
        patch(
            "rig.repo_governance.cli_repo_checks.inspect_npmrc",
            return_value=HygieneResult(status="MISSING", is_aligned=False),
        ),
        patch(
            "rig.repo_governance.cli_repo_checks.inspect_agents_md",
            return_value=HygieneResult(status="MISSING", is_aligned=False),
        ),
        patch(
            "rig.repo_governance.cli_repo_checks.inspect_gitignore",
            return_value=GitignoreInspection(
                languages=(), missing=(), missing_lines=(), file=".gitignore", status="MISSING", is_aligned=False
            ),
        ),
        patch(
            "rig.repo_governance.cli_repo_checks.inspect_todo_md",
            return_value=HygieneResult(status="MISSING", is_aligned=False),
        ),
    ):
        result = runner.invoke(app, ["audit", str(tmp_path)])
        assert result.exit_code == 1
        assert "AUDIT FAILED" in result.output


def test_status_command_base_branch_drift(tmp_path: pathlib.Path):
    (tmp_path / ".git").mkdir()
    with (
        patch(
            "rig.repo_governance.cli_repo_checks.inspect_repo_exists",
            return_value=RepoFound(status="EXISTS", message="", repo_name="rtkelly13/test-repo"),
        ),
        patch(
            "rig.repo_governance.cli_repo_checks.get_gh_repo_name",
            return_value="rtkelly13/test-repo",
        ),
        patch(
            "rig.repo_governance.cli_repo_checks.inspect_repo_settings",
            return_value=RepoSettingsResult(
                is_aligned=False,
                delete_on_merge=True,
                squash_only=True,
                auto_merge=True,
                default_branch="master",
                expected_branch="main",
                base_branch_aligned=False,
            ),
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
            "rig.repo_governance.cli_repo_checks.inspect_ls_lint",
            return_value=HygieneResult(status="CONFIGURED", is_aligned=True),
        ),
    ):
        result = runner.invoke(app, ["status", str(tmp_path)], env={"COLUMNS": "260"})
        assert result.exit_code == 0
        assert "DRIFTED" in result.output
        assert "master" in result.output
        assert "need main" in result.output


def test_audit_command_failure_base_branch_drift(tmp_path: pathlib.Path):
    (tmp_path / ".git").mkdir()
    with (
        patch(
            "rig.repo_governance.cli_repo_checks.inspect_repo_exists",
            return_value=RepoFound(status="EXISTS", message="", repo_name="rtkelly13/test-repo"),
        ),
        patch(
            "rig.repo_governance.cli_repo_checks.get_gh_repo_name",
            return_value="rtkelly13/test-repo",
        ),
        patch(
            "rig.repo_governance.cli_repo_checks.inspect_repo_settings",
            return_value=RepoSettingsResult(
                is_aligned=False,
                delete_on_merge=True,
                squash_only=True,
                auto_merge=True,
                default_branch="master",
                expected_branch="main",
                base_branch_aligned=False,
            ),
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
            "rig.repo_governance.cli_repo_checks.inspect_ls_lint",
            return_value=HygieneResult(status="CONFIGURED", is_aligned=True),
        ),
    ):
        result = runner.invoke(app, ["audit", str(tmp_path)])
        assert result.exit_code == 1
        assert "DRIFTED DETECTED" in result.output or "DRIFT DETECTED" in result.output
        assert "Base branch deviation: default branch is 'master', expected 'main'" in result.output


_CHECKS = "rig.repo_governance.cli_repo_checks"


def _patch_enforce_remote() -> dict[str, MagicMock]:
    """Mocks for every GitHub-touching call `enforce` makes, so no test reaches the network."""
    return {
        "inspect_repo_exists": MagicMock(
            return_value=RepoFound(status="EXISTS", repo_name="rtkelly13/test-repo", message="")
        ),
        "inspect_repo_owner": MagicMock(
            return_value=OwnerInspection(
                status="ALIGNED",
                actual_owner="rtkelly13",
                expected_owner=None,
                allowed_owners=(),
                is_aligned=True,
                message="",
            )
        ),
        "align_github_repo_settings": MagicMock(return_value=AlignResult(status="success")),
        "align_github_ruleset": MagicMock(return_value=AlignResult(status="success")),
    }


def test_enforce_command_dry_run(tmp_path: pathlib.Path):
    (tmp_path / ".git").mkdir()
    mocks = _patch_enforce_remote()
    with patch.multiple(_CHECKS, **mocks):
        result = runner.invoke(app, ["enforce", str(tmp_path), "--dry-run"])
    assert result.exit_code == 0, result.output
    assert "Conventions Enforcement Complete!" in result.output
    mocks["align_github_repo_settings"].assert_called_once_with(
        "rtkelly13/test-repo", expected_branch="main", dry_run=True
    )
    assert not (tmp_path / "AGENTS.md").exists()


def test_enforce_command_execution(tmp_path: pathlib.Path):
    (tmp_path / ".git").mkdir()
    mocks = _patch_enforce_remote()
    with (
        patch.multiple(_CHECKS, **mocks),
        patch(f"{_CHECKS}.align_git_hooks", return_value=HygieneResult(status="success")) as hooks,
        patch(f"{_CHECKS}.align_npmrc", return_value=HygieneResult(status="already_configured")),
    ):
        result = runner.invoke(app, ["enforce", str(tmp_path)])
    assert result.exit_code == 0, result.output
    assert "Conventions Enforcement Complete" in result.output
    mocks["align_github_repo_settings"].assert_called_once_with(
        "rtkelly13/test-repo", expected_branch="main", dry_run=False
    )
    hooks.assert_called_once_with(tmp_path, dry_run=False)


def test_verify_pr_checks_all_passing():
    mock_checks = [
        {"name": "Lint & Typecheck", "state": "SUCCESS", "bucket": "pass"},
        {"name": "Tests & Coverage", "state": "SUCCESS", "bucket": "pass"},
    ]
    mock_threads = {
        "data": {
            "repository": {
                "pullRequest": {
                    "reviewThreads": {
                        "pageInfo": {"hasNextPage": False, "endCursor": None},
                        "nodes": [{"id": "t1", "isResolved": True, "comments": {"nodes": []}}],
                    }
                }
            }
        }
    }
    with patch("subprocess.run") as mock_run:

        def side_effect(cmd: list[str], **kwargs: object) -> MagicMock:
            if "checks" in cmd:
                return MagicMock(returncode=0, stdout=json.dumps(mock_checks))
            if "graphql" in cmd:
                return MagicMock(returncode=0, stdout=json.dumps(mock_threads))
            return MagicMock(returncode=0, stdout="rtkelly13/custom-unregistered")

        mock_run.side_effect = side_effect
        result = runner.invoke(app, ["verify-pr-checks", "123", "--repo", "rtkelly13/custom-unregistered"])
        assert result.exit_code == 0
        assert "every conversation thread is resolved" in result.output


def test_verify_pr_checks_unreadable_output_exits_cleanly():
    with patch("subprocess.run") as mock_run:
        mock_run.return_value.returncode = 0
        mock_run.return_value.stdout = '{"not": "a list"}'
        result = runner.invoke(app, ["verify-pr-checks", "123"])
        assert result.exit_code == 1
        assert "Unreadable check list" in result.output


def test_verify_pr_checks_missing_hardcoded_check():
    mock_checks = [
        {"name": "Lint & Typecheck", "state": "SUCCESS", "bucket": "pass"},
    ]
    with patch("subprocess.run") as mock_run:
        mock_run.return_value.returncode = 0
        mock_run.return_value.stdout = json.dumps(mock_checks)
        result = runner.invoke(app, ["verify-pr-checks", "123"])
        assert result.exit_code == 1
        assert "BLOCKED" in result.output
        assert "Tests & Coverage" in result.output


def test_verify_pr_checks_failing_check():
    mock_checks = [
        {"name": "Lint & Typecheck", "state": "SUCCESS", "bucket": "pass"},
        {"name": "Tests & Coverage", "state": "FAILURE", "bucket": "fail"},
    ]
    with patch("subprocess.run") as mock_run:
        mock_run.return_value.returncode = 0
        mock_run.return_value.stdout = json.dumps(mock_checks)
        result = runner.invoke(app, ["verify-pr-checks", "123"])
        assert result.exit_code == 1
        assert "BLOCKED" in result.output
        assert "Tests & Coverage" in result.output


def test_verify_pr_checks_pending_check():
    mock_checks = [
        {"name": "Lint & Typecheck", "state": "SUCCESS", "bucket": "pass"},
        {"name": "Tests & Coverage", "state": "IN_PROGRESS", "bucket": "pending"},
    ]
    with patch("subprocess.run") as mock_run:
        mock_run.return_value.returncode = 0
        mock_run.return_value.stdout = json.dumps(mock_checks)
        result = runner.invoke(app, ["verify-pr-checks", "123"])
        assert result.exit_code == 1
        assert "BLOCKED" in result.output
        assert "pending" in result.output


def test_inspect_pinned_shas_no_workflows(tmp_path: pathlib.Path):
    from rig.repo_governance.align_repo_conventions import inspect_pinned_shas

    res = inspect_pinned_shas(tmp_path)
    assert res.is_aligned is True
    assert res.status == "N/A (No Workflows)"


def test_inspect_pinned_shas_counts_unpinned_refs(tmp_path: pathlib.Path):
    from rig.repo_governance.align_repo_conventions import inspect_pinned_shas

    wf_dir = tmp_path / ".github" / "workflows"
    wf_dir.mkdir(parents=True)
    wf_file = wf_dir / "ci.yml"
    wf_file.write_text("    uses: actions/checkout@v4\n    uses: actions/cache@v4\n", encoding="utf-8")

    res_before = inspect_pinned_shas(tmp_path)
    assert res_before.is_aligned is False
    assert res_before.unpinned_count == 2

    sha = "11d5960a326750d5838078e36cf38b85af677262"
    wf_file.write_text(f"    uses: actions/checkout@{sha} # v4.4.0\n", encoding="utf-8")

    res_after = inspect_pinned_shas(tmp_path)
    assert res_after.is_aligned is True
    assert res_after.status == "PINNED"


@pytest.mark.ci
def test_enforce_never_rewrites_workflow_pins(tmp_path: pathlib.Path):
    """Pins are owned by pinact and Dependabot in each repo; `enforce` only reports them."""
    (tmp_path / ".git").mkdir()
    wf_dir = tmp_path / ".github" / "workflows"
    wf_dir.mkdir(parents=True)
    wf_file = wf_dir / "ci.yml"
    wf_file.write_text("on: [push]\njobs:\n  a:\n    steps:\n      - uses: actions/checkout@v4\n", encoding="utf-8")
    before = wf_file.read_text(encoding="utf-8")

    with patch.multiple(_CHECKS, **_patch_enforce_remote()):
        result = runner.invoke(app, ["enforce", str(tmp_path)])

    assert result.exit_code == 0, result.output
    assert wf_file.read_text(encoding="utf-8") == before


# --- Required check names: shape, lexicon and drift ------------------------------------------


class TestCheckNameShape:
    """The lexicon rule: lowercase snake_case, at most two words, preferably a listed name."""

    @pytest.mark.ci
    @pytest.mark.parametrize(
        "name",
        [
            "ci",
            "build",
            "test",
            "tests",
            "lint",
            "visual",
            "pr_title",
            "pr-checks",
            "pr_checks",
            "benchmark",
            "deploy",
            "action_shas",
        ],
    )
    def test_lexicon_names_produce_no_warnings(self, name):
        assert lint_check_name(name) == []

    @pytest.mark.ci
    def test_uppercase_is_flagged(self):
        assert "not all lowercase" in lint_check_name("Build")

    @pytest.mark.ci
    @pytest.mark.parametrize("name", ["Lint & Typecheck", "PR checks", "unit-tests", "Lint (biome)"])
    def test_non_snake_case_separators_are_flagged(self, name):
        assert any("not snake_case" in w for w in lint_check_name(name))

    @pytest.mark.ci
    def test_more_than_two_words_is_flagged(self):
        warnings = lint_check_name("build_and_test_all")
        assert any("4 words, at most 2 allowed" in w for w in warnings)

    @pytest.mark.ci
    def test_two_words_is_allowed(self):
        assert not any("words, at most" in w for w in lint_check_name("api_snapshot"))

    @pytest.mark.ci
    def test_the_name_that_actually_drifted_is_flagged(self):
        """The regression this rule exists for — design-system's old required check."""
        warnings = lint_check_name("Build, Typecheck, Unit Tests, Storybook & Visual Regression")
        assert any("not all lowercase" in w for w in warnings)
        assert any("7 words" in w for w in warnings)

    @pytest.mark.ci
    def test_off_lexicon_but_well_shaped_name_warns_without_shape_complaints(self):
        warnings = lint_check_name("wibble")
        assert warnings == ["not in the preferred lexicon (allowed, but prefer a listed name)"]

    @pytest.mark.ci
    @pytest.mark.parametrize(
        ("name", "expected"),
        [
            ("Build", "build"),
            ("Lint & Typecheck", "lint"),
            ("Storybook & Visual Regression", "visual"),
            ("Tests & Coverage", "coverage"),
            ("PR Title", "pr_title"),
        ],
    )
    def test_suggestions_point_at_the_lexicon(self, name, expected):
        assert suggest_preferred_name(name) == expected

    @pytest.mark.ci
    def test_no_suggestion_for_an_unrelated_name(self):
        assert suggest_preferred_name("wibble") is None


class TestDiscoverPrCheckNames:
    """The check name GitHub reports is a job's `name:`, falling back to its job id."""

    @staticmethod
    def _write(tmp_path, filename, body):
        workflows = tmp_path / ".github" / "workflows"
        workflows.mkdir(parents=True, exist_ok=True)
        (workflows / filename).write_text(textwrap.dedent(body))
        return tmp_path

    @pytest.mark.ci
    def test_job_name_wins_and_job_id_is_the_fallback(self, tmp_path):
        repo = self._write(
            tmp_path,
            "ci.yml",
            """
            name: CI
            on:
              pull_request:
                branches: [main]
            jobs:
              verify:
                name: ci
                runs-on: ubuntu-latest
              unnamed:
                runs-on: ubuntu-latest
            """,
        )
        found = discover_pr_check_names(repo)
        assert set(found) == {"ci", "unnamed"}
        assert found["ci"] == "ci.yml :: verify"

    @pytest.mark.ci
    def test_on_is_read_despite_yaml_parsing_it_as_the_boolean_true(self, tmp_path):
        """`on:` is a YAML 1.1 boolean, so safe_load keys it as True, not "on"."""
        repo = self._write(
            tmp_path,
            "ci.yml",
            """
            on: [pull_request]
            jobs:
              verify:
                name: ci
            """,
        )
        assert "ci" in discover_pr_check_names(repo)

    @pytest.mark.ci
    def test_numeric_job_ids_do_not_hide_the_other_jobs(self, tmp_path):
        repo = self._write(
            tmp_path,
            "ci.yml",
            """
            on: [pull_request]
            jobs:
              1:
                runs-on: ubuntu-latest
              verify:
                name: ci
            """,
        )
        assert discover_pr_check_names(repo) == {"1": "ci.yml :: 1", "ci": "ci.yml :: verify"}

    @pytest.mark.ci
    def test_a_declared_status_context_counts_as_a_check(self, tmp_path):
        """A gate held at pending is a commit status, not a job; `STATUS_CONTEXT` names it."""
        repo = self._write(
            tmp_path,
            "regression.yml",
            """
            on:
              pull_request:
                types: [opened, synchronize, labeled]
            env:
              STATUS_CONTEXT: regression
            jobs:
              gate:
                name: gate
            """,
        )
        assert discover_pr_check_names(repo) == {
            "gate": "regression.yml :: gate",
            "regression": "regression.yml :: status",
        }

    @pytest.mark.ci
    def test_a_status_context_outside_a_pull_request_workflow_is_ignored(self, tmp_path):
        repo = self._write(
            tmp_path,
            "nightly.yml",
            """
            on: [push]
            env:
              STATUS_CONTEXT: regression
            jobs:
              run:
                name: run
            """,
        )
        assert discover_pr_check_names(repo) == {}

    @pytest.mark.ci
    def test_workflows_without_a_pull_request_trigger_are_ignored(self, tmp_path):
        """A job that never runs on a PR can never satisfy a required check on one."""
        repo = self._write(
            tmp_path,
            "publish.yml",
            """
            on:
              push:
                branches: [main]
            jobs:
              publish:
                name: publish
            """,
        )
        assert discover_pr_check_names(repo) == {}

    @pytest.mark.ci
    def test_unparseable_workflow_is_skipped_rather_than_raising(self, tmp_path):
        repo = self._write(tmp_path, "broken.yml", "this: [is: not: valid: yaml")
        self._write(
            repo,
            "ci.yml",
            """
            on: [pull_request]
            jobs:
              verify:
                name: ci
            """,
        )
        assert set(discover_pr_check_names(repo)) == {"ci"}

    @pytest.mark.ci
    def test_repo_without_a_workflows_directory_is_empty(self, tmp_path):
        assert discover_pr_check_names(tmp_path) == {}


class TestInspectCheckNames:
    """The drift half: a required check no PR job declares blocks every PR in that repo."""

    @staticmethod
    def _repo(tmp_path, *job_names):
        workflows = tmp_path / ".github" / "workflows"
        workflows.mkdir(parents=True, exist_ok=True)
        jobs = "".join(f"  job{i}:\n    name: {name}\n" for i, name in enumerate(job_names))
        (workflows / "ci.yml").write_text(f"on: [pull_request]\njobs:\n{jobs}")
        return tmp_path

    @pytest.mark.ci
    def test_declared_and_well_named_is_aligned(self, tmp_path):
        repo = self._repo(tmp_path, "ci")
        with _requiring("o/r", ["ci"]):
            res = inspect_check_names(repo, "o/r")
        assert res.is_aligned is True
        assert res.status == "aligned"
        assert res.missing == ()
        assert res.warnings == {}

    @pytest.mark.ci
    def test_a_required_check_no_job_declares_is_drift(self, tmp_path):
        """The exact design-system failure: the job was renamed, the map was not."""
        repo = self._repo(tmp_path, "Build, Typecheck, Unit Tests, Storybook & Visual Regression")
        with _requiring("o/r", ["Build, Typecheck, Storybook & Visual Regression"]):
            res = inspect_check_names(repo, "o/r")
        assert res.is_aligned is False
        assert res.status == "drift"
        assert res.missing == ("Build, Typecheck, Storybook & Visual Regression",)

    @pytest.mark.ci
    def test_a_badly_shaped_name_warns_but_stays_aligned(self, tmp_path):
        """Shape is advisory — most of these repos predate the rule."""
        repo = self._repo(tmp_path, "Lint & Typecheck")
        with _requiring("o/r", ["Lint & Typecheck"]):
            res = inspect_check_names(repo, "o/r")
        assert res.is_aligned is True
        assert res.status == "warn"
        assert "Lint & Typecheck" in res.warnings

    @pytest.mark.ci
    def test_no_pr_workflows_is_not_called_drift(self, tmp_path):
        with _requiring("o/r", ["ci"]):
            res = inspect_check_names(tmp_path, "o/r")
        assert res.status == "no_pr_workflows"
        assert res.is_aligned is True

    @pytest.mark.ci
    def test_an_unregistered_repo_has_no_borrowed_checks(self, tmp_path):
        """How Resultful and Parquet.TypeProvider went unnoticed: they matched no key at all."""
        repo = self._repo(tmp_path, "build", "pr-title")
        res = inspect_check_names(repo, "owner/never-registered")
        assert res.registered is False
        assert res.is_aligned is True
        assert res.required == ()
        assert res.missing == ()


class TestRequiredChecksConfigMatchesReality:
    """`required_checks` in config/repo_policies.json is a claim about what the workflows declare."""

    @staticmethod
    def _declared_lists() -> list[tuple[str, tuple[str, ...]]]:
        policy = load_policy_config().file
        layers = [("defaults", policy.defaults)]
        layers += [(f"profile {name}", profile) for name, profile in policy.profiles.items()]
        layers += [(f"override {name}", layer) for name, layer in policy.repo_overrides.items()]
        return [(where, layer.required_checks) for where, layer in layers if layer.required_checks is not None]

    @pytest.mark.ci
    def test_rig_requires_checks_its_own_workflows_declare(self):
        """This repo's ruleset is resolved from config, and every name is a real pull_request job."""
        required = registered_required_checks("rtkelly13/rig")
        declared = discover_pr_check_names(REPO_ROOT)
        assert required == ["ci"]
        assert set(required) <= set(declared), (required, sorted(declared))

    @pytest.mark.ci
    def test_design_system_requires_the_aggregate_and_the_regression_gate(self):
        assert registered_required_checks("rtkelly-labs/design-system") == ["ci", "regression"]

    @pytest.mark.ci
    def test_no_list_requires_the_same_check_twice(self):
        for where, checks in self._declared_lists():
            assert len(checks) == len(set(checks)), where

    @pytest.mark.ci
    def test_no_list_is_empty(self):
        """A ruleset with no required checks is a ruleset that gates nothing."""
        for where, checks in self._declared_lists():
            assert checks, where


class TestEnforceIsRulesetsOnly:
    """Required checks reach GitHub only through the ruleset, which only the check-name guard lets through."""

    @staticmethod
    def _enforce(tmp_path: pathlib.Path, job_name: str) -> list[list[str]]:
        workflows = tmp_path / ".github" / "workflows"
        workflows.mkdir(parents=True)
        (workflows / "ci.yml").write_text(f"on: [pull_request]\njobs:\n  verify:\n    name: {job_name}\n")
        cfg = PolicyConfig.parse({"repo_overrides": {"r": {"required_checks": ["ci"]}}})
        gh_calls: list[list[str]] = []

        def fake_gh(cmd: list[str], **_kwargs: object) -> subprocess.CompletedProcess[str]:
            gh_calls.append(cmd)
            return _done("[]")

        with (
            patch(f"{_CHECKS}.resolve_target_directories", return_value=[tmp_path]),
            patch(
                f"{_CHECKS}.inspect_repo_exists",
                return_value=RepoFound(status="EXISTS", message="", repo_name="rtkelly13/r"),
            ),
            patch("rig.repo_governance.github_rulesets.load_policy_config", return_value=cfg),
            patch("subprocess.run", side_effect=fake_gh),
            patch(f"{_CHECKS}.align_github_repo_settings"),
            patch(f"{_CHECKS}.align_npmrc"),
            patch(f"{_CHECKS}.align_agents_md"),
            patch(f"{_CHECKS}.align_gitignore"),
            patch(f"{_CHECKS}.align_todo_md"),
            patch(f"{_CHECKS}.align_git_hooks"),
            patch(f"{_CHECKS}.align_ls_lint"),
        ):
            CliRunner().invoke(app, ["enforce"])
        return gh_calls

    @pytest.mark.ci
    def test_a_declared_check_is_written_to_the_ruleset_and_nowhere_else(self, tmp_path):
        calls = self._enforce(tmp_path, "ci")
        assert ["gh", "api", "-X", "POST", "repos/rtkelly13/r/rulesets", "--input", "-"] in calls
        assert not [c for c in calls if any("/protection" in part for part in c)]

    @pytest.mark.ci
    def test_an_undeclared_check_reaches_github_by_no_path(self, tmp_path):
        calls = self._enforce(tmp_path, "something_else")
        assert not [c for c in calls if "-X" in c]


class TestEnforceRefusesStaleCheckNames:
    """`enforce` is the only path that writes a required check name into a live ruleset."""

    @pytest.mark.ci
    def test_ruleset_is_not_written_when_a_required_check_is_undeclared(self, tmp_path):
        workflows = tmp_path / ".github" / "workflows"
        workflows.mkdir(parents=True)
        (workflows / "ci.yml").write_text("on: [pull_request]\njobs:\n  verify:\n    name: something_else\n")

        with (
            patch(
                "rig.repo_governance.cli_repo_checks.resolve_target_directories",
                return_value=[tmp_path],
            ),
            patch(
                "rig.repo_governance.cli_repo_checks.inspect_repo_exists",
                return_value=RepoFound(status="EXISTS", message="", repo_name="rtkelly13/r"),
            ),
            _requiring("rtkelly13/r", ["ci"]),
            patch("rig.repo_governance.cli_repo_checks.align_github_ruleset") as mock_ruleset,
            patch("rig.repo_governance.cli_repo_checks.align_github_repo_settings"),
            patch("rig.repo_governance.cli_repo_checks.align_npmrc"),
            patch("rig.repo_governance.cli_repo_checks.align_agents_md"),
            patch("rig.repo_governance.cli_repo_checks.align_gitignore"),
            patch("rig.repo_governance.cli_repo_checks.align_todo_md"),
            patch("rig.repo_governance.cli_repo_checks.align_git_hooks"),
        ):
            result = CliRunner().invoke(app, ["enforce"])

        mock_ruleset.assert_not_called()
        assert "refusing to set required checks" in result.output

    @pytest.mark.ci
    def test_ruleset_is_not_written_when_no_pull_request_workflow_exists(self, tmp_path):

        with (
            patch(
                "rig.repo_governance.cli_repo_checks.resolve_target_directories",
                return_value=[tmp_path],
            ),
            patch(
                "rig.repo_governance.cli_repo_checks.inspect_repo_exists",
                return_value=RepoFound(status="EXISTS", message="", repo_name="rtkelly13/r"),
            ),
            _requiring("rtkelly13/r", ["ci"]),
            patch("rig.repo_governance.cli_repo_checks.align_github_ruleset") as mock_ruleset,
            patch("rig.repo_governance.cli_repo_checks.align_github_repo_settings"),
            patch("rig.repo_governance.cli_repo_checks.align_npmrc"),
            patch("rig.repo_governance.cli_repo_checks.align_agents_md"),
            patch("rig.repo_governance.cli_repo_checks.align_gitignore"),
            patch("rig.repo_governance.cli_repo_checks.align_todo_md"),
            patch("rig.repo_governance.cli_repo_checks.align_git_hooks"),
        ):
            result = CliRunner().invoke(app, ["enforce"])

        mock_ruleset.assert_not_called()
        output = " ".join(result.output.split())
        assert "refusing to set required checks" in output
        assert "no pull_request workflow" in output

    @pytest.mark.ci
    def test_ruleset_is_written_when_the_required_check_is_declared(self, tmp_path):
        workflows = tmp_path / ".github" / "workflows"
        workflows.mkdir(parents=True)
        (workflows / "ci.yml").write_text("on: [pull_request]\njobs:\n  verify:\n    name: ci\n")

        with (
            patch(
                "rig.repo_governance.cli_repo_checks.resolve_target_directories",
                return_value=[tmp_path],
            ),
            patch(
                "rig.repo_governance.cli_repo_checks.inspect_repo_exists",
                return_value=RepoFound(status="EXISTS", message="", repo_name="rtkelly13/r"),
            ),
            _requiring("rtkelly13/r", ["ci"]),
            patch(
                "rig.repo_governance.cli_repo_checks.align_github_ruleset",
                return_value=AlignResult(status="created"),
            ) as mock_ruleset,
            patch("rig.repo_governance.cli_repo_checks.align_github_repo_settings"),
            patch("rig.repo_governance.cli_repo_checks.align_npmrc"),
            patch("rig.repo_governance.cli_repo_checks.align_agents_md"),
            patch("rig.repo_governance.cli_repo_checks.align_gitignore"),
            patch("rig.repo_governance.cli_repo_checks.align_todo_md"),
            patch("rig.repo_governance.cli_repo_checks.align_git_hooks"),
        ):
            CliRunner().invoke(app, ["enforce"])

        mock_ruleset.assert_called_once()


class TestRulesetPayloadDeclaresEveryPullRequestParameter:
    """GitHub defaults any omitted rule parameter, so an undeclared one is reset on every enforce."""

    @staticmethod
    def _pr_params(repo_name="rtkelly13/design-system"):
        payload = build_ruleset_payload(repo_name)
        rule = next(r for r in payload["rules"] if r["type"] == "pull_request")
        return rule["parameters"]

    @pytest.mark.ci
    def test_extra_approval_for_unattributed_changes_is_declared_false(self):
        """Left undeclared, this made design-system#69 unmergeable by anyone with all checks green.

        `Co-Authored-By` names a non-GitHub address, so the commit reads as having an unattributed
        co-author and demands an extra approving review that no one can supply — the review count
        is 0, the author cannot self-approve, and there are no bypass actors.
        """
        params = self._pr_params()
        assert "require_extra_approval_for_unattributed_changes" in params
        assert params["require_extra_approval_for_unattributed_changes"] is False

    @pytest.mark.ci
    def test_no_pull_request_parameter_is_left_to_a_github_default(self):
        """A guard against re-introducing the same class of bug with a different parameter."""
        expected = {
            "required_approving_review_count",
            "dismiss_stale_reviews_on_push",
            "require_code_owner_review",
            "require_last_push_approval",
            "required_review_thread_resolution",
            "require_extra_approval_for_unattributed_changes",
        }
        assert set(self._pr_params()) == expected

    @pytest.mark.ci
    def test_required_checks_in_the_payload_come_from_the_config(self):
        payload = build_ruleset_payload("rtkelly13/design-system")
        rule = next(r for r in payload["rules"] if r["type"] == "required_status_checks")
        assert rule["parameters"]["required_status_checks"] == [
            {"context": c} for c in registered_required_checks("rtkelly13/design-system")
        ]


class TestRulesetForManagedForks:
    """A fork's default branch is rebuilt by its stack tool, so the ruleset must allow that push."""

    FORK = "rtkelly-labs/parquet-dotnet"

    @staticmethod
    def _rule_types(repo_name: str) -> list[str]:
        return [r["type"] for r in build_ruleset_payload(repo_name)["rules"]]

    @pytest.mark.ci
    def test_a_fork_ruleset_covers_its_own_default_branch(self):
        payload = build_ruleset_payload(self.FORK)
        assert payload["conditions"]["ref_name"]["include"] == ["refs/heads/master"]

    @pytest.mark.ci
    def test_a_fork_allows_rebaseline_pushes_but_keeps_history_and_checks(self):
        types = self._rule_types(self.FORK)
        assert "non_fast_forward" not in types
        assert "pull_request" not in types
        assert {"deletion", "required_linear_history", "required_status_checks"} <= set(types)

    @pytest.mark.ci
    def test_an_owned_repo_keeps_the_full_ruleset_on_main(self):
        payload = build_ruleset_payload("rtkelly13/design-system")
        assert payload["conditions"]["ref_name"]["include"] == ["refs/heads/main"]
        assert {"non_fast_forward", "pull_request"} <= set(self._rule_types("rtkelly13/design-system"))

    @pytest.mark.ci
    def test_editing_one_payload_does_not_change_the_next(self):
        first = build_ruleset_payload("rtkelly13/design-system")
        rule = next(r for r in first["rules"] if r["type"] == "pull_request")
        rule["parameters"]["allowed_merge_methods"] = ["squash"]
        second = build_ruleset_payload("rtkelly13/design-system")
        again = next(r for r in second["rules"] if r["type"] == "pull_request")
        assert "allowed_merge_methods" not in again["parameters"]


class TestRulesetDrift:
    """An active ruleset is compared with the declared one, not assumed current because it exists."""

    REPO = "rtkelly-labs/design-system"

    def _live(self, drop: str | None = None, extra_approval: bool = False) -> str:
        payload = build_ruleset_payload(self.REPO)
        rules = [r for r in payload["rules"] if r["type"] != drop]
        for r in rules:
            if r["type"] == "pull_request":
                r["parameters"]["require_extra_approval_for_unattributed_changes"] = extra_approval
                # GitHub returns parameters the payload never sets; they must not read as drift.
                r["parameters"]["allowed_merge_methods"] = ["squash"]
            if r["type"] == "required_status_checks":
                for check in r["parameters"]["required_status_checks"]:
                    check["integration_id"] = 15368
        payload["rules"] = rules
        return json.dumps(payload)

    def _github(self, detail: str) -> FakeGitHubApi:
        return FakeGitHubApi(
            {
                f"repos/{self.REPO}/rulesets": [{"id": 7, "name": RULESET_NAME, "enforcement": "active"}],
                f"repos/{self.REPO}/rulesets/7": load_json(detail),
            }
        )

    @pytest.mark.ci
    def test_a_ruleset_matching_the_declaration_has_no_drift(self):
        assert ruleset_drift(self.REPO, self._live()) == []

    @pytest.mark.ci
    def test_a_missing_rule_and_a_changed_parameter_are_both_drift(self):
        drift = ruleset_drift(self.REPO, self._live(drop="required_status_checks", extra_approval=True))
        assert "missing rule required_status_checks" in drift
        assert any(line.startswith("pull_request:") for line in drift)

    @pytest.mark.ci
    def test_align_updates_an_active_ruleset_that_drifted(self):
        api = self._github(self._live(drop="required_linear_history"))
        res = align_github_ruleset(self.REPO, dry_run=True, api=api)
        assert res.status == "dry_run"
        assert res.action == "PUT"
        assert res.drift == ("missing rule required_linear_history",)

    @pytest.mark.ci
    def test_align_leaves_an_active_ruleset_that_matches(self):
        api = self._github(self._live())
        res = align_github_ruleset(self.REPO, api=api)
        assert res.status == "already_active"
        assert api.writes == []


# ---------------------------------------------------------------------------
# align_agents_md — append only when genuinely absent
# ---------------------------------------------------------------------------


def test_align_agents_md_creates_a_missing_file(tmp_path: pathlib.Path):
    res = align_agents_md(tmp_path)
    assert res.status == "created"
    assert AGENTS_CONVENTIONS_HEADING in (tmp_path / "AGENTS.md").read_text(encoding="utf-8")


def test_align_agents_md_appends_when_the_section_is_absent(tmp_path: pathlib.Path):
    (tmp_path / "AGENTS.md").write_text("# thing\n\nJust a description.\n", encoding="utf-8")
    res = align_agents_md(tmp_path)
    assert res.status == "appended"
    assert (tmp_path / "AGENTS.md").read_text(encoding="utf-8").count("Repository Conventions") == 1


def test_align_agents_md_leaves_a_divergent_section_alone(tmp_path: pathlib.Path):
    """The regression. A rewritten section must not have the expected block appended.

    Previously the condition also required the string `TODO.md`, so a repo that
    reworded rule 6 looked *absent* rather than *divergent* and ended up with two
    contradictory conventions sections.
    """
    own = textwrap.dedent(
        """\
        # thing

        A description.

        ## 🛑 Repository Conventions & Workflow Policy

        1. **Squash Merge Only**: yes.
        2. **Our Own Rule**: something this repo decided.
        """
    )
    agents = tmp_path / "AGENTS.md"
    agents.write_text(own, encoding="utf-8")

    res = align_agents_md(tmp_path)

    assert res.status == "divergent"
    after = agents.read_text(encoding="utf-8")
    assert after == own, "a divergent section must be left byte-identical"
    assert after.count("Repository Conventions & Workflow Policy") == 1


def test_align_agents_md_is_idempotent_on_the_expected_block(tmp_path: pathlib.Path):
    (tmp_path / "AGENTS.md").write_text(f"# thing\n\nA description.\n\n{EXPECTED_AGENTS_CONVENTIONS}", encoding="utf-8")
    assert align_agents_md(tmp_path).status == "already_configured"


def test_align_agents_md_dry_run_writes_nothing(tmp_path: pathlib.Path):
    agents = tmp_path / "AGENTS.md"
    agents.write_text("# thing\n\nA description.\n", encoding="utf-8")
    res = align_agents_md(tmp_path, dry_run=True)
    assert res.status == "appended"
    assert "Repository Conventions" not in agents.read_text(encoding="utf-8")


def _policy_in_cwd(tmp_path: pathlib.Path, monkeypatch: pytest.MonkeyPatch, text: str) -> None:
    """`load_policy_config` reads `config/repo_policies.json` under the working directory first."""
    (tmp_path / "config").mkdir()
    (tmp_path / "config" / "repo_policies.json").write_text(text, encoding="utf-8")
    monkeypatch.chdir(tmp_path)


def test_sync_policies_rejects_a_misshapen_config(tmp_path: pathlib.Path, monkeypatch: pytest.MonkeyPatch):
    _policy_in_cwd(tmp_path, monkeypatch, json.dumps({"profiles": ["not", "a", "mapping"]}))
    result = runner.invoke(app, ["sync-policies"])
    assert result.exit_code == 1
    assert "Invalid policy config" in result.output


def test_sync_policies_walks_declared_profiles(tmp_path: pathlib.Path, monkeypatch: pytest.MonkeyPatch):
    policy = {"profiles": {"public_open_source": {"repos": ["absent-repo"], "policies": ["ai_policy"]}}}
    _policy_in_cwd(tmp_path, monkeypatch, json.dumps(policy))
    result = runner.invoke(app, ["sync-policies", "--dry-run"])
    assert result.exit_code == 0, result.output
    assert "Skipping absent-repo" in result.output


def test_verify_pr_checks_always_targets_the_resolved_repo():
    with (
        patch("rig.repo_governance.cli_triage.get_gh_repo_name", return_value=None),
        patch("subprocess.run") as mock_run,
    ):
        mock_run.return_value.returncode = 0
        mock_run.return_value.stdout = "[]"
        runner.invoke(app, ["verify-pr-checks", "123"])
    checks_calls = [c for c in mock_run.call_args_list if "checks" in c.args[0]]
    assert len(checks_calls) == 1
    cmd = checks_calls[0].args[0]
    assert cmd[cmd.index("--repo") + 1] == "rtkelly13/shared-utilities"


def test_audit_with_no_repositories_fails():
    """`--org rtkelly-labs` with no clones once printed "All 0 repositories are fully compliant"."""
    with patch("rig.repo_governance.cli_repo_checks.resolve_target_directories", return_value=[]):
        result = runner.invoke(app, ["audit", "--org", "rtkelly-labs"])
    assert result.exit_code == 1
    assert "no repositories found" in result.output
