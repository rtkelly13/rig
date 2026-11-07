"""Package and version dashboard across estate repositories, registries, tags, and changelogs.

Tracks every declared package from `config/managed-projects.json` `packages[]`:
- Latest published version on public registries (NuGet flatcontainer + registration API, npm packument)
- Latest matching git tag (`v<version>` and `<PackageId>/v<version>`)
- Changelog status (`[Unreleased]` section presence in root or per-package CHANGELOG.md)
- Flags: unreleased changes, tag with no registry version, registry version with no tag,
  deprecated or unlisted packages, version family drift across multi-package projects.
"""

from __future__ import annotations

import json
import pathlib
import re
from collections.abc import Callable, Sequence
from dataclasses import dataclass, field
from typing import Annotated, Protocol

import httpx
import typer
from pydantic import Field
from rich.console import Console
from rich.table import Table

from rig import proc, roots
from rig.typed_json import Lenient, try_decode_json
from rig.workspace.engine import config_file_path
from rig.workspace.package_versions import version_key
from rig.workspace.packages import (
    PackageBinding,
    PackageSpec,
    load_package_bindings,
)

_UNRELEASED_HEADER = re.compile(r"^##\s+\[?unreleased\]?", re.IGNORECASE)
_ANY_H2_OR_H1 = re.compile(r"^#{1,2}\s+")
_COMMENT_RE = re.compile(r"<!--.*?-->", re.DOTALL)


# ---------------------------------------------------------------- models


@dataclass(slots=True, frozen=True)
class NugetCatalogEntry:
    id: Annotated[str, Field(alias="@id")]
    version: str
    listed: bool = True
    deprecation: Lenient[object] = None


@dataclass(slots=True, frozen=True)
class NugetRegistrationLeaf:
    catalog_entry: Annotated[NugetCatalogEntry, Field(alias="catalogEntry")]


@dataclass(slots=True, frozen=True)
class NugetRegistrationPage:
    id: Annotated[str, Field(alias="@id")]
    items: Lenient[tuple[NugetRegistrationLeaf, ...]] = None


@dataclass(slots=True, frozen=True)
class NugetRegistrationIndex:
    items: tuple[NugetRegistrationPage, ...] = ()


@dataclass(slots=True, frozen=True)
class NugetFlatContainer:
    versions: tuple[str, ...] = ()


@dataclass(slots=True, frozen=True)
class NpmVersionMeta:
    deprecated: Lenient[str] = None


@dataclass(slots=True, frozen=True)
class NpmDistTags:
    latest: Lenient[str] = None


@dataclass(slots=True, frozen=True)
class NpmPackument:
    dist_tags: Annotated[Lenient[NpmDistTags], Field(alias="dist-tags")] = None
    versions: Lenient[dict[str, Lenient[NpmVersionMeta]]] = None


@dataclass(slots=True, frozen=True)
class RegistryPackageInfo:
    latest_version: str | None
    all_versions: tuple[str, ...] = ()
    unlisted: bool = False
    deprecated: bool = False
    deprecation_reason: str | None = None
    lookup_failed: bool = False
    """The registry could not be read (network error, 429, 5xx); nothing here is a confirmed release state."""


@dataclass(slots=True, frozen=True)
class MatchedTag:
    tag: str
    version: str


@dataclass(slots=True, frozen=True)
class ChangelogResult:
    found: bool
    has_unreleased: bool


@dataclass(slots=True, frozen=True)
class PackageStatusRow:
    project: str
    package: str
    registry: str
    manifest: str
    registry_version: str | None
    latest_tag: str | None
    tag_version: str | None
    changelog_found: bool
    changelog_unreleased: bool
    unlisted: bool
    deprecated: bool
    flags: tuple[str, ...]
    status: str

    def to_json(self) -> dict[str, object]:
        return {
            "project": self.project,
            "package": self.package,
            "registry": self.registry,
            "manifest": self.manifest,
            "registry_version": self.registry_version,
            "latest_tag": self.latest_tag,
            "tag_version": self.tag_version,
            "changelog_found": self.changelog_found,
            "changelog_unreleased": self.changelog_unreleased,
            "unlisted": self.unlisted,
            "deprecated": self.deprecated,
            "flags": list(self.flags),
            "status": self.status,
        }


# ---------------------------------------------------------------- protocols


class RegistryClient(Protocol):
    def fetch_package_info(self, registry: str, package_name: str) -> RegistryPackageInfo: ...


