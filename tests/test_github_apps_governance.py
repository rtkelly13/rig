from __future__ import annotations

import pathlib
from unittest.mock import patch

import pytest
from typer.testing import CliRunner

from rig.repo_governance.align_repo_conventions import app
from rig.repo_governance.github_apps_manager import (
    Installation,
    Outcome,
    SyncResult,
    audit_account_apps,
    sync_account_app_repos,
)
from rig.repo_governance.policy_config import PolicyConfig, load_policy_config
from rig.repo_governance.review_tool_configs import (
    ConfigChange,
    align_coderabbit_config,
    align_review_configs,
    inspect_coderabbit_config,
    inspect_review_configs,
)

runner = CliRunner()


@pytest.fixture(autouse=True)
def _no_github_enumeration():
    with patch("rig.repo_governance.repo_discovery.discover_org_repos", return_value=[]):
        yield


@pytest.fixture
def apps_policy() -> PolicyConfig:
    return PolicyConfig.parse(
        {
            "defaults": {},
            "profiles": {},
            "github_apps": {
                "chatgpt-codex-connector": {
                    "name": "ChatGPT / Codex in GitHub",
                    "slug": "chatgpt-codex-connector",
                    "install_url": "https://github.com/apps/chatgpt-codex-connector/installations/new",
                    "accounts": {
                        "rtkelly-labs": {
                            "required": True,
                            "repos": ["design-system", "Parquet.SourceGenerator"],
                        }
                    },
                },
                "coderabbitai": {
                    "name": "CodeRabbit AI Reviewer",
                    "slug": "coderabbitai",
                    "install_url": "https://github.com/apps/coderabbitai/installations/new",
                    "config_template": ".coderabbit.yaml",
                    "accounts": {
                        "rtkelly-labs": {
                            "required": False,
                            "repos": ["design-system"],
                        }
                    },
                },
            },
        }
    )


@pytest.mark.ci
def test_policy_config_github_apps(apps_policy: PolicyConfig):
    apps = apps_policy.github_apps()
    assert len(apps) == 2
    assert "chatgpt-codex-connector" in apps
    assert "coderabbitai" in apps

    org_apps = apps_policy.apps_for_account("rtkelly-labs")
    assert len(org_apps) == 2

    # apps_for_repo
    ds_apps = apps_policy.apps_for_repo("design-system", "rtkelly-labs")
    assert len(ds_apps) == 2

    psg_apps = apps_policy.apps_for_repo("Parquet.SourceGenerator", "rtkelly-labs")
    assert len(psg_apps) == 1
    assert psg_apps[0].slug == "chatgpt-codex-connector"


@pytest.mark.ci
def test_shipped_config_github_apps():
    cfg = load_policy_config()
    apps = cfg.github_apps()
    assert "chatgpt-codex-connector" in apps
    assert "coderabbitai" in apps
    assert "renovate" in apps
    assert "codecov" in apps
    assert "socket-security" in apps
    assert "deepsource-io" in apps

    renovate = apps["renovate"]
    assert renovate.slug == "renovate"
    assert renovate.name == "Renovate"
    assert renovate.install_url == "https://github.com/apps/renovate/installations/new"
    assert "rtkelly-labs" in renovate.accounts
    assert renovate.accounts["rtkelly-labs"].required is True
    assert renovate.accounts["rtkelly-labs"].all_repos is True
    assert ".github" in renovate.accounts["rtkelly-labs"].disallow_repos
    assert "rtkelly13" in renovate.accounts
    assert renovate.accounts["rtkelly13"].required is False
    assert renovate.accounts["rtkelly13"].all_repos is True
    assert "obsidian-vault" in renovate.accounts["rtkelly13"].disallow_repos

    codex = apps["chatgpt-codex-connector"]
    assert "rtkelly-labs" in codex.accounts
    assert codex.accounts["rtkelly-labs"].required is True
    assert codex.accounts["rtkelly-labs"].all_repos is True
    assert ".github" in codex.accounts["rtkelly-labs"].disallow_repos

    deepsource = apps["deepsource-io"]
    assert deepsource.slug == "deepsource-io"
    assert "rtkelly-labs" in deepsource.accounts
    assert deepsource.accounts["rtkelly-labs"].required is True

    rabbit_user = apps["coderabbitai"].accounts["rtkelly13"]
    assert rabbit_user.all_repos is True
    assert "obsidian-vault" in rabbit_user.disallow_repos

    # apps_for_repo should exclude obsidian-vault from all review apps
    vault_apps = cfg.apps_for_repo("obsidian-vault", "rtkelly13")
    assert len(vault_apps) == 0

    # apps_for_repo should include blog for review apps, Renovate, and Vercel, which deploys it
    blog_apps = cfg.apps_for_repo("blog", "rtkelly13")
    assert len(blog_apps) == 7


