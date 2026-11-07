"""Tests for `workspace packages audit`: package <-> repo bindings."""

from __future__ import annotations

import base64
import json
import pathlib
import subprocess
from dataclasses import dataclass, field
from datetime import UTC, datetime

import httpx
import pytest
from typer.testing import CliRunner

from rig.typed_json import decode_json
from rig.workspace import packages_cli
from rig.workspace.cli import app
from rig.workspace.package_sources import LiveFetcher, parse_npm_provenance, parse_npm_trust
from rig.workspace.package_versions import is_ahead, is_stable_version
from rig.workspace.packages import (
    Finding,
    ManifestFields,
    PackageBinding,
    PackageSpec,
    PackageSyncStatus,
    Provenance,
    Release,
    RepoIdentity,
    Status,
    TrustConfig,
    _version_key,
    audit_packages,
    consecutive_failures,
    format_relative_age,
    load_package_bindings,
    manifest_fields,
    repo_slug,
    track_package,
)

pytestmark = pytest.mark.ci

NOW = datetime(2026, 10, 16, tzinfo=UTC)
LABS = RepoIdentity(full_name="rtkelly-labs/design-system", id=1313191156, owner_id=330611343)
OIDC_WORKFLOW = "permissions:\n  contents: read\n  id-token: write\n"
NPM = PackageSpec(registry="npm", name="@s/pkg", manifest="package.json", workflow="publish.yml")
NUGET = PackageSpec(registry="nuget", name="Pkg", manifest="Directory.Build.props", workflow="release.yml")


def _manifest(url: str, version: str = "0.12.0") -> str:
    return json.dumps({"name": "@s/pkg", "version": version, "repository": {"type": "git", "url": url}})


def _release(owner_id: int = LABS.owner_id, repo: str = "rtkelly-labs/design-system") -> Release:
    prov = Provenance(f"https://github.com/{repo}", ".github/workflows/publish.yml", LABS.id, owner_id)
    return Release(
        version="0.12.0", published=datetime(2026, 9, 25, tzinfo=UTC), trusted_publisher=True, provenance=prov
    )


@dataclass
class FakeFetcher:
    repo: RepoIdentity | None = LABS
    files: dict[str, str] = field(default_factory=dict[str, str])
    release: Release | None = None
    runs: list[str] | None = field(default_factory=lambda: ["success"])
    trust: list[TrustConfig] | str = field(default_factory=list[TrustConfig])

    def github_repo(self, _slug: str, /) -> RepoIdentity | None:
        return self.repo

    def github_file(self, _slug: str, path: str, /) -> str | None:
        return self.files.get(path)

    def workflow_conclusions(self, _slug: str, _workflow: str, _limit: int, /) -> list[str] | None:
        return self.runs

    def registry_release(self, _spec: PackageSpec, /) -> Release | None:
        return self.release

    def npm_trust(self, _name: str, /) -> list[TrustConfig] | str:
        return self.trust


def _npm_fetcher(**overrides: object) -> FakeFetcher:
    fetcher = FakeFetcher(
        files={
            "package.json": _manifest("git+https://github.com/rtkelly-labs/design-system.git"),
            ".github/workflows/publish.yml": OIDC_WORKFLOW,
        },
        release=_release(),
    )
    for key, value in overrides.items():
        setattr(fetcher, key, value)
    return fetcher


def _audit(
    spec: PackageSpec,
    fetcher: FakeFetcher,
    repo: str = "git@github.com:rtkelly-labs/design-system.git",
    trust: bool = False,
) -> dict[str, Finding]:
    findings = audit_packages([PackageBinding("design-system", repo, spec)], fetcher, now=NOW, trust=trust)
    return {f.check: f for f in findings}


# ---------------------------------------------------------------- parsing


@pytest.mark.parametrize(
    "url",
    [
        "git@github.com:Owner/Repo.git",
        "https://github.com/Owner/Repo",
        "git+https://github.com/Owner/Repo.git",
        "https://github.com/Owner/Repo/",
    ],
)
def test_repo_slug_normalises_url_forms(url: str) -> None:
    assert repo_slug(url) == "Owner/Repo"


def test_repo_slug_rejects_non_github() -> None:
    assert repo_slug("https://gitlab.com/o/r") is None


def test_manifest_fields_reads_npm_string_and_object_repository() -> None:
    assert manifest_fields("npm", _manifest("https://github.com/o/r.git", "1.2.3")) == ManifestFields(
        "https://github.com/o/r.git", "1.2.3"
    )
    assert manifest_fields("npm", json.dumps({"repository": "github:o/r"})) == ManifestFields("github:o/r", None)


