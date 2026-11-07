"""Read package declarations and actual consumption from tracked, bounded local manifests."""

from __future__ import annotations

import re
import subprocess
from dataclasses import dataclass, field
from pathlib import Path
from typing import Annotated

from pydantic import Field

from rig.connectivity.models import Consumption, Publication
from rig.typed_json import decode_json
from rig.typed_toml import decode_toml

_SKIP = frozenset(
    {
        ".git",
        "node_modules",
        ".venv",
        "venv",
        "temp",
        ".worktrees",
        "bin",
        "obj",
        "dist",
        "build",
        ".next",
        "fixtures",
        "__fixtures__",
        "testdata",
    }
)


@dataclass(frozen=True, slots=True)
class Npm:
    name: str = ""
    dependencies: dict[str, str] = field(default_factory=dict[str, str])
    development: Annotated[dict[str, str], Field(alias="devDependencies")] = field(default_factory=dict[str, str])
    peers: Annotated[dict[str, str], Field(alias="peerDependencies")] = field(default_factory=dict[str, str])
    optional: Annotated[dict[str, str], Field(alias="optionalDependencies")] = field(default_factory=dict[str, str])


@dataclass(frozen=True, slots=True)
class PythonProject:
    name: str = ""
    dependencies: list[str] = field(default_factory=list[str])
    optional: Annotated[dict[str, list[str]], Field(alias="optional-dependencies")] = field(
        default_factory=dict[str, list[str]]
    )


@dataclass(frozen=True, slots=True)
class Pyproject:
    project: PythonProject = field(default_factory=PythonProject)


@dataclass(frozen=True, slots=True)
class ProjectLink:
    target: Path
    evidence: str


@dataclass(slots=True)
class Inventory:
    publications: list[Publication] = field(default_factory=list[Publication])
    consumptions: list[Consumption] = field(default_factory=list[Consumption])
    project_refs: list[ProjectLink] = field(default_factory=list[ProjectLink])
    paths: list[Path] = field(default_factory=list[Path])
    warnings: list[str] = field(default_factory=list[str])


def canonical(registry: str, name: str) -> str:
    """npm is already lower-case; NuGet/Python identities are case-insensitive."""
    return re.sub(r"[-_.]+", "-", name).lower() if registry == "pypi" else name.lower()


def _attr(text: str, name: str) -> str:
    match = re.search(rf"\b{re.escape(name)}\s*=\s*['\"]([^'\"]+)['\"]", text)
    return match.group(1) if match else ""


def _metadata(tag: str, body: str, name: str) -> str:
    child = re.search(rf"<{name}>\s*([^<]+?)\s*</{name}>", body)
    return _attr(tag, name) or (child.group(1) if child else "")


def _nuget_references(text: str, repo: str, evidence: str, versions: dict[str, str]) -> list[Consumption]:
    references: dict[str, Consumption] = {}
    updates: dict[str, str] = {}
    for match in re.finditer(r"<PackageReference\b([^>]*?)(?:/\s*>|>(.*?)</PackageReference\s*>)", text, re.DOTALL):
        tag, body = match.group(1), match.group(2) or ""
        name = _attr(tag, "Include")
        update = _attr(tag, "Update")
        version = _metadata(tag, body, "VersionOverride") or _metadata(tag, body, "Version")
        if name and "$(" not in name:
            key = canonical("nuget", name)
            references[key] = Consumption(
                "nuget", key, repo, version or versions.get(key, "centrally resolved"), "runtime", evidence
            )
        elif update and "$(" not in update and version:
            updates[canonical("nuget", update)] = version
    return [
        Consumption(ref.registry, ref.package, ref.repo, updates.get(name, ref.requirement), ref.scope, ref.evidence)
        for name, ref in references.items()
    ]