class GitReader(Protocol):
    def read_tags(self, repo_path: pathlib.Path) -> tuple[str, ...]: ...


class ChangelogReader(Protocol):
    def check_changelog(self, clone_path: pathlib.Path, manifest_path: str) -> ChangelogResult: ...


# ---------------------------------------------------------------- changelog


def changelog_has_unreleased(content: str) -> bool:
    """Return True if content has entries under an [Unreleased] section."""
    cleaned = _COMMENT_RE.sub("", content)
    lines = cleaned.splitlines()
    in_unreleased = False
    unreleased_lines: list[str] = []
    for line in lines:
        stripped = line.strip()
        if _UNRELEASED_HEADER.match(stripped):
            in_unreleased = True
            continue
        if in_unreleased:
            if _ANY_H2_OR_H1.match(stripped):
                break
            unreleased_lines.append(stripped)

    if not in_unreleased:
        return False

    substantive = [
        ln
        for ln in unreleased_lines
        if ln and not ln.startswith("---") and not ln.startswith("***") and not ln.startswith("___")
    ]
    real_content = [ln for ln in substantive if not ln.startswith("### ")]
    return len(real_content) > 0


@dataclass(slots=True, frozen=True)
class LiveChangelogReader:
    def check_changelog(self, clone_path: pathlib.Path, manifest_path: str) -> ChangelogResult:
        if not clone_path.is_dir():
            return ChangelogResult(found=False, has_unreleased=False)
        # Candidate 1: next to manifest
        p1 = clone_path / pathlib.Path(manifest_path).parent / "CHANGELOG.md"
        if p1.is_file():
            try:
                unrel = changelog_has_unreleased(p1.read_text(encoding="utf-8"))
                return ChangelogResult(found=True, has_unreleased=unrel)
            except OSError:
                return ChangelogResult(found=True, has_unreleased=False)
        # Candidate 2: root
        p2 = clone_path / "CHANGELOG.md"
        if p2.is_file():
            try:
                unrel = changelog_has_unreleased(p2.read_text(encoding="utf-8"))
                return ChangelogResult(found=True, has_unreleased=unrel)
            except OSError:
                return ChangelogResult(found=True, has_unreleased=False)
        return ChangelogResult(found=False, has_unreleased=False)


# ---------------------------------------------------------------- git tags


def find_matching_tags(all_tags: Sequence[str], package_name: str) -> tuple[MatchedTag, ...]:
    """Find tags for package, supporting both <PackageId>/v<version> and v<version> styles."""
    prefix = f"{package_name.lower()}/v"
    package_scoped: list[MatchedTag] = []
    global_scoped: list[MatchedTag] = []

    for tag in all_tags:
        t_low = tag.lower()
        if t_low.startswith(prefix):
            ver = tag[len(prefix) :].strip()
            if ver and ver[0].isdigit():
                package_scoped.append(MatchedTag(tag=tag, version=ver))
        elif t_low.startswith("v"):
            ver = tag[1:].strip()
            if ver and ver[0].isdigit():
                global_scoped.append(MatchedTag(tag=tag, version=ver))

    return tuple(package_scoped if package_scoped else global_scoped)


@dataclass(slots=True, frozen=True)
class LiveGitReader:
    def read_tags(self, repo_path: pathlib.Path) -> tuple[str, ...]:
        if not repo_path.is_dir():
            return ()
        return tuple(proc.git_lines("tag", "-l", cwd=repo_path) or ())


# ---------------------------------------------------------------- registry clients


def _default_http_client() -> httpx.Client:
    return httpx.Client(timeout=15.0, follow_redirects=True)


_NOT_PUBLISHED = RegistryPackageInfo(latest_version=None)
_LOOKUP_FAILED = RegistryPackageInfo(latest_version=None, lookup_failed=True)


