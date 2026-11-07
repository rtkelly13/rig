"""Live sources for `workspace packages audit`: public registry endpoints and the `gh` CLI.

Every payload is decoded into a named shape here, at the boundary. The npm and
NuGet shapes below are formats we do not own, so their fields are ``Lenient``:
an odd field costs that field, not the package's audit.

Two fields are observed rather than documented, and are read as hints:

- npm ``versions[v]._npmUser.trustedPublisher``: present on versions published
  through OIDC trusted publishing.
- the NuGet catalog leaf's ``repository``: copied from the nuspec.

NuGet resources are resolved from the V3 service index, as the protocol asks,
rather than hardcoded. Search's ``owners`` is reindexed on a publish, not on an
owner change, so it can lag the package's Manage page.
"""

from __future__ import annotations

import base64
import subprocess
from collections.abc import Callable
from dataclasses import dataclass, field
from datetime import datetime
from typing import Annotated

import httpx
from pydantic import Field

from rig import proc
from rig.typed_json import Lenient, load_json, try_decode, try_decode_json
from rig.workspace.package_versions import is_stable_version, version_key
from rig.workspace.packages import (
    PackageSpec,
    Provenance,
    Release,
    RepoIdentity,
    TrustConfig,
)

type NpmRunner = Callable[[list[str]], subprocess.CompletedProcess[str]]

# ---------------------------------------------------------------- GitHub


@dataclass(slots=True, frozen=True)
class _GhRepo:
    full_name: str
    id: int
    owner_id: int


def _gh(args: list[str]) -> str | None:
    res = proc.gh("api", *args)
    return res.out if res.ok else None


# ---------------------------------------------------------------- npm


@dataclass(slots=True, frozen=True)
class _TrustedPublisher:
    id: Lenient[str] = None


@dataclass(slots=True, frozen=True)
class _NpmUser:
    trusted_publisher: Annotated[Lenient[_TrustedPublisher], Field(alias="trustedPublisher")] = None


@dataclass(slots=True, frozen=True)
class _AttestationRef:
    url: str


@dataclass(slots=True, frozen=True)
class _Dist:
    attestations: Lenient[_AttestationRef] = None


@dataclass(slots=True, frozen=True)
class _NpmVersion:
    npm_user: Annotated[Lenient[_NpmUser], Field(alias="_npmUser")] = None
    dist: Lenient[_Dist] = None


@dataclass(slots=True, frozen=True)
class _DistTags:
    latest: Lenient[str] = None


@dataclass(slots=True, frozen=True)
class _Packument:
    dist_tags: Annotated[Lenient[_DistTags], Field(alias="dist-tags")] = None
    versions: Lenient[dict[str, Lenient[_NpmVersion]]] = None
    time: Lenient[dict[str, Lenient[datetime]]] = None


@dataclass(slots=True, frozen=True)
class _ProvenanceWorkflow:
    repository: str = ""
    path: str = ""


@dataclass(slots=True, frozen=True)
class _ExternalParameters:
    workflow: _ProvenanceWorkflow


@dataclass(slots=True, frozen=True)
class _GithubParameters:
    repository_id: Lenient[int] = None
    repository_owner_id: Lenient[int] = None


@dataclass(slots=True, frozen=True)
class _InternalParameters:
    github: Lenient[_GithubParameters] = None


@dataclass(slots=True, frozen=True)
class _BuildDefinition:
    external_parameters: Annotated[_ExternalParameters, Field(alias="externalParameters")]
    internal_parameters: Annotated[Lenient[_InternalParameters], Field(alias="internalParameters")] = None


@dataclass(slots=True, frozen=True)
class _Predicate:
    build_definition: Annotated[_BuildDefinition, Field(alias="buildDefinition")]


@dataclass(slots=True, frozen=True)
class _Statement:
    predicate: _Predicate


@dataclass(slots=True, frozen=True)
class _Envelope:
    payload: str


@dataclass(slots=True, frozen=True)
class _Bundle:
    dsse_envelope: Annotated[_Envelope, Field(alias="dsseEnvelope")]