def test_manifest_fields_reads_msbuild_props() -> None:
    props = (
        "<Project><PropertyGroup><RepositoryUrl>https://github.com/o/r.git</RepositoryUrl></PropertyGroup></Project>"
    )
    assert manifest_fields("nuget", props) == ManifestFields("https://github.com/o/r.git", None)
    assert manifest_fields("nuget", "<VersionPrefix>0.2.0</VersionPrefix>") == ManifestFields(None, "0.2.0")


def test_is_ahead_orders_prereleases_before_releases() -> None:
    assert is_ahead("0.16.0", "0.12.0")
    assert is_ahead("0.12.0", "0.12.0-beta1")
    assert not is_ahead("0.12.0", "0.12.0")
    assert not is_ahead("0.9.0", "0.12.0")


def test_consecutive_failures_ignores_cancelled_and_stops_at_success() -> None:
    assert consecutive_failures(["failure", "cancelled", "failure", "success", "failure"]) == 2
    assert consecutive_failures(["cancelled", "success"]) == 0


def test_parse_npm_provenance_reads_slsa_statement() -> None:
    statement = {
        "predicate": {
            "buildDefinition": {
                "externalParameters": {
                    "workflow": {
                        "repository": "https://github.com/rtkelly13/design-system",
                        "path": ".github/workflows/p.yml",
                    }
                },
                "internalParameters": {"github": {"repository_id": "1313191156", "repository_owner_id": "11708460"}},
            }
        }
    }
    payload = base64.b64encode(json.dumps(statement).encode()).decode()
    doc = {
        "attestations": [
            {"predicateType": "https://github.com/npm/attestation/tree/main/specs/publish/v0.1"},
            {"predicateType": "https://slsa.dev/provenance/v1", "bundle": {"dsseEnvelope": {"payload": payload}}},
        ]
    }
    assert parse_npm_provenance(doc) == Provenance(
        "https://github.com/rtkelly13/design-system", ".github/workflows/p.yml", 1313191156, 11708460
    )


def test_parse_npm_trust_accepts_claims_shape() -> None:
    text = json.dumps(
        [{"id": "x", "type": "github", "claims": {"repository": "o/r", "workflow_ref": {"file": "p.yml"}}}]
    )
    assert parse_npm_trust(text) == [TrustConfig("o/r", "p.yml")]


# ---------------------------------------------------------------- config


def test_load_package_bindings_reads_projects_and_skips_disabled(tmp_path: pathlib.Path) -> None:
    cfg = tmp_path / "managed-projects.json"
    pkg = {"registry": "npm", "name": "@s/p", "manifest": "package.json", "workflow": "p.yml"}
    cfg.write_text(
        json.dumps(
            {
                "projects": [
                    {"name": "a", "repo": "git@github.com:o/a.git", "packages": [pkg]},
                    {"name": "b", "repo": "git@github.com:o/b.git", "enabled": False, "packages": [pkg]},
                    {"name": "c", "repo": "git@github.com:o/c.git"},
                ]
            }
        )
    )
    bindings = load_package_bindings(cfg)
    assert [(b.project, b.spec.name, b.spec.auth) for b in bindings] == [("a", "@s/p", "oidc")]


def test_load_package_bindings_rejects_unknown_registry(tmp_path: pathlib.Path) -> None:
    cfg = tmp_path / "managed-projects.json"
    bad = {"registry": "pypi", "name": "p", "manifest": "m", "workflow": "w"}
    cfg.write_text(json.dumps({"projects": [{"name": "a", "repo": "r", "packages": [bad]}]}))
    with pytest.raises(ValueError, match="pypi"):
        load_package_bindings(cfg)


def test_repo_config_declares_valid_bindings() -> None:
    cfg = pathlib.Path(__file__).resolve().parents[1] / "config" / "managed-projects.json"
    assert load_package_bindings(cfg)


# ---------------------------------------------------------------- npm checks


def test_healthy_npm_package_is_all_ok() -> None:
    found = _audit(NPM, _npm_fetcher())
    assert {c: f.status for c, f in found.items()} == {
        "repo": Status.OK,
        "manifest": Status.OK,
        "version": Status.OK,
        "workflow": Status.OK,
        "trusted-publisher": Status.OK,
        "provenance": Status.OK,
        "runs": Status.OK,
    }


