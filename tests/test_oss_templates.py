"""Tests for open source community templates synchronization engine."""

from pathlib import Path

from rig.repo_governance.oss_templates import (
    TEMPLATE_MAPPINGS,
    inspect_oss_templates,
    sync_oss_templates,
)


def test_inspect_empty_repo(tmp_path: Path):
    res = inspect_oss_templates(tmp_path)
    assert not res.is_fully_aligned
    assert len(res.missing) == len(TEMPLATE_MAPPINGS)
    assert len(res.aligned) == 0


def test_sync_oss_templates_dry_run(tmp_path: Path):
    synced = sync_oss_templates(tmp_path, dry_run=True)
    assert synced.changed
    assert len(synced.messages) == len(TEMPLATE_MAPPINGS)
    assert all("[Dry Run]" in m for m in synced.messages)
    assert not (tmp_path / "CONTRIBUTING.md").exists()
    assert not (tmp_path / ".github" / "PULL_REQUEST_TEMPLATE.md").exists()


def test_sync_oss_templates_apply_and_inspect(tmp_path: Path):
    # 1. Apply templates
    synced = sync_oss_templates(
        tmp_path,
        context={"repo_name": "test-repo", "repo_full_name": "test-owner/test-repo"},
        dry_run=False,
    )
    assert synced.changed
    assert len(synced.messages) == len(TEMPLATE_MAPPINGS)

    # 2. Verify all files exist on disk
    for target_rel in TEMPLATE_MAPPINGS.values():
        path = tmp_path / target_rel
        assert path.exists(), f"Expected {target_rel} to exist"
        assert path.stat().st_size > 0

    # 3. Verify content was rendered with context
    contributing_text = (tmp_path / "CONTRIBUTING.md").read_text(encoding="utf-8")
    assert "test-repo" in contributing_text

    code_of_conduct = (tmp_path / "CODE_OF_CONDUCT.md").read_text(encoding="utf-8")
    assert "Contributor Covenant" in code_of_conduct

    bug_report = (tmp_path / ".github" / "ISSUE_TEMPLATE" / "bug_report.yml").read_text(encoding="utf-8")
    assert "Bug Report" in bug_report
    assert "reproduction" in bug_report

    # 4. Inspect populated directory
    audit_res = inspect_oss_templates(tmp_path)
    assert audit_res.is_fully_aligned
    assert len(audit_res.missing) == 0
    assert len(audit_res.aligned) == len(TEMPLATE_MAPPINGS)

    # 5. Re-sync is idempotent
    resynced = sync_oss_templates(
        tmp_path,
        context={"repo_name": "test-repo", "repo_full_name": "test-owner/test-repo"},
        dry_run=False,
    )
    assert not resynced.changed
    assert len(resynced.messages) == 0


def test_sync_oss_templates_language_rendering(tmp_path: Path):
    dotnet_dir = tmp_path / "dotnet_repo"
    dotnet_dir.mkdir()
    synced = sync_oss_templates(
        dotnet_dir,
        context={
            "repo_name": "MyDotNetLib",
            "repo_full_name": "rtkelly-labs/MyDotNetLib",
            "languages": ("dotnet",),
            "primary_language": "dotnet",
            "required_checks": ["build", "pr-title"],
            "discussions_enabled": False,
        },
        dry_run=False,
    )
    assert synced.changed

    contrib = (dotnet_dir / "CONTRIBUTING.md").read_text(encoding="utf-8")
    assert "dotnet build --configuration Release" in contrib
    assert "uv run ruff" not in contrib

    bug = (dotnet_dir / ".github" / "ISSUE_TEMPLATE" / "bug_report.yml").read_text(encoding="utf-8")
    assert ".NET SDK version" in bug
    assert "Python 3.13" not in bug

    config_yml = (dotnet_dir / ".github" / "ISSUE_TEMPLATE" / "config.yml").read_text(encoding="utf-8")
    assert "blank_issues_enabled: true" in config_yml
    assert "discussions" not in config_yml

    pr_tmpl = (dotnet_dir / ".github" / "PULL_REQUEST_TEMPLATE.md").read_text(encoding="utf-8")
    assert "`build`, `pr-title`" in pr_tmpl
    assert pr_tmpl.startswith("# Pull Request")