@pytest.mark.ci
def test_audit_account_apps_missing(apps_policy: PolicyConfig):
    with patch("rig.repo_governance.github_apps_manager.fetch_org_installations", return_value=[]):
        results = audit_account_apps("rtkelly-labs", config=apps_policy)
        assert len(results) == 2

        codex_res = next(r for r in results if r.app_slug == "chatgpt-codex-connector")
        assert codex_res.installed is False
        assert codex_res.is_aligned is False  # required=True
        assert codex_res.instructions is not None
        assert "https://github.com/apps/chatgpt-codex-connector/installations/new" in codex_res.instructions

        rabbit_res = next(r for r in results if r.app_slug == "coderabbitai")
        assert rabbit_res.installed is False
        assert rabbit_res.is_aligned is True  # required=False (advisory only)


@pytest.mark.ci
def test_audit_account_apps_installed_selected(apps_policy: PolicyConfig):
    mock_installs = [Installation(id=12345, app_slug="chatgpt-codex-connector", repository_selection="selected")]
    with (
        patch(
            "rig.repo_governance.github_apps_manager.fetch_org_installations",
            return_value=mock_installs,
        ),
        patch(
            "rig.repo_governance.github_apps_manager.fetch_installation_repos",
            return_value=["design-system"],
        ),
    ):
        results = audit_account_apps("rtkelly-labs", config=apps_policy)
        codex_res = next(r for r in results if r.app_slug == "chatgpt-codex-connector")
        assert codex_res.installed is True
        assert codex_res.is_aligned is False
        assert codex_res.missing_repos == ("Parquet.SourceGenerator",)


@pytest.mark.ci
def test_sync_account_app_repos_adds_missing(apps_policy: PolicyConfig):
    mock_installs = [Installation(id=12345, app_slug="chatgpt-codex-connector", repository_selection="selected")]
    with (
        patch(
            "rig.repo_governance.github_apps_manager.fetch_org_installations",
            return_value=mock_installs,
        ),
        patch(
            "rig.repo_governance.github_apps_manager.fetch_installation_repos",
            return_value=["design-system"],
        ),
        patch("rig.repo_governance.github_apps_manager.get_repo_id", return_value=987654),
        patch(
            "rig.repo_governance.github_apps_manager.add_repo_to_installation",
            return_value=Outcome(True, "added"),
        ),
    ):
        sync = sync_account_app_repos("rtkelly-labs", "chatgpt-codex-connector", config=apps_policy, dry_run=False)
        assert sync.ok is True
        assert any("Parquet.SourceGenerator: added" in m for m in sync.messages)


@pytest.mark.ci
def test_coderabbit_config_inspection_and_align(tmp_path: pathlib.Path):
    res = inspect_coderabbit_config(tmp_path)
    assert res.is_aligned is False

    # Dry run
    assert align_coderabbit_config(tmp_path, dry_run=True) == ConfigChange(
        True, "dry-run: would create .coderabbit.yaml"
    )
    assert not (tmp_path / ".coderabbit.yaml").exists()

    # Apply
    assert align_coderabbit_config(tmp_path, dry_run=False) == ConfigChange(True, "created .coderabbit.yaml")
    cfg_file = tmp_path / ".coderabbit.yaml"
    assert cfg_file.exists()
    assert "CodeRabbit Configuration" in cfg_file.read_text(encoding="utf-8")

    # Second run is idempotent
    assert align_coderabbit_config(tmp_path, dry_run=False) == ConfigChange(False, "already configured")


