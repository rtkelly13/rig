"""
Tests for the Intentional Policy Template Engine.
"""

import pathlib
from unittest.mock import patch

import pytest

from rig import proc
from rig.repo_governance.policy_template_engine import (
    RepoMetadata,
    apply_ai_policy,
    apply_security_policy,
    detect_repo_metadata,
    inject_readme_ai_disclosure,
    render_policy_template,
)


@pytest.mark.ci
def test_detect_repo_metadata_fallback(tmp_path: pathlib.Path):
    meta = detect_repo_metadata(tmp_path)
    assert meta.repo_name == tmp_path.name
    assert meta.supported_runtimes


@pytest.mark.ci
def test_render_policy_template():
    context = RepoMetadata(
        repo_name="rtkelly13/Parquet.TypeProvider",
        is_public=True,
        license="MIT",
        supported_runtimes=".NET 8 & .NET 9",
        author_email="test@example.com",
    )
    rendered = render_policy_template("AI_POLICY.md.jinja", context)
    assert "rtkelly13/Parquet.TypeProvider" in rendered
    assert ".NET 8 & .NET 9" in rendered
    assert "MIT License" in rendered


@pytest.mark.ci
def test_apply_ai_and_security_policy(tmp_path: pathlib.Path):
    context = RepoMetadata(
        repo_name="test-repo",
        is_public=True,
        license="MIT",
        supported_runtimes="Python 3.13",
        author_email="test@example.com",
    )
    apply_ai_policy(tmp_path, context)
    apply_security_policy(tmp_path, context)

    assert (tmp_path / "AI_POLICY.md").exists()
    assert (tmp_path / "SECURITY.md").exists()

    ai_content = (tmp_path / "AI_POLICY.md").read_text(encoding="utf-8")
    assert "test-repo" in ai_content
    assert "Python 3.13" in ai_content


@pytest.mark.ci
def test_inject_readme_ai_disclosure_idempotency(tmp_path: pathlib.Path):
    readme = tmp_path / "README.md"
    readme_content = (
        "# Test Project\n\n"
        "[![Build Status](https://example.com/badge.svg)](https://example.com)\n\n"
        "## 📄 License\n\nMIT"
    )
    readme.write_text(readme_content, encoding="utf-8")

    context = RepoMetadata(
        repo_name="test-repo",
        is_public=True,
        license="MIT",
        supported_runtimes="Python 3.13",
        author_email="test@example.com",
    )

    # First injection
    inject_readme_ai_disclosure(tmp_path, context)
    text1 = readme.read_text(encoding="utf-8")
    assert "[![AI Assisted]" in text1
    assert "## 🤖 AI Utilization & Transparency" in text1

    # Second injection (must be idempotent)
    inject_readme_ai_disclosure(tmp_path, context)
    text2 = readme.read_text(encoding="utf-8")
    assert text1 == text2


def test_detect_repo_metadata_describes_origin_not_upstream(tmp_path: pathlib.Path):
    """A fork clone has an `upstream` remote, which a bare `gh repo view` would prefer."""
    gh_calls: list[list[str]] = []

    def fake_run(argv: list[str], **_: object) -> proc.Ran:
        if argv[:3] == ["git", "remote", "get-url"]:
            return proc.Ran(0, "https://github.com/rtkelly-labs/parquet-dotnet.git\n", "")
        gh_calls.append(argv)
        return proc.Ran(0, '{"nameWithOwner": "rtkelly-labs/parquet-dotnet", "isPrivate": false}', "")

    with patch("rig.proc.run", side_effect=fake_run):
        meta = detect_repo_metadata(tmp_path)

    assert gh_calls[0][3] == "https://github.com/rtkelly-labs/parquet-dotnet.git"
    assert meta.repo_name == "rtkelly-labs/parquet-dotnet"
