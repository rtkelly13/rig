"""Tests for ls-lint repository hygiene and alignment."""

from __future__ import annotations

import pathlib

import pytest

from rig.repo_governance.repo_hygiene import (
    align_ls_lint,
    inspect_ls_lint,
    render_ls_lint_config,
)


@pytest.mark.ci
def test_inspect_ls_lint_missing(tmp_path: pathlib.Path):
    res = inspect_ls_lint(tmp_path)
    assert not res.is_aligned
    assert res.status == "MISSING"


@pytest.mark.ci
def test_inspect_ls_lint_incomplete(tmp_path: pathlib.Path):
    (tmp_path / ".ls-lint.yml").write_text("ls:\n  .js: kebab-case\n", encoding="utf-8")
    res = inspect_ls_lint(tmp_path)
    assert not res.is_aligned
    assert res.status == "INCOMPLETE_RULES"


@pytest.mark.ci
def test_align_and_inspect_ls_lint(tmp_path: pathlib.Path):
    # Align generates .ls-lint.yml
    align_res = align_ls_lint(tmp_path, dry_run=False)
    assert align_res.status == "aligned"
    assert (tmp_path / ".ls-lint.yml").exists()

    # Inspect reports configured and aligned
    inspect_res = inspect_ls_lint(tmp_path)
    assert inspect_res.is_aligned
    assert inspect_res.status == "CONFIGURED"

    # Second align reports already_configured
    align_again = align_ls_lint(tmp_path, dry_run=False)
    assert align_again.status == "already_configured"


@pytest.mark.ci
def test_render_ls_lint_config_with_exceptions():
    content = render_ls_lint_config(allowed_exceptions=("scripts/test.sh",))
    assert ".sh: exists:0" in content
    assert ".ps1: exists:0" in content
    assert "scripts/test.sh" in content


@pytest.mark.ci
def test_render_ls_lint_config_with_script_rules():
    content = render_ls_lint_config(
        allowed_script_extensions=(".cs",),
        banned_script_extensions=(".py",),
    )
    assert "scripts:" in content
    assert '".cs": regex:.*' in content or ".cs: regex:.*" in content
    assert '".py": exists:0' in content or ".py: exists:0" in content
    assert ".*: exists:0" in content