@dataclass(slots=True, frozen=True)
class _Attestation:
    predicate_type: Annotated[str, Field(alias="predicateType")] = ""
    bundle: Lenient[_Bundle] = None


@dataclass(slots=True, frozen=True)
class _Attestations:
    attestations: tuple[Lenient[_Attestation], ...] = ()


def parse_npm_provenance(doc: object) -> Provenance | None:
    """The SLSA statement in ``/-/npm/v1/attestations/<pkg>@<ver>``."""
    found = try_decode(_Attestations, doc)
    for att in found.attestations if found else ():
        if att is None or att.bundle is None or "slsa.dev/provenance" not in att.predicate_type:
            continue
        statement = try_decode_json(_Statement, base64.b64decode(att.bundle.dsse_envelope.payload))
        if statement is None:
            return None
        build = statement.predicate.build_definition
        github = build.internal_parameters.github if build.internal_parameters else None
        return Provenance(
            repository_url=build.external_parameters.workflow.repository,
            workflow_path=build.external_parameters.workflow.path,
            repository_id=github.repository_id if github else None,
            owner_id=github.repository_owner_id if github else None,
        )
    return None


@dataclass(slots=True, frozen=True)
class _WorkflowRef:
    file: str = ""


@dataclass(slots=True, frozen=True)
class _TrustClaims:
    repository: str = ""
    workflow_ref: Lenient[_WorkflowRef] = None
    environment: Lenient[str] = None


@dataclass(slots=True, frozen=True)
class _TrustEntry:
    claims: Lenient[_TrustClaims] = None


def parse_npm_trust(text: str) -> list[TrustConfig]:
    """``npm trust list --json``: one entry or a list of them, each carrying the claims it was created with."""
    entries = try_decode_json(tuple[_TrustEntry, ...], text)
    if entries is None:
        single = try_decode_json(_TrustEntry, text)
        entries = (single,) if single else ()
    return [
        TrustConfig(
            repository=e.claims.repository,
            file=e.claims.workflow_ref.file if e.claims.workflow_ref else "",
            environment=e.claims.environment,
        )
        for e in entries
        if e.claims is not None
    ]


# ---------------------------------------------------------------- NuGet


NUGET_SERVICE_INDEX = "https://api.nuget.org/v3/index.json"
_SEVERITY: tuple[str, ...] = ("low", "moderate", "high", "critical")


@dataclass(slots=True, frozen=True)
class _ServiceResource:
    id: Annotated[str, Field(alias="@id")]
    type: Annotated[str, Field(alias="@type")]


@dataclass(slots=True, frozen=True)
class _ServiceIndex:
    resources: tuple[Lenient[_ServiceResource], ...] = ()

    def find(self, kind: str) -> str | None:
        """The first resource of exactly ``kind``; nuget.org lists each search host twice."""
        return next((r.id for r in self.resources if r is not None and r.type == kind), None)


@dataclass(slots=True, frozen=True)
class _NugetEndpoints:
    registrations: str
    flat: str
    search: str


@dataclass(slots=True, frozen=True)
class _FlatIndex:
    """``{PackageBaseAddress}{id}/index.json``: every version, unlisted ones too; a 404 means no such package."""

    versions: tuple[str, ...] = ()


@dataclass(slots=True, frozen=True)
class _SearchHit:
    id: str = ""
    owners: Lenient[tuple[str, ...] | str] = None


@dataclass(slots=True, frozen=True)
class _SearchResults:
    data: tuple[Lenient[_SearchHit], ...] = ()


@dataclass(slots=True, frozen=True)
class _AlternatePackage:
    id: Lenient[str] = None


@dataclass(slots=True, frozen=True)
class _Deprecation:
    reasons: Lenient[tuple[str, ...]] = None
    message: Lenient[str] = None
    alternate: Annotated[Lenient[_AlternatePackage], Field(alias="alternatePackage")] = None

    def summary(self) -> str:
        alternate = f"; use {self.alternate.id}" if self.alternate and self.alternate.id else ""
        message = f" ({self.message})" if self.message else ""
        return f"{', '.join(self.reasons or ()) or 'no reason given'}{message}{alternate}"