def manifest_paths(root: Path) -> list[Path]:
    """Git's tracked inventory avoids caches/worktrees and recursive traversal through links."""
    result = subprocess.run(
        ["git", "ls-files", "-z"],
        cwd=root,
        capture_output=True,
        text=True,
        encoding="utf-8",
        timeout=10,
        check=True,
        stdin=subprocess.DEVNULL,
    )
    paths: list[Path] = []
    for name in sorted(result.stdout.split("\0")):
        relative = Path(name)
        if not name or any(part in _SKIP for part in relative.parts):
            continue
        if (
            relative.name not in {"package.json", "pyproject.toml", "Directory.Packages.props", "Directory.Build.props"}
            and relative.suffix != ".csproj"
        ):
            continue
        candidate = root / relative
        if candidate.is_file() and candidate.resolve().is_relative_to(root.resolve()):
            paths.append(candidate)
    if len(paths) > 500:
        raise ValueError("More than 500 tracked package manifests; narrow the scan before indexing")
    return paths


def read_inventory(root: Path, repo: str) -> Inventory:
    """Extract providers and consumers, retaining manifest paths for every relationship."""
    inventory = Inventory()
    try:
        inventory.paths = manifest_paths(root)
    except (OSError, ValueError, subprocess.SubprocessError) as exc:
        inventory.warnings.append(f"Manifest inventory unavailable: {exc}")
        return inventory
    central: dict[Path, dict[str, str]] = {}
    texts: dict[Path, str] = {}
    for path in inventory.paths:
        try:
            if path.stat().st_size > 2_000_000:
                raise ValueError("Manifest exceeds 2 MB")
            text = path.read_text(encoding="utf-8")
            if path.suffix in {".csproj", ".props"}:
                text = re.sub(r"<!--.*?-->", "", text, flags=re.DOTALL)
            texts[path] = text
            if path.name == "Directory.Packages.props":
                for match in re.finditer(r"<PackageVersion\b[^>]*>", text):
                    tag = match.group(0)
                    central.setdefault(path.parent, {})[canonical("nuget", _attr(tag, "Include"))] = _attr(
                        tag, "Version"
                    )
        except (OSError, ValueError) as exc:
            inventory.warnings.append(f"{path.relative_to(root).as_posix()}: {exc}")
    for path, text in texts.items():
        evidence = path.relative_to(root).as_posix()
        try:
            if path.name == "package.json":
                package = decode_json(Npm, text)
                if package.name:
                    inventory.publications.append(Publication("npm", canonical("npm", package.name), repo, evidence))
                for scope, values in (
                    ("runtime", package.dependencies),
                    ("development", package.development),
                    ("peer", package.peers),
                    ("optional", package.optional),
                ):
                    for name, version in values.items():
                        alias = re.match(r"npm:((?:@[^/\s]+/)?[^@\s]+)(?:@|$)", version)
                        inventory.consumptions.append(
                            Consumption(
                                "npm",
                                canonical("npm", alias.group(1) if alias else name),
                                repo,
                                version,
                                scope,
                                evidence,
                            )
                        )
            elif path.name == "pyproject.toml":
                project = decode_toml(Pyproject, text).project
                if project.name:
                    inventory.publications.append(Publication("pypi", canonical("pypi", project.name), repo, evidence))
                requirements = [("runtime", value) for value in project.dependencies]
                requirements.extend(
                    (f"optional:{name}", value) for name, values in project.optional.items() for value in values
                )
                for scope, value in requirements:
                    match = re.match(r"[A-Za-z0-9][A-Za-z0-9._-]*", value)
                    if match:
                        inventory.consumptions.append(
                            Consumption("pypi", canonical("pypi", match.group(0)), repo, value, scope, evidence)
                        )
            else:
                package_id = re.search(r"<PackageId>\s*([^<]+?)\s*</PackageId>", text)
                if package_id and "$(" not in package_id.group(1):
                    inventory.publications.append(
                        Publication("nuget", canonical("nuget", package_id.group(1)), repo, evidence)
                    )
                empty_versions: dict[str, str] = {}
                versions = next((central[parent] for parent in path.parents if parent in central), empty_versions)
                inventory.consumptions.extend(_nuget_references(text, repo, evidence, versions))
                for match in re.finditer(r"<ProjectReference\b[^>]*>", text):
                    target = _attr(match.group(0), "Include")
                    if target and "$(" not in target:
                        inventory.project_refs.append(
                            ProjectLink((path.parent / target.replace("\\", "/")).resolve(), evidence)
                        )
        except (ValueError, OSError) as exc:
            inventory.warnings.append(f"{evidence}: {exc}")
    return inventory
