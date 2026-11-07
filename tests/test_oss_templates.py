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