@dataclass
class LiveRegistryClient:
    client: httpx.Client = field(default_factory=_default_http_client)
    _cache: dict[str, RegistryPackageInfo] = field(default_factory=dict)

    def fetch_package_info(self, registry: str, package_name: str) -> RegistryPackageInfo:
        cache_key = f"{registry}:{package_name.lower()}"
        if cache_key in self._cache:
            return self._cache[cache_key]

        if registry == "nuget":
            info = self._fetch_nuget(package_name)
        elif registry == "npm":
            info = self._fetch_npm(package_name)
        else:
            info = RegistryPackageInfo(latest_version=None)

        self._cache[cache_key] = info
        return info

    def _fetch_nuget(self, name: str) -> RegistryPackageInfo:
        # Flat container: list all versions
        flat_url = f"https://api.nuget.org/v3-flatcontainer/{name.lower()}/index.json"
        try:
            res = self.client.get(flat_url)
        except httpx.HTTPError:
            return _LOOKUP_FAILED

        if not res.is_success:
            return _NOT_PUBLISHED if res.status_code == 404 else _LOOKUP_FAILED

        flat = try_decode_json(NugetFlatContainer, res.content)
        versions = flat.versions if flat else ()
        if not versions:
            return RegistryPackageInfo(latest_version=None)

        latest = max(versions, key=version_key)

        # Registration API: check listed & deprecation status
        reg_url = f"https://api.nuget.org/v3/registration5-gz-semver2/{name.lower()}/index.json"
        unlisted = False
        deprecated = False
        try:
            r_res = self.client.get(reg_url)
            if r_res.is_success:
                reg_index = try_decode_json(NugetRegistrationIndex, r_res.content)
                target_leaf: NugetCatalogEntry | None = None
                for page in reversed(reg_index.items if reg_index else ()):
                    leaves = page.items
                    if leaves is None:
                        try:
                            p_res = self.client.get(page.id)
                            if p_res.is_success:
                                p_page = try_decode_json(NugetRegistrationPage, p_res.content)
                                leaves = p_page.items if p_page else None
                        except httpx.HTTPError:
                            leaves = None
                    for leaf in reversed(leaves or ()):
                        if leaf.catalog_entry.version.lower() == latest.lower():
                            target_leaf = leaf.catalog_entry
                            break
                        if target_leaf is None:
                            target_leaf = leaf.catalog_entry
                    if target_leaf is not None and target_leaf.version.lower() == latest.lower():
                        break
                if target_leaf is not None:
                    unlisted = not target_leaf.listed
                    deprecated = target_leaf.deprecation is not None
        except httpx.HTTPError:
            pass

        return RegistryPackageInfo(
            latest_version=latest,
            all_versions=versions,
            unlisted=unlisted,
            deprecated=deprecated,
        )

    def _fetch_npm(self, name: str) -> RegistryPackageInfo:
        url = f"https://registry.npmjs.org/{name.replace('/', '%2f')}"
        try:
            res = self.client.get(url)
        except httpx.HTTPError:
            return _LOOKUP_FAILED

        if not res.is_success:
            return _NOT_PUBLISHED if res.status_code == 404 else _LOOKUP_FAILED

        packument = try_decode_json(NpmPackument, res.content)
        if not packument or not packument.versions:
            return RegistryPackageInfo(latest_version=None)

        all_vers = tuple(packument.versions.keys())
        dist_latest = packument.dist_tags.latest if packument.dist_tags else None
        latest = dist_latest if dist_latest and dist_latest in all_vers else max(all_vers, key=version_key)

        version_meta = packument.versions.get(latest)
        deprecated_reason = version_meta.deprecated if version_meta else None
        deprecated = bool(deprecated_reason)

        return RegistryPackageInfo(
            latest_version=latest,
            all_versions=all_vers,
            unlisted=False,
            deprecated=deprecated,
            deprecation_reason=deprecated_reason,
        )


# ---------------------------------------------------------------- clone resolver


def resolve_clone_path(
    project_name: str,
    orca_group: str | None = None,
    code_root: pathlib.Path | None = None,
) -> pathlib.Path | None:
    """Find the local clone for a managed project."""
    top_group = orca_group.split("/")[0] if orca_group else ""
    estate_code = roots.code_root()

    candidates: list[pathlib.Path | None] = []
    if code_root:
        root = code_root.expanduser().resolve()
        candidates.extend([root / project_name, root / top_group / project_name if top_group else None])
        if root.is_dir():
            for child in root.iterdir():
                if child.is_dir() and child.name == project_name:
                    candidates.append(child)

    candidates.extend(
        [
            estate_code / top_group / project_name if top_group else None,
            estate_code / "personal" / project_name,
            estate_code / project_name,
        ]
    )

    for c in candidates:
        if c and c.is_dir():
            return c.resolve()

    return None


# ---------------------------------------------------------------- dashboard engine