def test_transfer_detected_from_provenance_owner_id() -> None:
    fetcher = _npm_fetcher(release=_release(owner_id=11708460, repo="rtkelly13/design-system"))
    finding = _audit(NPM, fetcher)["provenance"]
    assert finding.status is Status.FAIL
    assert "repository moved" in finding.detail
    assert "11708460 -> 330611343" in finding.detail
    assert "npm trust github @s/pkg --repo rtkelly-labs/design-system --file publish.yml" in finding.fix


def test_different_repository_id_is_not_reported_as_a_move() -> None:
    other = Release(
        version="0.12.0",
        published=None,
        provenance=Provenance("https://github.com/x/fork", "p.yml", 42, LABS.owner_id),
    )
    finding = _audit(NPM, _npm_fetcher(release=other))["provenance"]
    assert finding.status is Status.FAIL
    assert "different repository" in finding.detail


def test_stale_manifest_url_fails_for_npm() -> None:
    fetcher = _npm_fetcher()
    fetcher.files["package.json"] = _manifest("git+https://github.com/rtkelly13/design-system.git")
    finding = _audit(NPM, fetcher)["manifest"]
    assert finding.status is Status.FAIL
    assert "422" in finding.detail


def test_unpublished_versions_warn_with_age() -> None:
    fetcher = _npm_fetcher()
    fetcher.files["package.json"] = _manifest("https://github.com/rtkelly-labs/design-system", "0.16.0")
    finding = _audit(NPM, fetcher)["version"]
    assert finding.status is Status.WARN
    assert "0.16.0" in finding.detail and "21 days ago" in finding.detail


def test_stale_declared_repo_fails() -> None:
    found = _audit(NPM, _npm_fetcher(), repo="git@github.com:rtkelly13/design-system.git")
    assert found["repo"].status is Status.FAIL
    assert "redirects to rtkelly-labs/design-system" in found["repo"].detail


def test_unresolvable_repo_stops_the_audit() -> None:
    found = _audit(NPM, _npm_fetcher(repo=None))
    assert list(found) == ["repo"]
    assert found["repo"].status is Status.FAIL


def test_oidc_workflow_without_id_token_fails() -> None:
    fetcher = _npm_fetcher()
    fetcher.files[".github/workflows/publish.yml"] = "permissions:\n  contents: read\n"
    assert _audit(NPM, fetcher)["workflow"].status is Status.FAIL


def test_oidc_workflow_still_referencing_a_token_warns() -> None:
    fetcher = _npm_fetcher()
    fetcher.files[".github/workflows/publish.yml"] = OIDC_WORKFLOW + "NODE_AUTH_TOKEN: ${{ secrets.NPM_TOKEN }}\n"
    finding = _audit(NPM, fetcher)["workflow"]
    assert finding.status is Status.WARN
    assert "NPM_TOKEN" in finding.detail


def test_token_auth_warns_to_migrate() -> None:
    spec = PackageSpec(registry="npm", name="@s/pkg", manifest="package.json", workflow="publish.yml", auth="token")
    assert _audit(spec, _npm_fetcher())["workflow"].status is Status.WARN


def test_token_published_latest_warns_on_oidc_package() -> None:
    release = Release(version="0.12.0", published=None, trusted_publisher=False)
    found = _audit(NPM, _npm_fetcher(release=release))
    assert found["trusted-publisher"].status is Status.WARN
    assert found["provenance"].status is Status.WARN


def test_missing_package_points_at_bootstrap() -> None:
    finding = _audit(NPM, _npm_fetcher(release=None))["registry"]
    assert finding.status is Status.WARN
    assert "must exist before `npm trust`" in finding.fix


def test_failing_publish_runs_fail() -> None:
    finding = _audit(NPM, _npm_fetcher(runs=["failure", "failure", "success"]))["runs"]
    assert finding.status is Status.FAIL
    assert "last 2" in finding.detail


def test_trust_listing_only_read_when_asked() -> None:
    assert "trust" not in _audit(NPM, _npm_fetcher())


def test_trust_listing_naming_old_owner_fails() -> None:
    fetcher = _npm_fetcher(trust=[TrustConfig("rtkelly13/design-system", "publish.yml")])
    finding = _audit(NPM, fetcher, trust=True)["trust"]
    assert finding.status is Status.FAIL
    assert "rtkelly13/design-system" in finding.detail


def test_trust_listing_matching_repo_and_workflow_is_ok() -> None:
    fetcher = _npm_fetcher(trust=[TrustConfig("rtkelly-labs/design-system", ".github/workflows/publish.yml")])
    assert _audit(NPM, fetcher, trust=True)["trust"].status is Status.OK


def test_trust_listing_error_is_a_warning() -> None:
    assert _audit(NPM, _npm_fetcher(trust="exit 1"), trust=True)["trust"].status is Status.WARN