@pytest.mark.ci
def test_cli_apps_command(apps_policy: PolicyConfig):
    mock_installs = [
        Installation(id=12345, app_slug="chatgpt-codex-connector", repository_selection="all"),
        Installation(id=67890, app_slug="coderabbitai", repository_selection="all"),
    ]
    with (
        patch("rig.repo_governance.github_apps_manager.load_policy_config", return_value=apps_policy),
        patch(
            "rig.repo_governance.github_apps_manager.fetch_org_installations",
            return_value=mock_installs,
        ),
    ):
        res = runner.invoke(app, ["apps", "--org", "rtkelly-labs"])
        assert res.exit_code == 0
        assert "ChatGPT" in res.stdout
        assert "Codex" in res.stdout
        assert "CodeRabbit" in res.stdout
        assert "SUCCESS: all declared GitHub Apps conform to policy." in res.stdout


@pytest.mark.ci
def test_review_configs_inspection_and_alignment(tmp_path: pathlib.Path):
    # Initially missing
    status = inspect_review_configs(tmp_path)
    assert status.coderabbit.is_aligned is False
    assert status.codecov.is_aligned is False
    assert status.deepsource.is_aligned is False
    # tmp_path is not a .NET repo, so qodana is optional
    assert status.qodana.applicable is False

    # Dry-run alignment
    results = align_review_configs(tmp_path, org="rtkelly-labs", dry_run=True)
    assert len(results) == 4
    assert not (tmp_path / ".coderabbit.yaml").exists()
    assert not (tmp_path / "codecov.yml").exists()
    assert not (tmp_path / ".deepsource.toml").exists()

    # Enforce alignment on non-.NET repo
    results = align_review_configs(tmp_path, org="rtkelly-labs", dry_run=False)
    assert len(results) == 4
    assert (tmp_path / ".coderabbit.yaml").is_file()
    assert (tmp_path / "codecov.yml").is_file()
    assert (tmp_path / ".deepsource.toml").is_file()

    # Inspect again - now aligned
    status_after = inspect_review_configs(tmp_path)
    assert status_after.coderabbit.is_aligned is True
    assert status_after.codecov.is_aligned is True
    assert status_after.deepsource.is_aligned is True

    # Test .NET repo Qodana alignment
    dotnet_repo = tmp_path / "dotnet_repo"
    dotnet_repo.mkdir()
    (dotnet_repo / "App.sln").write_text("", encoding="utf-8")

    status_dotnet = inspect_review_configs(dotnet_repo)
    assert status_dotnet.qodana.applicable is True
    assert status_dotnet.qodana.is_aligned is False

    dotnet_results = align_review_configs(dotnet_repo, org="rtkelly-labs", dry_run=False)
    assert any("Qodana" in r.tool and r.changed is True for r in dotnet_results)
    assert (dotnet_repo / "qodana.yaml").is_file()
    assert (dotnet_repo / ".github" / "workflows" / "qodana.yml").is_file()

    status_dotnet_after = inspect_review_configs(dotnet_repo)
    assert status_dotnet_after.qodana.is_aligned is True


@pytest.mark.ci
def test_align_review_configs_disallowed_repo(tmp_path: pathlib.Path):
    # Create mock obsidian-vault
    vault_dir = tmp_path / "obsidian-vault"
    vault_dir.mkdir()

    results = align_review_configs(vault_dir, org="rtkelly13", dry_run=False)

    # CodeRabbit should have deployed the suppressor (auto_review: false)
    rabbit_result = next(r for r in results if "CodeRabbit" in r.tool)
    assert rabbit_result.changed is True
    assert "configured suppressor" in rabbit_result.message

    suppressor_file = vault_dir / ".coderabbit.yaml"
    assert suppressor_file.exists()
    assert "enabled: false" in suppressor_file.read_text(encoding="utf-8")

    # Codecov, DeepSource, Qodana should be skipped
    codecov_result = next(r for r in results if "Codecov" in r.tool)
    assert codecov_result.changed is False
    assert "disallowed by policy" in codecov_result.message
    assert not (vault_dir / "codecov.yml").exists()

    deepsource_result = next(r for r in results if "DeepSource" in r.tool)
    assert deepsource_result.changed is False
    assert "disallowed by policy" in deepsource_result.message
    assert not (vault_dir / ".deepsource.toml").exists()


