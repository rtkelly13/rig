"""Real manifest evidence, package aliases, incomplete scans and cycle-preserving DAGs."""

from __future__ import annotations

import json
import subprocess
from pathlib import Path

import pytest

from rig.connectivity.service import build_graph

pytestmark = pytest.mark.ci


def checkout(root: Path, name: str, files: dict[str, str]) -> Path:
    path = root / name
    path.mkdir(parents=True)
    for filename, text in files.items():
        target = path / filename
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(text, encoding="utf-8")
    subprocess.run(["git", "init", "--quiet", str(path)], check=True)
    subprocess.run(["git", "-C", str(path), "add", "."], check=True)
    return path


def registry(path: Path, projects: list[dict[str, object]]) -> Path:
    target = path / "config" / "managed-projects.json"
    target.parent.mkdir(parents=True)
    target.write_text(json.dumps({"projects": projects}), encoding="utf-8")
    return target


def test_connections_join_real_npm_nuget_python_and_project_references(tmp_path: Path) -> None:
    root = tmp_path / "personal"
    checkout(root, "ui", {"package.json": json.dumps({"name": "@scope/new-ui"})})
    checkout(
        root,
        "library",
        {"lib.csproj": "<Project><PropertyGroup><PackageId>Managed.Lib</PackageId></PropertyGroup></Project>"},
    )
    checkout(root, "python", {"pyproject.toml": '[project]\nname="Managed_Tool"\nversion="1.0"\n'})
    checkout(
        root,
        "app",
        {
            "package.json": json.dumps(
                {
                    "name": "app",
                    "dependencies": {"@scope/new-ui": "^1.0"},
                    "devDependencies": {"old-ui": "npm:@scope/old-ui@^0.5"},
                }
            ),
            "app.csproj": (
                '<Project><ItemGroup><PackageReference Include="MANAGED.LIB" Version="1.2"/>'
                '<ProjectReference Include="../library/lib.csproj"/></ItemGroup></Project>'
            ),
            "pyproject.toml": '[project]\nname="app-python"\ndependencies=["managed-tool>=1"]\n',
        },
    )
    cfg = registry(
        tmp_path / "rig",
        [
            {
                "name": "ui",
                "repo": "https://github.com/owner/ui",
                "packages": [
                    {"registry": "npm", "name": "@scope/old-ui", "manifest": "package.json", "workflow": "publish.yml"}
                ],
            },
            {"name": "library", "repo": "https://github.com/owner/library"},
            {"name": "python", "repo": "https://github.com/owner/python"},
            {"name": "app", "repo": "https://github.com/owner/app"},
        ],
    )
    value = build_graph(cfg, root, tmp_path / "temp" / "graph.duckdb")
    assert len(value.projects) == 4
    assert {(edge.source, edge.target) for edge in value.dependencies} == {
        ("owner/ui", "owner/app"),
        ("owner/library", "owner/app"),
        ("owner/python", "owner/app"),
    }
    assert {edge.registry for edge in value.dependencies} == {"npm", "nuget", "pypi", "project"}
    assert {edge.package for edge in value.dependencies if edge.registry == "npm"} == {"@scope/new-ui", "@scope/old-ui"}
    assert all(edge.evidence in {"package.json", "pyproject.toml", "app.csproj"} for edge in value.dependencies)
    assert any(edge.requirement == "^1.0" for edge in value.dependencies)
    assert all(project.scanned for project in value.projects)
    repeated = build_graph(cfg, root, tmp_path / "temp" / "graph.duckdb")
    assert repeated.dependencies == value.dependencies
    assert repeated.components == value.components


def test_cycles_are_condensed_without_dropping_original_evidence(tmp_path: Path) -> None:
    root = tmp_path / "personal"
    checkout(root, "a", {"package.json": '{"name":"a","dependencies":{"b":"1"}}'})
    checkout(root, "b", {"package.json": '{"name":"b","dependencies":{"a":"1"}}'})
    checkout(root, "c", {"package.json": '{"dependencies":{"a":"1"}}'})
    cfg = registry(tmp_path / "rig", [{"name": name, "repo": f"https://github.com/owner/{name}"} for name in "abc"])
    graph = build_graph(cfg, root, tmp_path / "temp" / "graph.duckdb")
    cycle = next(component for component in graph.components if component.cyclic)
    assert cycle.members == ["owner/a", "owner/b"]
    assert len(graph.dependencies) == 3
    assert len(graph.edges) == 1 and graph.edges[0].source == cycle.id and graph.edges[0].target == "owner/c"


