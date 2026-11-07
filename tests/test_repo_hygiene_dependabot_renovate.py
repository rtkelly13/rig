"""Tests for Dependabot and Renovate coexistence governance check."""

from __future__ import annotations

import pathlib

import pytest

from rig.repo_governance.repo_hygiene import (
    find_dependabot_config,
    find_renovate_config,
    has_dependabot_version_updates,
    inspect_dependabot_renovate,
)


@pytest.mark.ci
def test_neither_renovate_nor_dependabot(tmp_path: pathlib.Path) -> None:
    res = inspect_dependabot_renovate(tmp_path)
    assert res.is_aligned
    assert res.status == "NO_RENOVATE"


@pytest.mark.ci
def test_only_dependabot_version_updates(tmp_path: pathlib.Path) -> None:
    dot_github = tmp_path / ".github"
    dot_github.mkdir(parents=True)
    (dot_github / "dependabot.yml").write_text(
        """version: 2
updates:
  - package-ecosystem: "npm"
    directory: "/"
    schedule:
      interval: "weekly"
""",
        encoding="utf-8",
    )
    assert find_dependabot_config(tmp_path) == dot_github / "dependabot.yml"
    assert has_dependabot_version_updates(tmp_path)
    res = inspect_dependabot_renovate(tmp_path)
    assert res.is_aligned
    assert res.status == "NO_RENOVATE"


@pytest.mark.ci
def test_only_renovate(tmp_path: pathlib.Path) -> None:
    (tmp_path / "renovate.json").write_text('{"extends": ["config:recommended"]}\n', encoding="utf-8")
    assert find_renovate_config(tmp_path) is not None
    assert not has_dependabot_version_updates(tmp_path)
    res = inspect_dependabot_renovate(tmp_path)
    assert res.is_aligned
    assert res.status == "CONFIGURED"


@pytest.mark.ci
def test_renovate_nested_dot_github_json5(tmp_path: pathlib.Path) -> None:
    dot_github = tmp_path / ".github"
    dot_github.mkdir(parents=True)
    (dot_github / "renovate.json5").write_text('{\n  extends: ["github>rtkelly-labs/.github"]\n}\n', encoding="utf-8")
    assert find_renovate_config(tmp_path) == dot_github / "renovate.json5"
    res = inspect_dependabot_renovate(tmp_path)
    assert res.is_aligned
    assert res.status == "CONFIGURED"


@pytest.mark.ci
def test_conflict_renovate_and_dependabot_version_updates(tmp_path: pathlib.Path) -> None:
    (tmp_path / "renovate.json").write_text('{"extends": ["config:recommended"]}\n', encoding="utf-8")
    dot_github = tmp_path / ".github"
    dot_github.mkdir(parents=True)
    (dot_github / "dependabot.yml").write_text(
        """version: 2
updates:
  - package-ecosystem: "nuget"
    directory: "/"
    schedule:
      interval: "daily"
""",
        encoding="utf-8",
    )
    res = inspect_dependabot_renovate(tmp_path)
    assert not res.is_aligned
    assert res.status == "CONFLICT"
    assert res.reason is not None
    assert "Renovate config (renovate.json)" in res.reason
    assert ".github/dependabot.yml" in res.reason


@pytest.mark.ci
def test_security_only_dependabot_with_renovate_allowed(tmp_path: pathlib.Path) -> None:
    (tmp_path / "renovate.json").write_text('{"extends": ["config:recommended"]}\n', encoding="utf-8")
    dot_github = tmp_path / ".github"
    dot_github.mkdir(parents=True)
    # open-pull-requests-limit: 0 disables version update PRs while keeping security alerts
    (dot_github / "dependabot.yml").write_text(
        """version: 2
updates:
  - package-ecosystem: "npm"
    directory: "/"
    schedule:
      interval: "weekly"
    open-pull-requests-limit: 0
""",
        encoding="utf-8",
    )
    assert not has_dependabot_version_updates(tmp_path)
    res = inspect_dependabot_renovate(tmp_path)
    assert res.is_aligned
    assert res.status == "CONFIGURED"


@pytest.mark.ci
def test_empty_dependabot_updates_with_renovate_allowed(tmp_path: pathlib.Path) -> None:
    (tmp_path / ".renovaterc.json").write_text("{}", encoding="utf-8")
    dot_github = tmp_path / ".github"
    dot_github.mkdir(parents=True)
    (dot_github / "dependabot.yaml").write_text("version: 2\nupdates: []\n", encoding="utf-8")
    assert not has_dependabot_version_updates(tmp_path)
    res = inspect_dependabot_renovate(tmp_path)
    assert res.is_aligned
    assert res.status == "CONFIGURED"