@dataclass(slots=True, frozen=True)
class _Vulnerability:
    advisory_url: Annotated[Lenient[str], Field(alias="advisoryUrl")] = None
    severity: Lenient[str] = None

    def summary(self) -> str:
        level = int(self.severity) if self.severity and self.severity.isdigit() else -1
        name = _SEVERITY[level] if 0 <= level < len(_SEVERITY) else "unknown"
        return f"{name} {self.advisory_url or ''}".strip()


@dataclass(slots=True, frozen=True)
class _CatalogSummary:
    id: Annotated[str, Field(alias="@id")]
    version: str
    listed: bool = True
    published: Lenient[datetime] = None
    deprecation: Lenient[_Deprecation] = None
    vulnerabilities: Lenient[tuple[Lenient[_Vulnerability], ...]] = None


@dataclass(slots=True, frozen=True)
class _RegistrationLeaf:
    catalog_entry: Annotated[_CatalogSummary, Field(alias="catalogEntry")]


@dataclass(slots=True, frozen=True)
class _RegistrationPage:
    id: Annotated[str, Field(alias="@id")]
    items: Lenient[tuple[_RegistrationLeaf, ...]] = None


@dataclass(slots=True, frozen=True)
class _RegistrationIndex:
    items: tuple[_RegistrationPage, ...] = ()


@dataclass(slots=True, frozen=True)
class _CatalogRepository:
    url: Lenient[str] = None


@dataclass(slots=True, frozen=True)
class _CatalogLeaf:
    repository: Lenient[_CatalogRepository] = None


# ---------------------------------------------------------------- fetcher


def _no_releases() -> dict[PackageSpec, Release | None]:
    return {}


def _client() -> httpx.Client:
    return httpx.Client(timeout=20.0, follow_redirects=True)


def _run_npm(cmd: list[str]) -> subprocess.CompletedProcess[str]:
    # Interactive by design: npm prompts for a 2FA code, so stdin and stderr stay attached.
    return subprocess.run(cmd, stdout=subprocess.PIPE, text=True, check=False)