def _fake_gh(responses: dict[str, object]):
    import json
    import subprocess

    def run(cmd, **kwargs):
        key = cmd[2]
        value = responses[key]
        if isinstance(value, Exception):
            raise value
        if isinstance(value, subprocess.CompletedProcess):
            return value
        return subprocess.CompletedProcess(cmd, 0, stdout=json.dumps(value), stderr="")

    return run


@pytest.mark.ci
def test_fetch_installations_uses_user_endpoint_for_personal_account():
    from rig.repo_governance.github_apps_manager import fetch_org_installations

    responses = {
        "users/rtkelly13": {"login": "rtkelly13", "type": "User"},
        "user/installations": [
            {
                "installations": [
                    {"id": 1, "app_slug": "a", "account": {"login": "rtkelly13", "type": "User"}},
                    {"id": 2, "app_slug": "b", "account": {"login": "someone-else", "type": "User"}},
                ]
            }
        ],
    }
    with patch("subprocess.run", side_effect=_fake_gh(responses)):
        result = fetch_org_installations("rtkelly13")
    assert [i.id for i in result] == [1]


@pytest.mark.ci
def test_fetch_installations_reports_lookup_failure():
    import subprocess

    from rig.repo_governance.github_apps_manager import GitHubLookupError, fetch_org_installations

    err = subprocess.CompletedProcess(["gh"], 1, "", "HTTP 404")
    with (
        patch("subprocess.run", side_effect=_fake_gh({"users/x": err})),
        pytest.raises(GitHubLookupError, match="HTTP 404"),
    ):
        fetch_org_installations("x")


@pytest.mark.ci
def test_apps_command_exits_nonzero_on_lookup_failure():
    from rig.repo_governance.github_apps_manager import GitHubLookupError

    with patch(
        "rig.repo_governance.github_apps_manager.fetch_org_installations",
        side_effect=GitHubLookupError("boom"),
    ):
        res = runner.invoke(app, ["apps", "--org", "rtkelly-labs"])
    assert res.exit_code == 1
    assert "lookup failed" in res.stdout


@pytest.mark.ci
def test_apps_enforce_fails_when_sync_fails(tmp_path: pathlib.Path):
    inst = [Installation(id=1, app_slug="chatgpt-codex-connector", repository_selection="selected")]
    base = "rig.repo_governance.github_apps_manager"
    with (
        patch(f"{base}.fetch_org_installations", return_value=inst),
        patch(f"{base}.fetch_installation_repos", return_value=[]),
        patch(f"{base}.sync_account_app_repos", return_value=SyncResult(False, ("Could not resolve repository ID",))),
    ):
        res = runner.invoke(app, ["apps", "--org", "rtkelly-labs", "--enforce", "--dry-run", str(tmp_path)])
    assert res.exit_code == 1
    assert "sync(s) failed" in res.stdout


@pytest.mark.ci
def test_qodana_workflow_uses_supported_input_and_requires_workflow(tmp_path: pathlib.Path):
    from rig.repo_governance.review_tool_configs import DEFAULT_QODANA_WORKFLOW_DOTNET

    assert "linter:" not in DEFAULT_QODANA_WORKFLOW_DOTNET
    assert "args: --linter" in DEFAULT_QODANA_WORKFLOW_DOTNET

    (tmp_path / "src" / "deep" / "er").mkdir(parents=True)
    (tmp_path / "src" / "deep" / "er" / "X.csproj").write_text("<Project/>")
    (tmp_path / "qodana.yaml").write_text("version: '1.0'\n")
    from rig.repo_governance.review_tool_configs import inspect_qodana_config

    assert inspect_qodana_config(tmp_path).is_aligned is False
    wf = tmp_path / ".github" / "workflows"
    wf.mkdir(parents=True)
    (wf / "qodana.yml").write_text("name: q\n")
    assert inspect_qodana_config(tmp_path).is_aligned is True