# ---------------------------------------------------------------- NuGet checks


def _nuget_fetcher(source: str | None) -> FakeFetcher:
    props = "<RepositoryUrl>https://github.com/rtkelly-labs/design-system.git</RepositoryUrl>"
    return FakeFetcher(
        files={"Directory.Build.props": props, ".github/workflows/release.yml": OIDC_WORKFLOW},
        release=Release(version="0.0.3", published=None, source_repo_url=source),
    )


def test_nuget_policy_checklist_only_under_trust_when_nothing_drifted() -> None:
    found = _audit(NUGET, _nuget_fetcher("https://github.com/rtkelly-labs/design-system.git"))
    assert found["provenance"].status is Status.OK
    assert "policy" not in found
    policy = _audit(NUGET, _nuget_fetcher("https://github.com/rtkelly-labs/design-system.git"), trust=True)["policy"]
    assert policy.status is Status.MANUAL
    assert "repository owner: rtkelly-labs" in policy.detail


def test_nuget_release_recording_old_owner_warns_to_recreate_policy() -> None:
    found = _audit(NUGET, _nuget_fetcher("https://github.com/rtkelly13/design-system.git"))
    assert found["provenance"].status is Status.WARN
    assert "nuget.org/account/trustedpublishing" in found["provenance"].fix
    assert "delete the old one" in found["provenance"].fix
    assert "Pkg provenance drifted" in found["policy"].detail


def test_nuget_stale_manifest_only_warns() -> None:
    fetcher = _nuget_fetcher(None)
    fetcher.files["Directory.Build.props"] = "<RepositoryUrl>https://github.com/rtkelly13/design-system</RepositoryUrl>"
    assert _audit(NUGET, fetcher)["manifest"].status is Status.WARN


# ---------------------------------------------------------------- CLI


def test_cli_strict_exits_non_zero_on_failure(tmp_path: pathlib.Path, monkeypatch: pytest.MonkeyPatch) -> None:
    cfg = tmp_path / "managed-projects.json"
    pkg = {"registry": "npm", "name": "@s/pkg", "manifest": "package.json", "workflow": "publish.yml"}
    cfg.write_text(
        json.dumps(
            {"projects": [{"name": "ds", "repo": "git@github.com:rtkelly-labs/design-system.git", "packages": [pkg]}]}
        )
    )
    monkeypatch.setattr(packages_cli, "LiveFetcher", lambda: _npm_fetcher(runs=["failure"]))

    result = CliRunner().invoke(app, ["packages", "audit", "--config", str(cfg), "--json", "--strict"])

    assert result.exit_code == 1
    findings = decode_json(list[dict[str, str]], result.stdout)
    assert {"check": "runs", "status": "fail"}.items() <= next(f for f in findings if f["check"] == "runs").items()


def test_cli_without_strict_exits_zero(tmp_path: pathlib.Path, monkeypatch: pytest.MonkeyPatch) -> None:
    cfg = tmp_path / "managed-projects.json"
    pkg = {"registry": "npm", "name": "@s/pkg", "manifest": "package.json", "workflow": "publish.yml"}
    cfg.write_text(
        json.dumps(
            {"projects": [{"name": "ds", "repo": "git@github.com:rtkelly-labs/design-system.git", "packages": [pkg]}]}
        )
    )
    monkeypatch.setattr(packages_cli, "LiveFetcher", lambda: _npm_fetcher(runs=["failure"]))

    result = CliRunner().invoke(app, ["packages", "audit", "--config", str(cfg)])

    assert result.exit_code == 0
    assert "--trust" in result.stdout


# ---------------------------------------------------------------- live fetcher, offline


def _npm_routes(owner_id: str) -> dict[str, object]:
    statement = {
        "predicate": {
            "buildDefinition": {
                "externalParameters": {
                    "workflow": {"repository": "https://github.com/rtkelly13/design-system", "path": "p.yml"}
                },
                "internalParameters": {"github": {"repository_id": "1313191156", "repository_owner_id": owner_id}},
            }
        }
    }
    payload = base64.b64encode(json.dumps(statement).encode()).decode()
    att_url = "https://registry.npmjs.org/-/npm/v1/attestations/@s%2fpkg@0.12.0"
    return {
        "https://registry.npmjs.org/@s%2fpkg": {
            "dist-tags": {"latest": "0.12.0"},
            "time": {"created": "2026-07-01T00:00:00Z", "0.12.0": "2026-09-25T12:44:02.600Z"},
            "versions": {
                "0.12.0": {
                    "_npmUser": {"name": "GitHub Actions", "trustedPublisher": {"id": "github", "oidcConfigId": "x"}},
                    "dist": {"attestations": {"url": att_url, "provenance": {}}},
                }
            },
        },
        att_url: {
            "attestations": [
                {"predicateType": "https://slsa.dev/provenance/v1", "bundle": {"dsseEnvelope": {"payload": payload}}}
            ]
        },
    }