@dataclass
class LiveFetcher:
    """Public registry endpoints over HTTP; GitHub through ``gh``, so its auth applies."""

    client: httpx.Client = field(default_factory=_client)
    run_npm: NpmRunner = _run_npm
    _releases: dict[PackageSpec, Release | None] = field(default_factory=_no_releases)
    _nuget_endpoints: _NugetEndpoints | None = None

    def github_repo(self, slug: str, /) -> RepoIdentity | None:
        out = _gh([f"repos/{slug}", "--jq", "{full_name, id, owner_id: .owner.id}"])
        repo = try_decode_json(_GhRepo, out) if out else None
        return RepoIdentity(full_name=repo.full_name, id=repo.id, owner_id=repo.owner_id) if repo else None

    def github_file(self, slug: str, path: str, /) -> str | None:
        return _gh([f"repos/{slug}/contents/{path}", "-H", "Accept: application/vnd.github.raw"])

    def workflow_conclusions(self, slug: str, workflow: str, limit: int, /) -> list[str] | None:
        out = _gh(
            [
                f"repos/{slug}/actions/workflows/{workflow}/runs?status=completed&per_page={limit}",
                "--jq",
                "[.workflow_runs[].conclusion]",
            ]
        )
        found = try_decode_json(tuple[Lenient[str], ...], out) if out else None
        return None if found is None else [c for c in found if c]

    def registry_release(self, spec: PackageSpec, /) -> Release | None:
        if spec not in self._releases:
            self._releases[spec] = self._npm(spec.name) if spec.registry == "npm" else self._nuget(spec.name)
        return self._releases[spec]

    def npm_trust(self, name: str, /) -> list[TrustConfig] | str:
        res = self.run_npm(["npm", "trust", "list", name, "--json"])
        if res.returncode != 0:
            return f"exit {res.returncode} (needs npm >= 11.15 and `npm login`)"
        return parse_npm_trust(res.stdout)

    def _get(self, url: str) -> object | None:
        res = self.client.get(url)
        return load_json(res.content) if res.is_success else None

    def _npm(self, name: str) -> Release | None:
        pkg = try_decode(_Packument, self._get(f"https://registry.npmjs.org/{name.replace('/', '%2f')}"))
        if pkg is None:
            return None
        versions = list(pkg.versions.keys()) if pkg.versions else []
        dist_latest = pkg.dist_tags.latest if pkg.dist_tags else None
        if dist_latest and dist_latest not in versions:
            versions.append(dist_latest)
        if not versions:
            return None

        latest = max(versions, key=version_key)
        stables = [v for v in versions if is_stable_version(v)]
        stable_ver = max(stables, key=version_key) if stables else None

        meta = (pkg.versions or {}).get(latest)
        if meta is None and dist_latest:
            meta = (pkg.versions or {}).get(dist_latest)
        user = meta.npm_user if meta else None
        att = meta.dist.attestations if meta and meta.dist else None
        return Release(
            version=latest,
            published=(pkg.time or {}).get(latest),
            trusted_publisher=None if user is None else user.trusted_publisher is not None,
            provenance=parse_npm_provenance(self._get(att.url)) if att else None,
            stable_version=stable_ver,
            stable_published=(pkg.time or {}).get(stable_ver) if stable_ver else None,
        )

    def _endpoints(self) -> _NugetEndpoints | None:
        if self._nuget_endpoints is None:
            index = try_decode(_ServiceIndex, self._get(NUGET_SERVICE_INDEX))
            registrations = index.find("RegistrationsBaseUrl/3.6.0") if index else None
            flat = index.find("PackageBaseAddress/3.0.0") if index else None
            search = index.find("SearchQueryService/3.5.0") if index else None
            if registrations and flat and search:
                self._nuget_endpoints = _NugetEndpoints(registrations, flat, search)
        return self._nuget_endpoints

    def _nuget_owners(self, search: str, name: str) -> tuple[str, ...] | None:
        url = f"{search}?q=packageid:{name}&prerelease=true&semVerLevel=2.0.0"
        found = try_decode(_SearchResults, self._get(url))
        hit = next((h for h in found.data if h and h.id.lower() == name.lower()), None) if found else None
        if hit is None or hit.owners is None:
            return None
        return (hit.owners,) if isinstance(hit.owners, str) else hit.owners

    def _nuget(self, name: str) -> Release | None:
        endpoints = self._endpoints()
        if endpoints is None:
            return None
        flat = try_decode(_FlatIndex, self._get(f"{endpoints.flat}{name.lower()}/index.json"))
        if flat is None or not flat.versions:
            return None
        owners = self._nuget_owners(endpoints.search, name)
        index = try_decode(_RegistrationIndex, self._get(f"{endpoints.registrations}{name.lower()}/index.json"))
        if index is None:
            return None

        latest_entry: _CatalogSummary | None = None
        stable_entry: _CatalogSummary | None = None

        for page in reversed(index.items):
            leaves = page.items
            if leaves is None:
                fetched = try_decode(_RegistrationPage, self._get(page.id))
                leaves = fetched.items if fetched else None
            listed = [leaf.catalog_entry for leaf in leaves or () if leaf.catalog_entry.listed]
            for entry in reversed(listed):
                if latest_entry is None:
                    latest_entry = entry
                if stable_entry is None and is_stable_version(entry.version):
                    stable_entry = entry
                if stable_entry is not None:
                    break
            if latest_entry is not None and stable_entry is not None:
                break

        if latest_entry is None:
            # Every version is unlisted: the package exists, so a policy still has to cover it.
            return Release(max(flat.versions, key=version_key), None, listed=False, owners=owners)

        leaf = try_decode(_CatalogLeaf, self._get(latest_entry.id))
        repo = leaf.repository if leaf else None
        return Release(
            version=latest_entry.version,
            published=latest_entry.published,
            source_repo_url=repo.url if repo else None,
            stable_version=stable_entry.version if stable_entry else None,
            stable_published=stable_entry.published if stable_entry else None,
            owners=owners,
            deprecation=latest_entry.deprecation.summary() if latest_entry.deprecation else None,
            vulnerabilities=tuple(v.summary() for v in latest_entry.vulnerabilities or () if v is not None),
        )
