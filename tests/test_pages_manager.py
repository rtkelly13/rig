"""Tests for GitHub Pages and custom domain governance manager."""

from __future__ import annotations

from unittest.mock import patch

from typer.testing import CliRunner

from rig.repo_governance.align_repo_conventions import app
from rig.repo_governance.pages_manager import PagesInspection, align_pages, inspect_pages
from rig.repo_governance.policy_config import PagesPolicy
from tests.fakes.github_rest import FakeGitHubApi

REPO = "rtkelly13/docs"
POLICY = PagesPolicy(enabled=True, build_type="workflow", cname="docs.ryankelly.dev", https_enforced=True)


def _pages(**site: object) -> FakeGitHubApi:
    return FakeGitHubApi({f"repos/{REPO}/pages": site})


def test_inspect_pages_enabled_and_aligned():
    api = _pages(
        status="built",
        cname="docs.ryankelly.dev",
        html_url="https://docs.ryankelly.dev",
        build_type="workflow",
        https_enforced=True,
    )

    res = inspect_pages(REPO, expected=POLICY, api=api)

    assert res.enabled is True
    assert res.cname == "docs.ryankelly.dev"
    assert res.build_type == "workflow"
    assert res.https_enforced is True
    assert res.is_aligned is True
    assert len(res.drift_reasons) == 0


def test_inspect_pages_drift_cname():
    api = _pages(status="built", cname="old.ryankelly.dev", build_type="workflow", https_enforced=True)

    res = inspect_pages(REPO, expected=POLICY, api=api)

    assert res.enabled is True
    assert res.is_aligned is False
    assert any("cname is 'old.ryankelly.dev'" in d for d in res.drift_reasons)


def test_inspect_pages_disabled_404():
    res = inspect_pages(REPO, expected=POLICY, api=FakeGitHubApi())

    assert res.enabled is False
    assert res.is_aligned is False
    assert "Pages is disabled" in res.drift_reasons


def test_align_pages_dry_run_when_disabled():
    api = FakeGitHubApi()

    res = align_pages(REPO, POLICY, dry_run=True, api=api)

    assert res.status == "dry_run"
    assert len(res.actions) == 2
    assert "POST repos/rtkelly13/docs/pages" in res.actions[0]
    assert '"build_type":"workflow"' in res.actions[0]
    assert "PUT repos/rtkelly13/docs/pages" in res.actions[1]
    assert '"cname":"docs.ryankelly.dev"' in res.actions[1]
    assert api.writes == []


def test_align_pages_enables_then_configures_and_is_then_aligned():
    api = FakeGitHubApi()

    res = align_pages(REPO, POLICY, api=api)

    assert res.status == "success"
    assert [(w.method, w.route) for w in api.writes] == [
        ("POST", f"repos/{REPO}/pages"),
        ("PUT", f"repos/{REPO}/pages"),
    ]
    assert inspect_pages(REPO, expected=POLICY, api=api).is_aligned
    assert align_pages(REPO, POLICY, api=api).status == "aligned"


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