NUGET_REGISTRATIONS = "https://api.nuget.org/v3/registration5-gz-semver2/"
NUGET_FLAT = "https://api.nuget.org/v3-flatcontainer/"
NUGET_SEARCH = "https://azuresearch-usnc.nuget.org/query"


def nuget_routes(name: str, versions: list[str], owners: list[str] | None = None) -> dict[str, object]:
    """The service index, flat container and search routes every NuGet lookup starts with."""
    resources = [
        {"@id": "https://api.nuget.org/v3/registration5-semver1/", "@type": "RegistrationsBaseUrl"},
        {"@id": NUGET_REGISTRATIONS, "@type": "RegistrationsBaseUrl/3.6.0"},
        {"@id": NUGET_FLAT, "@type": "PackageBaseAddress/3.0.0"},
        {"@id": NUGET_SEARCH, "@type": "SearchQueryService/3.5.0"},
        {"@id": "https://azuresearch-ussc.nuget.org/query", "@type": "SearchQueryService/3.5.0"},
    ]
    routes: dict[str, object] = {
        "https://api.nuget.org/v3/index.json": {"version": "3.0.0", "resources": resources},
        f"{NUGET_FLAT}{name.lower()}/index.json": {"versions": versions},
    }
    if owners is not None:
        search = f"{NUGET_SEARCH}?q=packageid:{name}&prerelease=true&semVerLevel=2.0.0"
        routes[search] = {"totalHits": 1, "data": [{"id": name, "version": versions[-1], "owners": owners}]}
    return routes


def _transport(routes: dict[str, object]) -> httpx.MockTransport:
    def handle(request: httpx.Request) -> httpx.Response:
        body = routes.get(str(request.url))
        return httpx.Response(404) if body is None else httpx.Response(200, json=body)

    return httpx.MockTransport(handle)


def test_live_fetcher_decodes_npm_packument_and_provenance() -> None:
    fetcher = LiveFetcher(client=httpx.Client(transport=_transport(_npm_routes("11708460"))))
    release = fetcher.registry_release(NPM)
    assert release == Release(
        version="0.12.0",
        published=datetime(2026, 9, 25, 12, 44, 2, 600000, tzinfo=UTC),
        trusted_publisher=True,
        provenance=Provenance("https://github.com/rtkelly13/design-system", "p.yml", 1313191156, 11708460),
    )


def test_live_fetcher_missing_npm_package_is_none() -> None:
    assert LiveFetcher(client=httpx.Client(transport=_transport({}))).registry_release(NPM) is None


def test_live_fetcher_follows_nuget_registration_pages_to_the_catalog() -> None:
    base = "https://api.nuget.org/v3/registration5-gz-semver2/pkg"
    leaf = "https://api.nuget.org/v3/catalog0/data/x/pkg.0.0.3.json"
    entry = {"@id": leaf, "version": "0.0.3", "listed": True, "published": "2026-09-05T21:11:52.6Z"}
    unlisted = {"@id": "u", "version": "0.0.4", "listed": False}
    routes: dict[str, object] = {
        f"{base}/index.json": {"items": [{"@id": f"{base}/page.json"}]},
        f"{base}/page.json": {
            "@id": f"{base}/page.json",
            "items": [{"catalogEntry": entry}, {"catalogEntry": unlisted}],
        },
        leaf: {"repository": {"type": "git", "url": "https://github.com/rtkelly13/Parquet.SourceGenerator.git"}},
        **nuget_routes("Pkg", ["0.0.3", "0.0.4"]),
    }
    release = LiveFetcher(client=httpx.Client(transport=_transport(routes))).registry_release(NUGET)
    assert release is not None
    assert release.version == "0.0.3"
    assert release.source_repo_url == "https://github.com/rtkelly13/Parquet.SourceGenerator.git"


def test_live_fetcher_reports_npm_trust_failure_without_raising() -> None:
    def refuse(cmd: list[str]) -> subprocess.CompletedProcess[str]:
        return subprocess.CompletedProcess(cmd, 1, "", "")

    assert "npm >= 11.15" in str(LiveFetcher(run_npm=refuse).npm_trust("@s/pkg"))


