from __future__ import annotations

import json
from dataclasses import dataclass
from pathlib import Path

import httpx
import pytest
from typer.testing import CliRunner

from rig.workspace import package_dashboard
from rig.workspace.package_dashboard import (
    ChangelogReader,
    ChangelogResult,
    GitReader,
    LiveRegistryClient,
    MatchedTag,
    PackageStatusRow,
    RegistryClient,
    RegistryPackageInfo,
    changelog_has_unreleased,
    collect_package_dashboard,
    find_matching_tags,
)
from rig.workspace.packages import PackageBinding, PackageSpec
from rig.workspace.packages_cli import packages_cli as app

pytestmark = pytest.mark.ci

runner = CliRunner()


# ---------------------------------------------------------------- test doubles


@dataclass(slots=True, frozen=True)
class MockRegistryClient(RegistryClient):
    packages: dict[str, RegistryPackageInfo]

    def fetch_package_info(self, registry: str, package_name: str) -> RegistryPackageInfo:
        return self.packages.get(package_name, RegistryPackageInfo(latest_version=None))


@dataclass(slots=True, frozen=True)
class MockGitReader(GitReader):
    tags_by_repo: dict[str, tuple[str, ...]]

    def read_tags(self, repo_path: Path) -> tuple[str, ...]:
        return self.tags_by_repo.get(repo_path.name, ())


@dataclass(slots=True, frozen=True)
class MockChangelogReader(ChangelogReader):
    unreleased_repos: set[str]
    missing_repos: set[str] = frozenset()

    def check_changelog(self, clone_path: Path, manifest_path: str) -> ChangelogResult:
        if clone_path.name in self.missing_repos:
            return ChangelogResult(found=False, has_unreleased=False)
        return ChangelogResult(
            found=True,
            has_unreleased=clone_path.name in self.unreleased_repos,
        )


# ---------------------------------------------------------------- changelog tests


def test_changelog_has_unreleased_entries() -> None:
    text_with_entries = """# Changelog

All notable changes documented here.

## [Unreleased]

### Added
- Iceberg REST client retry policies (#123)
- Vectorization benchmark metrics

## [0.1.0] - 2026-10-01
"""
    assert changelog_has_unreleased(text_with_entries) is True


def test_changelog_clean_unreleased_section() -> None:
    text_empty_unreleased = """# Changelog

## [Unreleased]

## [0.0.1-alpha.1] - 2026-10-25

First published build.
"""
    assert changelog_has_unreleased(text_empty_unreleased) is False


def test_changelog_comments_only_in_unreleased() -> None:
    text_comment_only = """# Changelog

## [Unreleased]

<!-- Upcoming changes go here -->

## [1.0.0] - 2026-05-01
"""
    assert changelog_has_unreleased(text_comment_only) is False


def test_changelog_no_unreleased_section() -> None:
    text_no_unreleased = """# Changelog

## 0.16.0

Five MDX figures added.

## 0.15.0
"""
    assert changelog_has_unreleased(text_no_unreleased) is False


# ---------------------------------------------------------------- tag matching tests


def test_find_matching_tags_package_scoped() -> None:
    tags = (
        "Iceberg.Net.Core/v0.0.1-alpha.1",
        "Iceberg.Net.Core/v0.0.2",
        "Iceberg.Net.Avro/v0.0.1",
        "v0.0.0-root",
    )
    matches = find_matching_tags(tags, "Iceberg.Net.Core")
    assert matches == (
        MatchedTag(tag="Iceberg.Net.Core/v0.0.1-alpha.1", version="0.0.1-alpha.1"),
        MatchedTag(tag="Iceberg.Net.Core/v0.0.2", version="0.0.2"),
    )


def test_find_matching_tags_global_scoped_fallback() -> None:
    tags = (
        "v0.0.1",
        "v0.0.2",
        "v0.0.3",
        "prerebase/360-guard",
        "random-tag",
    )
    matches = find_matching_tags(tags, "Parquet.SourceGenerator")
    assert matches == (
        MatchedTag(tag="v0.0.1", version="0.0.1"),
        MatchedTag(tag="v0.0.2", version="0.0.2"),
        MatchedTag(tag="v0.0.3", version="0.0.3"),
    )


def test_find_matching_tags_none_matching() -> None:
    tags = ("branch-alpha", "prerebase/ci-matrix")
    matches = find_matching_tags(tags, "Parquet.TypeProvider")
    assert matches == ()


# ---------------------------------------------------------------- dashboard collection tests