def _load_project_bindings(
    config_path: pathlib.Path,
    filter_project: str | None = None,
) -> tuple[PackageBinding, ...]:
    bindings = load_package_bindings(config_path)
    if filter_project:
        fp_lower = filter_project.lower()
        bindings = [b for b in bindings if b.project.lower() == fp_lower]
    return tuple(bindings)


def collect_package_dashboard(
    bindings: Sequence[PackageBinding],
    registry_client: RegistryClient,
    git_reader: GitReader,
    changelog_reader: ChangelogReader,
    *,
    code_root: pathlib.Path | None = None,
    group_map: Callable[[str], str | None] | None = None,
) -> tuple[PackageStatusRow, ...]:
    """Collect dashboard status rows for declared package bindings."""
    initial_rows: list[PackageStatusRow] = []

    for b in bindings:
        spec: PackageSpec = b.spec
        orca_group = group_map(b.project) if group_map else None
        clone_path = resolve_clone_path(b.project, orca_group, code_root=code_root)

        # 1. Registry info
        reg_info = registry_client.fetch_package_info(spec.registry, spec.name)

        # 2. Git tags
        tags = git_reader.read_tags(clone_path) if clone_path else ()
        matching_tags = find_matching_tags(tags, spec.name)
        latest_tag_match: MatchedTag | None = None
        if matching_tags:
            latest_tag_match = max(matching_tags, key=lambda m: version_key(m.version))

        latest_tag = latest_tag_match.tag if latest_tag_match else None
        tag_version = latest_tag_match.version if latest_tag_match else None
        tag_versions = tuple(m.version for m in matching_tags)

        # 3. Changelog
        cl_res = (
            changelog_reader.check_changelog(clone_path, spec.manifest)
            if clone_path
            else ChangelogResult(found=False, has_unreleased=False)
        )

        # 4. Flags
        flags: list[str] = []
        if cl_res.has_unreleased:
            flags.append("unreleased-changes")

        if reg_info.lookup_failed:
            flags.append("registry-unavailable")
        elif tag_version is not None and (reg_info.latest_version is None or tag_version not in reg_info.all_versions):
            flags.append("tag-without-registry")

        if reg_info.latest_version is not None and reg_info.latest_version not in tag_versions:
            flags.append("registry-without-tag")

        if reg_info.unlisted:
            flags.append("unlisted")

        if reg_info.deprecated:
            flags.append("deprecated")

        initial_rows.append(
            PackageStatusRow(
                project=b.project,
                package=spec.name,
                registry=spec.registry,
                manifest=spec.manifest,
                registry_version=reg_info.latest_version,
                latest_tag=latest_tag,
                tag_version=tag_version,
                changelog_found=cl_res.found,
                changelog_unreleased=cl_res.has_unreleased,
                unlisted=reg_info.unlisted,
                deprecated=reg_info.deprecated,
                flags=tuple(flags),
                status="",
            )
        )

    # 5. Check version family drift across packages in the same project
    project_rows: dict[str, list[PackageStatusRow]] = {}
    for row in initial_rows:
        project_rows.setdefault(row.project, []).append(row)

    final_rows: list[PackageStatusRow] = []
    for rows in project_rows.values():
        drift = False
        if len(rows) > 1:
            distinct_versions = {r.registry_version for r in rows if "registry-unavailable" not in r.flags}
            if len(distinct_versions) > 1:
                drift = True

        for r in rows:
            flags = list(r.flags)
            if drift:
                flags.append("version-family-drift")

            if not flags:
                status = "unpublished" if r.registry_version is None and r.latest_tag is None else "synced"
            else:
                status = ", ".join(flags)

            final_rows.append(
                PackageStatusRow(
                    project=r.project,
                    package=r.package,
                    registry=r.registry,
                    manifest=r.manifest,
                    registry_version=r.registry_version,
                    latest_tag=r.latest_tag,
                    tag_version=r.tag_version,
                    changelog_found=r.changelog_found,
                    changelog_unreleased=r.changelog_unreleased,
                    unlisted=r.unlisted,
                    deprecated=r.deprecated,
                    flags=tuple(flags),
                    status=status,
                )
            )

    return tuple(final_rows)


@dataclass(slots=True, frozen=True)
class _OrcaConfig:
    group: Lenient[str] = None


@dataclass(slots=True, frozen=True)
class _ProjectConfig:
    name: str
    orca: Lenient[_OrcaConfig] = None


@dataclass(slots=True, frozen=True)
class _ConfigDoc:
    projects: tuple[_ProjectConfig, ...] = ()