def test_track_package_synced() -> None:
    fetcher = _npm_fetcher(
        files={"package.json": _manifest("git+https://github.com/rtkelly-labs/design-system.git", "0.12.0")},
        release=Release("0.12.0", published=NOW),
    )
    binding = PackageBinding("design-system", "git@github.com:rtkelly-labs/design-system.git", NPM)
    record = track_package(binding, fetcher)
    assert record.status == PackageSyncStatus.SYNCED
    assert record.local_version == "0.12.0"
    assert record.deployed_version == "0.12.0"
    assert record.registry_url == "https://www.npmjs.com/package/@s/pkg"


def test_track_package_unreleased_when_local_ahead() -> None:
    fetcher = _npm_fetcher(
        files={"package.json": _manifest("git+https://github.com/rtkelly-labs/design-system.git", "0.15.0")},
        release=Release("0.12.0", published=NOW),
    )
    binding = PackageBinding("design-system", "git@github.com:rtkelly-labs/design-system.git", NPM)
    record = track_package(binding, fetcher)
    assert record.status == PackageSyncStatus.UNRELEASED
    assert record.local_version == "0.15.0"
    assert record.deployed_version == "0.12.0"
    assert "local ahead" in record.detail


def test_track_package_behind() -> None:
    fetcher = _npm_fetcher(
        files={"package.json": _manifest("git+https://github.com/rtkelly-labs/design-system.git", "0.11.0")},
        release=Release("0.12.0", published=NOW),
    )
    binding = PackageBinding("design-system", "git@github.com:rtkelly-labs/design-system.git", NPM)
    record = track_package(binding, fetcher)
    assert record.status == PackageSyncStatus.BEHIND
    assert "local behind" in record.detail


def test_track_package_unpublished() -> None:
    fetcher = _npm_fetcher(
        files={"package.json": _manifest("git+https://github.com/rtkelly-labs/design-system.git", "0.1.0")},
        release=None,
    )
    binding = PackageBinding("design-system", "git@github.com:rtkelly-labs/design-system.git", NPM)
    record = track_package(binding, fetcher)
    assert record.status == PackageSyncStatus.UNPUBLISHED
    assert record.deployed_version is None


def test_track_package_reads_local_estate_file_first(tmp_path: pathlib.Path) -> None:
    pkg_dir = tmp_path / "rtkelly-labs" / "design-system"
    pkg_dir.mkdir(parents=True)
    (pkg_dir / "package.json").write_text(json.dumps({"name": "@s/pkg", "version": "0.16.0"}), encoding="utf-8")
    # The remote fetcher has 0.12.0
    fetcher = _npm_fetcher(
        files={"package.json": _manifest("git+https://github.com/rtkelly-labs/design-system.git", "0.12.0")},
        release=Release("0.12.0", published=NOW),
    )
    binding = PackageBinding("design-system", "git@github.com:rtkelly-labs/design-system.git", NPM)
    record = track_package(binding, fetcher, estate_root=tmp_path / "personal")
    assert record.local_version == "0.16.0"
    assert record.status == PackageSyncStatus.UNRELEASED


def test_track_package_reads_local_nuget_directory_build_props(tmp_path: pathlib.Path) -> None:
    pkg_dir = tmp_path / "Resultful" / "Resultful"
    src_dir = pkg_dir / "Resultful"
    src_dir.mkdir(parents=True)
    (src_dir / "Resultful.csproj").write_text('<Project Sdk="Microsoft.NET.Sdk"></Project>', encoding="utf-8")
    (pkg_dir / "Directory.Build.props").write_text(
        "<Project><PropertyGroup><VersionPrefix>1.2.3</VersionPrefix></PropertyGroup></Project>",
        encoding="utf-8",
    )
    fetcher = FakeFetcher(
        files={"Resultful/Resultful.csproj": "<Project></Project>"},
        release=Release("0.2.0", published=NOW),
    )
    binding = PackageBinding(
        "Resultful",
        "git@github.com:Resultful/Resultful.git",
        PackageSpec("nuget", "Resultful", "Resultful/Resultful.csproj", "Publish.yml", "token"),
    )
    record = track_package(binding, fetcher, estate_root=tmp_path / "personal")
    assert record.local_version == "1.2.3"
    assert record.status == PackageSyncStatus.UNRELEASED