def test_collect_dashboard_iceberg_net_synced(tmp_path: Path) -> None:
    repo_dir = tmp_path / "Iceberg.NET"
    repo_dir.mkdir()

    bindings = (
        PackageBinding(
            project="Iceberg.NET",
            repo="git@github.com:rtkelly-labs/Iceberg.NET.git",
            spec=PackageSpec(
                registry="nuget",
                name="Iceberg.Net.Core",
                manifest="src/Directory.Build.props",
                workflow="release.yml",
            ),
        ),
        PackageBinding(
            project="Iceberg.NET",
            repo="git@github.com:rtkelly-labs/Iceberg.NET.git",
            spec=PackageSpec(
                registry="nuget",
                name="Iceberg.Net.Avro",
                manifest="src/Directory.Build.props",
                workflow="release.yml",
            ),
        ),
    )

    registry = MockRegistryClient(
        {
            "Iceberg.Net.Core": RegistryPackageInfo(
                latest_version="0.0.1-alpha.1",
                all_versions=("0.0.1-alpha.1",),
            ),
            "Iceberg.Net.Avro": RegistryPackageInfo(
                latest_version="0.0.1-alpha.1",
                all_versions=("0.0.1-alpha.1",),
            ),
        }
    )

    git = MockGitReader({"Iceberg.NET": ("v0.0.1-alpha.1",)})
    changelog = MockChangelogReader(unreleased_repos=set())

    rows = collect_package_dashboard(
        bindings,
        registry,
        git,
        changelog,
        code_root=tmp_path,
    )

    assert len(rows) == 2
    for r in rows:
        assert r.project == "Iceberg.NET"
        assert r.registry_version == "0.0.1-alpha.1"
        assert r.latest_tag == "v0.0.1-alpha.1"
        assert r.tag_version == "0.0.1-alpha.1"
        assert r.changelog_unreleased is False
        assert r.flags == ()
        assert r.status == "synced"


def test_collect_dashboard_registry_without_tag(tmp_path: Path) -> None:
    repo_dir = tmp_path / "Parquet.SourceGenerator"
    repo_dir.mkdir()

    bindings = (
        PackageBinding(
            project="Parquet.SourceGenerator",
            repo="git@github.com:rtkelly-labs/Parquet.SourceGenerator.git",
            spec=PackageSpec(
                registry="nuget",
                name="Parquet.SourceGenerator",
                manifest="Directory.Build.props",
                workflow="release.yml",
            ),
        ),
    )

    registry = MockRegistryClient(
        {
            "Parquet.SourceGenerator": RegistryPackageInfo(
                latest_version="0.1.0-alpha.1",
                all_versions=("0.0.1", "0.0.2", "0.0.3", "0.1.0-alpha.1"),
            ),
        }
    )

    # Git only has tags up to v0.0.3
    git = MockGitReader({"Parquet.SourceGenerator": ("v0.0.1", "v0.0.2", "v0.0.3")})
    changelog = MockChangelogReader(unreleased_repos=set())

    rows = collect_package_dashboard(
        bindings,
        registry,
        git,
        changelog,
        code_root=tmp_path,
    )

    assert len(rows) == 1
    row = rows[0]
    assert row.registry_version == "0.1.0-alpha.1"
    assert row.latest_tag == "v0.0.3"
    assert "registry-without-tag" in row.flags
    assert row.status == "registry-without-tag"


def test_collect_dashboard_unpublished(tmp_path: Path) -> None:
    repo_dir = tmp_path / "Parquet.TypeProvider"
    repo_dir.mkdir()

    bindings = (
        PackageBinding(
            project="Parquet.TypeProvider",
            repo="git@github.com:rtkelly-labs/Parquet.TypeProvider.git",
            spec=PackageSpec(
                registry="nuget",
                name="Parquet.TypeProvider",
                manifest="Directory.Build.props",
                workflow="release.yml",
            ),
        ),
    )

    registry = MockRegistryClient({})
    git = MockGitReader({})
    changelog = MockChangelogReader(unreleased_repos=set(), missing_repos={"Parquet.TypeProvider"})

    rows = collect_package_dashboard(
        bindings,
        registry,
        git,
        changelog,
        code_root=tmp_path,
    )

    assert len(rows) == 1
    row = rows[0]
    assert row.registry_version is None
    assert row.latest_tag is None
    assert row.flags == ()
    assert row.status == "unpublished"