@pytest.mark.ci
def test_disallowed_coderabbit_preserves_existing_config(tmp_path: pathlib.Path):
    repo = tmp_path / "obsidian-vault"
    repo.mkdir()
    (repo / ".coderabbit.yml").write_text("reviews:\n  auto_review:\n    enabled: true\n")
    results = align_review_configs(repo, org="rtkelly13")
    (cr, *_) = results
    assert cr.changed is False and "CONFLICT" in cr.message
    assert "enabled: true" in (repo / ".coderabbit.yml").read_text()
    assert not (repo / ".coderabbit.yaml").exists()


@pytest.mark.ci
def test_all_repos_policy_enumerates_github_repos_then_applies_disallow():
    cfg = PolicyConfig.parse(
        {
            "defaults": {},
            "profiles": {},
            "github_apps": {
                "coderabbitai": {
                    "name": "CodeRabbit",
                    "slug": "coderabbitai",
                    "install_url": "https://x",
                    "accounts": {"acct": {"required": True, "all_repos": True, "disallow_repos": ["skip"]}},
                }
            },
        }
    )
    inst = [Installation(id=1, app_slug="coderabbitai", repository_selection="selected")]
    base = "rig.repo_governance.github_apps_manager"
    with (
        patch(f"{base}.fetch_org_installations", return_value=inst),
        patch(f"{base}.fetch_installation_repos", return_value=[]),
        patch(
            "rig.repo_governance.repo_discovery.discover_org_repos",
            return_value=["acct/one", "acct/skip", "other/x"],
        ),
    ):
        (res,) = audit_account_apps("acct", config=cfg)
    assert res.expected_repos == ("one",)


_ACME = {"login": "acme", "type": "Organization"}


@pytest.mark.ci
@pytest.mark.parametrize(
    ("account", "pages"),
    [
        pytest.param(_ACME, [{"installations": [{"app_slug": "a"}]}], id="installation-without-id"),
        pytest.param(_ACME, [{}], id="page-without-installations"),
        pytest.param({"login": "acme"}, [{"installations": []}], id="account-without-type"),
    ],
)
def test_fetch_installations_rejects_a_misshapen_answer(account: object, pages: object):
    """A misshapen answer is a lookup failure, never a silently shorter list or a wrong endpoint."""
    from rig.repo_governance.github_apps_manager import GitHubLookupError, fetch_org_installations

    responses = {"users/acme": account, "orgs/acme/installations": pages, "user/installations": pages}
    with patch("subprocess.run", side_effect=_fake_gh(responses)), pytest.raises(GitHubLookupError, match="unexpected"):
        fetch_org_installations("acme")


@pytest.mark.ci
def test_installation_without_a_selection_counts_as_selected():
    assert Installation(id=1, repository_selection=None).selection == "selected"


@pytest.mark.ci
@pytest.mark.parametrize(
    ("text", "disabled"),
    [
        ("reviews:\n  auto_review:\n    enabled: false\n", True),
        ("reviews:\n  auto_review:\n    enabled: 'false'\n", False),
        ("reviews: nope\n", False),
        ("- a list\n", False),
        ("", False),
    ],
)
def test_suppressor_detection_needs_a_literal_false(tmp_path: pathlib.Path, text: str, disabled: bool):
    from rig.repo_governance.review_tool_configs import _coderabbit_auto_review_disabled

    config = tmp_path / ".coderabbit.yaml"
    config.write_text(text, encoding="utf-8")
    assert _coderabbit_auto_review_disabled(config) is disabled