def test_ambiguous_publishers_and_missing_checkouts_remain_explicit(tmp_path: Path) -> None:
    root = tmp_path / "personal"
    checkout(root, "a", {"package.json": '{"name":"same"}'})
    checkout(root, "b", {"package.json": '{"name":"same"}'})
    checkout(root, "consumer", {"package.json": '{"dependencies":{"same":"1"}}'})
    cfg = registry(
        tmp_path / "rig",
        [{"name": name, "repo": f"https://github.com/owner/{name}"} for name in ("a", "b", "consumer", "missing")],
    )
    graph = build_graph(cfg, root, tmp_path / "temp" / "graph.duckdb")
    assert graph.dependencies == []
    assert any("Ambiguous publisher" in warning for warning in graph.warnings)
    missing = next(project for project in graph.projects if project.name == "missing")
    assert not missing.scanned and missing.warnings


def test_nuget_comments_are_ignored_and_central_versions_use_nearest_scope(tmp_path: Path) -> None:
    root = tmp_path / "personal"
    checkout(root, "library", {"lib.csproj": "<Project><PackageId>Managed.Lib</PackageId></Project>"})
    checkout(
        root,
        "app",
        {
            "Directory.Packages.props": '<Project><PackageVersion Include="Managed.Lib" Version="1.0"/></Project>',
            "app.csproj": (
                '<Project><!-- <PackageReference Include="Managed.Lib" Version="99"/> -->'
                '<PackageReference Include="Managed.Lib"/></Project>'
            ),
            "nested/Directory.Packages.props": (
                '<Project><PackageVersion Include="Managed.Lib" Version="2.0"/></Project>'
            ),
            "nested/test.csproj": '<Project><PackageReference Include="Managed.Lib"/></Project>',
        },
    )
    cfg = registry(
        tmp_path / "rig", [{"name": name, "repo": f"https://github.com/owner/{name}"} for name in ("library", "app")]
    )
    graph = build_graph(cfg, root, tmp_path / "temp" / "graph.duckdb")
    assert {edge.requirement for edge in graph.dependencies} == {"1.0", "2.0"}


def test_declared_connection_supports_a_repository_without_a_local_checkout(tmp_path: Path) -> None:
    root = tmp_path / "personal"
    checkout(root, "provider", {"package.json": '{"name":"managed"}'})
    cfg = registry(
        tmp_path / "rig",
        [
            {"name": "provider", "repo": "https://github.com/owner/provider"},
            {"name": "consumer", "repo": "https://github.com/owner/consumer", "depends_on": ["provider", "unknown"]},
        ],
    )
    graph = build_graph(cfg, root, tmp_path / "temp" / "graph.duckdb")
    assert len(graph.dependencies) == 1
    assert graph.dependencies[0].registry == "declared"
    assert graph.dependencies[0].source == "owner/provider" and graph.dependencies[0].target == "owner/consumer"
    assert any("Unknown or self dependency" in warning for warning in graph.warnings)


def test_nuget_updates_require_an_include_and_explicit_metadata_wins(tmp_path: Path) -> None:
    root = tmp_path / "personal"
    checkout(root, "library", {"lib.csproj": "<Project><PackageId>Managed.Lib</PackageId></Project>"})
    checkout(
        root,
        "app",
        {
            "Directory.Packages.props": '<Project><PackageVersion Include="Managed.Lib" Version="1.0"/></Project>',
            "update.csproj": '<Project><PackageReference Update="Managed.Lib" Version="99"/></Project>',
            "child.csproj": (
                '<Project><PackageReference Include="Managed.Lib"><Version>2.0</Version></PackageReference></Project>'
            ),
            "override.csproj": '<Project><PackageReference Include="Managed.Lib" VersionOverride="3.0"/></Project>',
            "child-override.csproj": (
                "<Project>"
                '<PackageReference Include="Managed.Lib" Version="2.0"><VersionOverride>4.0</VersionOv'
                "erride></PackageReference></Project>"
            ),
            "included.csproj": (
                "<Project>"
                '<PackageReference Include="Managed.Lib"/><PackageReference Update="Managed.Lib" Versi'
                'on="5.0"/></Project>'
            ),
        },
    )
    cfg = registry(
        tmp_path / "rig", [{"name": name, "repo": f"https://github.com/owner/{name}"} for name in ("library", "app")]
    )
    graph = build_graph(cfg, root, tmp_path / "temp" / "graph.duckdb")
    assert {edge.requirement for edge in graph.dependencies} == {"2.0", "3.0", "4.0", "5.0"}
    assert not any(edge.evidence == "update.csproj" for edge in graph.dependencies)
