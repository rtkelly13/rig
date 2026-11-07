"""Tests for GitHub Pages and custom domain governance manager."""

from __future__ import annotations

import subprocess
from unittest.mock import MagicMock, patch

from typer.testing import CliRunner

from rig.repo_governance.align_repo_conventions import app
from rig.repo_governance.pages_manager import PagesInspection, align_pages, inspect_pages
from rig.repo_governance.policy_config import PagesPolicy


def test_inspect_pages_enabled_and_aligned():
    mock_stdout = """{
        "url": "https://api.github.com/repos/rtkelly13/docs/pages",
        "status": "built",
        "cname": "docs.ryankelly.dev",
        "custom_404": false,
        "html_url": "https://docs.ryankelly.dev",
        "build_type": "workflow",
        "https_enforced": true
    }"""
    expected_policy = PagesPolicy(
        enabled=True,
        build_type="workflow",
        cname="docs.ryankelly.dev",
        https_enforced=True,
    )

    with patch("subprocess.run") as mock_run:
        mock_run.return_value = MagicMock(stdout=mock_stdout, returncode=0)
        res = inspect_pages("rtkelly13/docs", expected=expected_policy)

    assert res.enabled is True
    assert res.cname == "docs.ryankelly.dev"
    assert res.build_type == "workflow"
    assert res.https_enforced is True
    assert res.is_aligned is True
    assert len(res.drift_reasons) == 0


def test_inspect_pages_drift_cname():
    mock_stdout = """{
        "status": "built",
        "cname": "old.ryankelly.dev",
        "build_type": "workflow",
        "https_enforced": true
    }"""
    expected_policy = PagesPolicy(
        enabled=True,
        build_type="workflow",
        cname="docs.ryankelly.dev",
        https_enforced=True,
    )

    with patch("subprocess.run") as mock_run:
        mock_run.return_value = MagicMock(stdout=mock_stdout, returncode=0)
        res = inspect_pages("rtkelly13/docs", expected=expected_policy)

    assert res.enabled is True
    assert res.is_aligned is False
    assert any("cname is 'old.ryankelly.dev'" in d for d in res.drift_reasons)


def test_inspect_pages_disabled_404():
    expected_policy = PagesPolicy(
        enabled=True,
        build_type="workflow",
        cname="docs.ryankelly.dev",
        https_enforced=True,
    )

    err = subprocess.CompletedProcess(["gh", "api", "repos/test/pages"], 1, "", "HTTP 404: Not Found")

    with patch("subprocess.run", return_value=err):
        res = inspect_pages("rtkelly13/docs", expected=expected_policy)

    assert res.enabled is False
    assert res.is_aligned is False
    assert "Pages is disabled" in res.drift_reasons


def test_align_pages_dry_run_when_disabled():
    policy = PagesPolicy(
        enabled=True,
        build_type="workflow",
        cname="docs.ryankelly.dev",
        https_enforced=True,
    )

    with patch(
        "rig.repo_governance.pages_manager.inspect_pages",
        return_value=PagesInspection(
            enabled=False,
            cname=None,
            build_type=None,
            https_enforced=True,
            status=None,
            html_url=None,
            drift_reasons=("Pages is disabled",),
            is_aligned=False,
        ),
    ):
        res = align_pages("rtkelly13/docs", policy, dry_run=True)

    assert res.status == "dry_run"
    assert len(res.actions) == 2
    assert "POST repos/rtkelly13/docs/pages" in res.actions[0]
    assert '"build_type":"workflow"' in res.actions[0]
    assert "PUT repos/rtkelly13/docs/pages" in res.actions[1]
    assert '"cname":"docs.ryankelly.dev"' in res.actions[1]


def test_align_pages_execute_flow():
    policy = PagesPolicy(
        enabled=True,
        build_type="workflow",
        cname="docs.ryankelly.dev",
        https_enforced=True,
    )

    with (
        patch(
            "rig.repo_governance.pages_manager.inspect_pages",
            return_value=PagesInspection(
                enabled=False,
                cname=None,
                build_type=None,
                https_enforced=True,
                status=None,
                html_url=None,
                drift_reasons=("Pages is disabled",),
                is_aligned=False,
            ),
        ),
        patch("subprocess.run") as mock_run,
    ):
        mock_run.return_value = MagicMock(returncode=0)
        res = align_pages("rtkelly13/docs", policy, dry_run=False)

    assert res.status == "success"
    assert mock_run.call_count == 2


def test_cli_pages_command():
    runner = CliRunner()
    with (
        patch(
            "rig.repo_governance.cli_sync.get_gh_repo_name",
            return_value="rtkelly13/docs",
        ),
        patch("rig.repo_governance.cli_sync.inspect_pages") as mock_inspect,
    ):
        mock_inspect.return_value = PagesInspection(
            enabled=True,
            cname="docs.ryankelly.dev",
            build_type=None,
            https_enforced=True,
            status=None,
            html_url=None,
            drift_reasons=(),
            is_aligned=True,
        )
        result = runner.invoke(app, ["pages", "--audit"])
        assert result.exit_code == 0
        assert "Pages aligned" in result.stdout