def test_track_package_nuget_defaults_to_1_0_0_when_no_version_tag(tmp_path: pathlib.Path) -> None:
    pkg_dir = tmp_path / "Resultful" / "Resultful"
    src_dir = pkg_dir / "Resultful"
    src_dir.mkdir(parents=True)
    (src_dir / "Resultful.csproj").write_text('<Project Sdk="Microsoft.NET.Sdk"></Project>', encoding="utf-8")
    fetcher = FakeFetcher(
        files={"Resultful/Resultful.csproj": "<Project></Project>"},
        release=Release("0.2.0", published=NOW),
    )
    binding = PackageBinding(
        "Resultful",
        "git@github.com:Resultful/Resultful.git",
        PackageSpec("nuget", "Resultful", "Resultful/Resultful.csproj", "Publish.yml", "token"),
    )
    record = track_package(binding, fetcher, estate_root=tmp_path / "personal")
    assert record.local_version == "1.0.0"
    assert record.status == PackageSyncStatus.UNRELEASED


def test_format_relative_age() -> None:
    now = datetime(2026, 10, 19, 12, 0, 0, tzinfo=UTC)
    assert format_relative_age(None, now) == "--"
    assert format_relative_age(now, now) == "just now"
    assert format_relative_age(datetime(2026, 10, 19, 9, 0, 0, tzinfo=UTC), now) == "3h ago"
    assert format_relative_age(datetime(2026, 10, 16, 12, 0, 0, tzinfo=UTC), now) == "3d ago"
    assert format_relative_age(datetime(2026, 10, 5, 12, 0, 0, tzinfo=UTC), now) == "2w ago"
    assert format_relative_age(datetime(2026, 8, 19, 12, 0, 0, tzinfo=UTC), now) == "2mo ago"
    assert format_relative_age(datetime(2024, 10, 19, 12, 0, 0, tzinfo=UTC), now) == "2y ago"


def test_cli_packages_status_and_artifacts_alias(tmp_path: pathlib.Path, monkeypatch: pytest.MonkeyPatch) -> None:
    # The registry is faked: this test used to resolve registry.npmjs.org on every run.
    routes = _npm_routes("11708460")
    monkeypatch.setattr(
        packages_cli, "LiveFetcher", lambda: LiveFetcher(client=httpx.Client(transport=_transport(routes)))
    )
    cfg = tmp_path / "managed.json"
    cfg.write_text(
        json.dumps(
            {
                "projects": [
                    {
                        "name": "design-system",
                        "repo": "git@github.com:rtkelly-labs/design-system.git",
                        "packages": [
                            {
                                "registry": "npm",
                                "name": "@s/pkg",
                                "manifest": "package.json",
                                "workflow": "publish.yml",
                            }
                        ],
                    }
                ]
            }
        ),
        encoding="utf-8",
    )
    runner = CliRunner()
    # Test 'workspace packages status --json'
    res = runner.invoke(app, ["packages", "status", "--config", str(cfg), "--json"])
    assert res.exit_code == 0
    payload = json.loads(res.stdout)
    assert len(payload) == 1
    assert payload[0]["package"] == "@s/pkg"
    assert payload[0]["latest_version"] == "0.12.0"

    # Test 'workspace artifacts --json'
    res_artifacts = runner.invoke(app, ["artifacts", "--config", str(cfg), "--json"])
    assert res_artifacts.exit_code == 0
    payload_artifacts = json.loads(res_artifacts.stdout)
    assert payload_artifacts == payload
    assert "latest_version" in payload[0]
    assert "latest_stable_version" in payload[0]
    assert "deployed_version" in payload[0]


def test_is_stable_version() -> None:
    assert is_stable_version("1.0.0") is True
    assert is_stable_version("0.0.3") is True
    assert is_stable_version("1.0.0+meta123") is True
    assert is_stable_version("0.2.0-beta05") is False
    assert is_stable_version("1.0.0-rc1") is False
    assert is_stable_version("16.5.0-canary.18") is False


def test_version_key_prerelease_ordering() -> None:
    assert _version_key("0.2.0-beta05") < _version_key("0.2.0-beta06")
    assert _version_key("16.5.0-canary.2") < _version_key("16.5.0-canary.18")
    assert _version_key("1.0.0-alpha") < _version_key("1.0.0-beta")
    assert _version_key("1.0.0-beta") < _version_key("1.0.0-rc1")
    assert _version_key("1.0.0-rc1") < _version_key("1.0.0")
    assert _version_key("1.0.0") < _version_key("1.0.1")