def test_collect_dashboard_version_family_drift(tmp_path: Path) -> None:
    repo_dir = tmp_path / "Monorepo"
    repo_dir.mkdir()

    bindings = (
        PackageBinding(
            project="Monorepo",
            repo="git@github.com:test/Monorepo.git",
            spec=PackageSpec(registry="nuget", name="Pkg.A", manifest="A.csproj", workflow="ci.yml"),
        ),
        PackageBinding(
            project="Monorepo",
            repo="git@github.com:test/Monorepo.git",
            spec=PackageSpec(registry="nuget", name="Pkg.B", manifest="B.csproj", workflow="ci.yml"),
        ),
    )

    registry = MockRegistryClient(
        {
            "Pkg.A": RegistryPackageInfo(latest_version="1.0.0", all_versions=("1.0.0",)),
            "Pkg.B": RegistryPackageInfo(latest_version="0.9.0", all_versions=("0.9.0",)),
        }
    )
    git = MockGitReader({"Monorepo": ("Pkg.A/v1.0.0", "Pkg.B/v0.9.0")})
    changelog = MockChangelogReader(unreleased_repos=set())

    rows = collect_package_dashboard(
        bindings,
        registry,
        git,
        changelog,
        code_root=tmp_path,
    )

    assert len(rows) == 2
    for r in rows:
        assert "version-family-drift" in r.flags


def test_collect_dashboard_unreleased_changes(tmp_path: Path) -> None:
    repo_dir = tmp_path / "Repo"
    repo_dir.mkdir()

    bindings = (
        PackageBinding(
            project="Repo",
            repo="git@github.com:test/Repo.git",
            spec=PackageSpec(registry="npm", name="@scope/pkg", manifest="package.json", workflow="release.yml"),
        ),
    )

    registry = MockRegistryClient(
        {
            "@scope/pkg": RegistryPackageInfo(latest_version="1.0.0", all_versions=("1.0.0",)),
        }
    )
    git = MockGitReader({"Repo": ("v1.0.0",)})
    changelog = MockChangelogReader(unreleased_repos={"Repo"})

    rows = collect_package_dashboard(
        bindings,
        registry,
        git,
        changelog,
        code_root=tmp_path,
    )

    assert len(rows) == 1
    row = rows[0]
    assert row.changelog_unreleased is True
    assert "unreleased-changes" in row.flags


def test_collect_dashboard_unlisted_and_deprecated(tmp_path: Path) -> None:
    repo_dir = tmp_path / "DepPkg"
    repo_dir.mkdir()

    bindings = (
        PackageBinding(
            project="DepPkg",
            repo="git@github.com:test/DepPkg.git",
            spec=PackageSpec(registry="nuget", name="OldPkg", manifest="OldPkg.csproj", workflow="ci.yml"),
        ),
    )

    registry = MockRegistryClient(
        {
            "OldPkg": RegistryPackageInfo(
                latest_version="2.0.0",
                all_versions=("2.0.0",),
                unlisted=True,
                deprecated=True,
            ),
        }
    )
    git = MockGitReader({"DepPkg": ("v2.0.0",)})
    changelog = MockChangelogReader(unreleased_repos=set())

    rows = collect_package_dashboard(
        bindings,
        registry,
        git,
        changelog,
        code_root=tmp_path,
    )

    assert len(rows) == 1
    row = rows[0]
    assert row.unlisted is True
    assert row.deprecated is True
    assert "unlisted" in row.flags
    assert "deprecated" in row.flags


def test_collect_dashboard_tag_without_registry(tmp_path: Path) -> None:
    repo_dir = tmp_path / "RepoTagAhead"
    repo_dir.mkdir()

    bindings = (
        PackageBinding(
            project="RepoTagAhead",
            repo="git@github.com:test/RepoTagAhead.git",
            spec=PackageSpec(registry="npm", name="my-pkg", manifest="package.json", workflow="release.yml"),
        ),
    )

    # Registry has 1.0.0, but git has v1.1.0 tag
    registry = MockRegistryClient(
        {
            "my-pkg": RegistryPackageInfo(latest_version="1.0.0", all_versions=("1.0.0",)),
        }
    )
    git = MockGitReader({"RepoTagAhead": ("v1.0.0", "v1.1.0")})
    changelog = MockChangelogReader(unreleased_repos=set())

    rows = collect_package_dashboard(
        bindings,
        registry,
        git,
        changelog,
        code_root=tmp_path,
    )

    assert len(rows) == 1
    row = rows[0]
    assert row.latest_tag == "v1.1.0"
    assert "tag-without-registry" in row.flags