def load_project_orca_groups(config_path: pathlib.Path) -> dict[str, str]:
    """Load project -> orca.group mapping from managed-projects.json."""
    try:
        doc = try_decode_json(_ConfigDoc, config_path.read_text(encoding="utf-8"))
        if not doc:
            return {}
        return {p.name: p.orca.group for p in doc.projects if p.orca and p.orca.group}
    except OSError:
        return {}


def scan_estate_packages(
    *,
    config: str | None = None,
    project: str | None = None,
    code_root: str | None = None,
    registry_client: RegistryClient | None = None,
    git_reader: GitReader | None = None,
    changelog_reader: ChangelogReader | None = None,
) -> tuple[PackageStatusRow, ...]:
    """High-level runner to scan estate packages."""
    cfg = pathlib.Path(config).resolve() if config else config_file_path()
    root_path = pathlib.Path(code_root).expanduser().resolve() if code_root else None

    bindings = _load_project_bindings(cfg, filter_project=project)
    group_map = load_project_orca_groups(cfg)

    client = registry_client or LiveRegistryClient()
    git = git_reader or LiveGitReader()
    changelog = changelog_reader or LiveChangelogReader()

    return collect_package_dashboard(
        bindings,
        client,
        git,
        changelog,
        code_root=root_path,
        group_map=group_map.get,
    )


_STATUS_BADGES: dict[str, str] = {
    "unreleased-changes": "[yellow]unreleased[/]",
    "tag-without-registry": "[bold yellow]unreleased tag[/]",
    "registry-without-tag": "[bold red]no tag[/]",
    "unlisted": "[red]unlisted[/]",
    "deprecated": "[red]deprecated[/]",
    "version-family-drift": "[yellow]drift[/]",
    "registry-unavailable": "[dim]registry unavailable[/]",
}


def run_packages(
    *,
    root: str | None = None,
    config: str | None = None,
    project: str | None = None,
    json_output: bool = False,
    console: Console | None = None,
    registry_client: RegistryClient | None = None,
    git_reader: GitReader | None = None,
    changelog_reader: ChangelogReader | None = None,
) -> None:
    """Execute `workspace packages dashboard` and output table or JSON."""
    active_console = console or Console()

    rows = scan_estate_packages(
        config=config,
        project=project,
        code_root=root,
        registry_client=registry_client,
        git_reader=git_reader,
        changelog_reader=changelog_reader,
    )

    if json_output:
        typer.echo(json.dumps([r.to_json() for r in rows], indent=2))
        return

    table = Table(title=f"Estate — Published Packages ({len(rows)})", title_style="bold", expand=True)
    table.add_column("project", style="bold", no_wrap=True)
    table.add_column("package", style="bold cyan", no_wrap=True)
    table.add_column("registry", style="magenta", no_wrap=True)
    table.add_column("latest", style="bold", no_wrap=True)
    table.add_column("tag", style="dim", no_wrap=True)
    table.add_column("changelog", no_wrap=True)
    table.add_column("status", no_wrap=True)

    for r in rows:
        reg_ver = r.registry_version or "—"
        tag_str = r.latest_tag or "—"
        if r.changelog_unreleased:
            cl_str = "[yellow]unreleased[/]"
        elif r.changelog_found:
            cl_str = "[dim]clean[/]"
        else:
            cl_str = "[dim]—[/]"

        if not r.flags:
            status_str = "[dim]unpublished[/]" if r.status == "unpublished" else "[bold green]synced[/]"
        else:
            badges = [_STATUS_BADGES.get(f, f"[{f}]") for f in r.flags]
            status_str = " ".join(badges)

        table.add_row(
            r.project,
            r.package,
            r.registry,
            reg_ver,
            tag_str,
            cl_str,
            status_str,
        )

    active_console.print(table)

    synced_count = sum(1 for r in rows if r.status == "synced")
    unpublished_count = sum(1 for r in rows if r.status == "unpublished")
    flagged_count = len(rows) - synced_count - unpublished_count

    active_console.print(
        f"\n[bold]{len(rows)}[/bold] packages · "
        f"[green]{synced_count} synced[/green] · "
        f"[yellow]{flagged_count} flagged[/yellow] · "
        f"[dim]{unpublished_count} unpublished[/dim]"
    )
    active_console.print("\n[dim]`workspace packages dashboard --json` for machine-readable output[/dim]")