def test_release_stable_version_defaults() -> None:
    rel_stable = Release("0.0.3", published=NOW)
    assert rel_stable.version == "0.0.3"
    assert rel_stable.stable_version == "0.0.3"
    assert rel_stable.stable_published == NOW

    rel_pre = Release("0.2.0-beta05", published=NOW)
    assert rel_pre.version == "0.2.0-beta05"
    assert rel_pre.stable_version is None
    assert rel_pre.stable_published is None

    t_stable = datetime(2025, 1, 1, tzinfo=UTC)
    rel_custom = Release("1.0.0-rc1", published=NOW, stable_version="0.9.0", stable_published=t_stable)
    assert rel_custom.version == "1.0.0-rc1"
    assert rel_custom.stable_version == "0.9.0"
    assert rel_custom.stable_published == t_stable


def test_track_package_latest_and_stable_fields() -> None:
    # Package with only prerelease
    fetcher_pre = FakeFetcher(
        files={"package.json": _manifest("git+https://github.com/rtkelly-labs/design-system.git", "0.2.0-beta05")},
        release=Release("0.2.0-beta05", published=NOW),
    )
    binding = PackageBinding("design-system", "git@github.com:rtkelly-labs/design-system.git", NPM)
    record = track_package(binding, fetcher_pre)
    assert record.latest_version == "0.2.0-beta05"
    assert record.latest_stable_version is None
    assert record.deployed_version == "0.2.0-beta05"

    # Package with newer prerelease than stable
    t_stable = datetime(2025, 1, 1, tzinfo=UTC)
    fetcher_mixed = FakeFetcher(
        files={"package.json": _manifest("git+https://github.com/rtkelly-labs/design-system.git", "1.0.0-rc1")},
        release=Release("1.0.0-rc1", published=NOW, stable_version="0.9.0", stable_published=t_stable),
    )
    record_mixed = track_package(binding, fetcher_mixed)
    assert record_mixed.latest_version == "1.0.0-rc1"
    assert record_mixed.latest_stable_version == "0.9.0"
    assert record_mixed.deployed_version == "1.0.0-rc1"
    assert record_mixed.latest_published_at == NOW
    assert record_mixed.stable_published_at == t_stable


def test_live_fetcher_nuget_prerelease_and_stable_resolution() -> None:
    base = "https://api.nuget.org/v3/registration5-gz-semver2/pkg"
    entry_stable = {
        "@id": f"{base}/0.9.0.json",
        "version": "0.9.0",
        "listed": True,
        "published": "2025-01-01T00:00:00Z",
    }
    entry_rc1 = {
        "@id": f"{base}/1.0.0-rc1.json",
        "version": "1.0.0-rc1",
        "listed": True,
        "published": "2026-09-01T00:00:00Z",
    }
    entry_rc2 = {
        "@id": f"{base}/1.0.0-rc2.json",
        "version": "1.0.0-rc2",
        "listed": True,
        "published": "2026-10-01T00:00:00Z",
    }
    routes: dict[str, object] = {
        f"{base}/index.json": {
            "items": [
                {
                    "@id": f"{base}/page.json",
                    "items": [
                        {"catalogEntry": entry_stable},
                        {"catalogEntry": entry_rc1},
                        {"catalogEntry": entry_rc2},
                    ],
                }
            ]
        },
        f"{base}/1.0.0-rc2.json": {"repository": {"type": "git", "url": "https://github.com/org/repo.git"}},
        **nuget_routes("Pkg", ["0.9.0", "1.0.0-rc1", "1.0.0-rc2"]),
    }
    fetcher = LiveFetcher(client=httpx.Client(transport=_transport(routes)))
    release = fetcher.registry_release(NUGET)
    assert release is not None
    assert release.version == "1.0.0-rc2"
    assert release.stable_version == "0.9.0"
    assert release.published == datetime(2026, 10, 1, 0, 0, tzinfo=UTC)
    assert release.stable_published == datetime(2025, 1, 1, 0, 0, tzinfo=UTC)


def test_live_fetcher_npm_prerelease_and_stable_resolution() -> None:
    t_stable = "2025-01-01T00:00:00.000Z"
    t_beta = "2026-10-01T00:00:00.000Z"
    routes = {
        "https://registry.npmjs.org/@s%2fpkg": {
            "dist-tags": {"latest": "0.9.0"},
            "versions": {
                "0.9.0": {"_npmUser": {"name": "u"}},
                "1.0.0-beta.1": {"_npmUser": {"name": "u"}},
            },
            "time": {
                "0.9.0": t_stable,
                "1.0.0-beta.1": t_beta,
            },
        }
    }
    fetcher = LiveFetcher(client=httpx.Client(transport=_transport(routes)))
    release = fetcher.registry_release(NPM)
    assert release is not None
    assert release.version == "1.0.0-beta.1"
    assert release.stable_version == "0.9.0"