def test_collect_dashboard_registry_unavailable(tmp_path: Path) -> None:
    (tmp_path / "Repo").mkdir()
    bindings = (
        PackageBinding(
            project="Repo",
            repo="git@github.com:test/Repo.git",
            spec=PackageSpec(registry="nuget", name="Pkg", manifest="Pkg.csproj", workflow="release.yml"),
        ),
    )
    registry = MockRegistryClient({"Pkg": RegistryPackageInfo(latest_version=None, lookup_failed=True)})

    rows = collect_package_dashboard(
        bindings,
        registry,
        MockGitReader({"Repo": ("v1.0.0",)}),
        MockChangelogReader(unreleased_repos=set()),
        code_root=tmp_path,
    )

    assert rows[0].flags == ("registry-unavailable",)
    assert rows[0].status == "registry-unavailable"


def test_collect_dashboard_unavailable_registry_is_not_drift(tmp_path: Path) -> None:
    (tmp_path / "Monorepo").mkdir()
    bindings = tuple(
        PackageBinding(
            project="Monorepo",
            repo="git@github.com:test/Monorepo.git",
            spec=PackageSpec(registry="nuget", name=name, manifest=f"{name}.csproj", workflow="ci.yml"),
        )
        for name in ("Pkg.A", "Pkg.B")
    )
    registry = MockRegistryClient(
        {
            "Pkg.A": RegistryPackageInfo(latest_version="1.0.0", all_versions=("1.0.0",)),
            "Pkg.B": RegistryPackageInfo(latest_version=None, lookup_failed=True),
        }
    )

    rows = collect_package_dashboard(
        bindings,
        registry,
        MockGitReader({"Monorepo": ("Pkg.A/v1.0.0", "Pkg.B/v1.0.0")}),
        MockChangelogReader(unreleased_repos=set()),
        code_root=tmp_path,
    )

    for r in rows:
        assert "version-family-drift" not in r.flags


@pytest.mark.parametrize(("status", "failed"), [(404, False), (429, True), (503, True)])
def test_live_registry_separates_missing_from_failed(status: int, failed: bool) -> None:
    client = httpx.Client(transport=httpx.MockTransport(lambda _request: httpx.Response(status)))
    live = LiveRegistryClient(client=client)

    for registry in ("nuget", "npm"):
        info = live.fetch_package_info(registry, "Some.Package")
        assert info.latest_version is None
        assert info.lookup_failed is failed


def test_package_status_row_to_json() -> None:
    row = PackageStatusRow(
        project="TestProject",
        package="TestPackage",
        registry="nuget",
        manifest="src/Directory.Build.props",
        registry_version="1.0.0",
        latest_tag="v1.0.0",
        tag_version="1.0.0",
        changelog_found=True,
        changelog_unreleased=False,
        unlisted=False,
        deprecated=False,
        flags=(),
        status="synced",
    )
    data = row.to_json()
    assert data["project"] == "TestProject"
    assert data["package"] == "TestPackage"
    assert data["status"] == "synced"
    assert data["flags"] == []


# ---------------------------------------------------------------- CLI integration tests


def test_cli_dashboard_json_offline(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setenv("CODE_ROOT", str(tmp_path))
    monkeypatch.setattr(package_dashboard, "LiveRegistryClient", lambda: MockRegistryClient({}))
    monkeypatch.setattr(package_dashboard, "LiveGitReader", lambda: MockGitReader({}))
    monkeypatch.setattr(package_dashboard, "LiveChangelogReader", lambda: MockChangelogReader(unreleased_repos=set()))
    config_file = tmp_path / "managed-projects.json"
    config_content = {
        "projects": [
            {
                "name": "Iceberg.NET",
                "repo": "git@github.com:rtkelly-labs/Iceberg.NET.git",
                "packages": [
                    {
                        "registry": "nuget",
                        "name": "Iceberg.Net.Core",
                        "manifest": "src/Directory.Build.props",
                        "workflow": "release.yml",
                    }
                ],
            },
            {
                "name": "Parquet.TypeProvider",
                "repo": "git@github.com:rtkelly-labs/Parquet.TypeProvider.git",
                "packages": [
                    {
                        "registry": "nuget",
                        "name": "Parquet.TypeProvider",
                        "manifest": "Directory.Build.props",
                        "workflow": "release.yml",
                    }
                ],
            },
        ]
    }
    config_file.write_text(json.dumps(config_content), encoding="utf-8")

    result = runner.invoke(app, ["dashboard", "--config", str(config_file), "--root", str(tmp_path), "--json"])
    assert result.exit_code == 0
    payload = json.loads(result.stdout)
    assert isinstance(payload, list)
    assert len(payload) == 2
    names = {item["package"] for item in payload}
    assert "Iceberg.Net.Core" in names
    assert "Parquet.TypeProvider" in names
