"""Tests for NuGet trusted publishing governance in `workspace packages audit`.

The policy itself has no API, so these cover what the audit checks around it: the declared
policy fields, the publish workflow's NuGet/login job, the public V3 registry, and the
checklist printed for the website.
"""

from __future__ import annotations

import json
import pathlib
from dataclasses import replace
from datetime import UTC, datetime

import httpx
import pytest
from typer.testing import CliRunner

from rig.workspace import packages_cli
from rig.workspace.cli import app
from rig.workspace.nuget_governance import PolicyChecklist, package_glob, read_publish_workflow
from rig.workspace.package_sources import LiveFetcher
from rig.workspace.packages import (
    Finding,
    PackageBinding,
    PackageSpec,
    Release,
    RepoIdentity,
    Status,
    audit_packages,
    load_package_bindings,
)
from tests.test_workspace_packages import NUGET_REGISTRATIONS, FakeFetcher, nuget_routes

pytestmark = pytest.mark.ci

NOW = datetime(2026, 10, 25, tzinfo=UTC)
REPO = "git@github.com:rtkelly-labs/Parquet.SourceGenerator.git"
LIVE = RepoIdentity(full_name="rtkelly-labs/Parquet.SourceGenerator", id=467247054, owner_id=330611343)
SPEC = PackageSpec(
    registry="nuget",
    name="Parquet.SourceGenerator",
    manifest="Directory.Build.props",
    workflow="release.yml",
    nuget_owner="rtkelly-labs",
    nuget_user="rtk_13",
    owners=("rtk_13", "rtkelly-labs"),
    scopes=("push-new", "push-version"),
)
SOURCE = "https://github.com/rtkelly-labs/Parquet.SourceGenerator.git"
RELEASE = Release(version="0.0.3", published=None, source_repo_url=SOURCE, owners=("rtk_13", "rtkelly-labs"))

# Trimmed from Parquet.SourceGenerator's release.yml: the build job holds read only, and the
# publish job alone gets id-token: write, runs in `release`, logs in and pushes.
RELEASE_YML = """\
name: Release & Publish NuGet
on:
  workflow_dispatch:
permissions:
  contents: read
jobs:
  build:
    runs-on: ubuntu-latest
    permissions:
      contents: read
    steps:
      - run: dotnet pack --output ./artifacts
  publish:
    needs: [build]
    runs-on: ubuntu-latest
    permissions:
      contents: write
      id-token: write
    environment: release
    steps:
      - name: Login to NuGet via OIDC (Trusted Publishing)
        uses: NuGet/login@8d196754b4036150537f80ac539e15c2f1028841 # v1
        id: login
        with:
          user: ${{ secrets.NUGET_USER || 'rtk_13' }}
      - name: Push to NuGet
        env:
          NUGET_API_KEY: ${{ steps.login.outputs.NUGET_API_KEY }}
        run: |
          dotnet nuget push ./artifacts/*.nupkg \\
            --api-key "$NUGET_API_KEY" \\
            --source https://api.nuget.org/v3/index.json \\
            --skip-duplicate
"""


def _fetcher(workflow: str = RELEASE_YML, release: Release | None = RELEASE) -> FakeFetcher:
    props = f"<RepositoryUrl>{SOURCE}</RepositoryUrl>"
    return FakeFetcher(
        repo=LIVE,
        files={"Directory.Build.props": props, ".github/workflows/release.yml": workflow},
        release=release,
    )


def _findings(fetcher: FakeFetcher, *specs: PackageSpec, trust: bool = False) -> list[Finding]:
    bindings = [PackageBinding("Parquet.SourceGenerator", REPO, s) for s in specs or (SPEC,)]
    return audit_packages(bindings, fetcher, now=NOW, trust=trust)


def _audit(fetcher: FakeFetcher, spec: PackageSpec = SPEC, *, trust: bool = False) -> dict[str, Finding]:
    return {f.check: f for f in _findings(fetcher, spec, trust=trust)}


# ---------------------------------------------------------------- config


def test_policy_fields_decode_and_default_to_unset(tmp_path: pathlib.Path) -> None:
    declared = {
        "registry": "nuget",
        "name": "P",
        "manifest": "m",
        "workflow": "release.yml",
        "nuget_owner": "rtkelly-labs",
        "nuget_user": "rtk_13",
        "owners": ["rtk_13"],
        "scopes": ["push-new", "push-version"],
    }
    bare = {"registry": "nuget", "name": "Q", "manifest": "m", "workflow": "ci.yml", "auth": "token"}
    cfg = tmp_path / "managed-projects.json"
    _ = cfg.write_text(
        json.dumps({"projects": [{"name": "a", "repo": "git@github.com:o/a.git", "packages": [declared, bare]}]}),
        encoding="utf-8",
    )
    first, second = (b.spec for b in load_package_bindings(cfg))
    assert (first.nuget_owner, first.nuget_user, first.owners, first.scopes, first.environment) == (
        "rtkelly-labs",
        "rtk_13",
        ("rtk_13",),
        ("push-new", "push-version"),
        None,
    )
    assert (second.nuget_owner, second.nuget_user, second.owners, second.scopes) == (None, None, (), ())


def test_repo_config_declares_the_parquet_policy() -> None:
    cfg = pathlib.Path(__file__).resolve().parents[1] / "config" / "managed-projects.json"
    parquet = [b.spec for b in load_package_bindings(cfg) if b.project == "Parquet.SourceGenerator"]
    assert len(parquet) == 3
    for spec in parquet:
        assert spec.nuget_owner == "rtkelly-labs"
        assert spec.nuget_user == "rtk_13"
        assert spec.environment is None
        assert spec.scopes == ("push-new", "push-version")


# ---------------------------------------------------------------- workflow lint


def test_release_workflow_reads_the_publish_job() -> None:
    workflow = read_publish_workflow(RELEASE_YML)
    assert workflow is not None
    [job] = workflow.login_jobs
    assert (job.name, job.id_token_write, job.user, job.environment) == ("publish", True, "rtk_13", "release")
    assert job.push_found
    assert job.skip_duplicate
    assert job.push_secrets == ()


def test_release_workflow_passes_every_login_check() -> None:
    found = _audit(_fetcher())
    assert found["login"].status is Status.OK
    assert found["push"].status is Status.OK
    # No environment declared: a policy without one matches any, so this is noted, not judged.
    assert found["environment"].status is Status.SKIP
    assert _audit(_fetcher(), replace(SPEC, environment="release"))["environment"].status is Status.OK


def test_id_token_on_another_job_fails() -> None:
    moved = RELEASE_YML.replace(
        "      contents: read\n    steps:", "      contents: read\n      id-token: write\n    steps:"
    )
    moved = moved.replace("      contents: write\n      id-token: write\n", "      contents: write\n")
    found = _audit(_fetcher(moved))
    assert found["login"].status is Status.FAIL
    assert "id-token" in found["login"].detail


def test_write_all_and_inherited_permissions_grant_id_token() -> None:
    write_all = RELEASE_YML.replace("      contents: write\n      id-token: write\n", "").replace(
        "    environment: release", "    permissions: write-all\n    environment: release"
    )
    write_all = write_all.replace("    permissions:\n    permissions: write-all", "    permissions: write-all")
    assert "permissions: write-all" in write_all
    assert _audit(_fetcher(write_all))["login"].status is Status.OK
    inherited = (
        "permissions:\n  id-token: write\n"
        "jobs:\n  p:\n    steps:\n      - uses: nuget/login@v1\n        with:\n          user: rtk_13\n"
    )
    workflow = read_publish_workflow(inherited)
    assert workflow is not None
    assert workflow.login_jobs[0].id_token_write


def test_login_user_mismatch_fails_but_an_org_policy_owner_is_not_compared() -> None:
    other = RELEASE_YML.replace("'rtk_13'", "'someone-else'")
    found = _audit(_fetcher(other))
    assert found["login"].status is Status.FAIL
    assert "expected rtk_13" in found["login"].detail
    # nuget_owner is the organisation; logging in as the member account is correct.
    assert _audit(_fetcher())["login"].status is Status.OK


def test_login_user_without_a_literal_cannot_be_checked() -> None:
    secret_only = RELEASE_YML.replace("${{ secrets.NUGET_USER || 'rtk_13' }}", "${{ secrets.NUGET_USER }}")
    assert _audit(_fetcher(secret_only))["login"].status is Status.WARN


def test_undeclared_login_user_is_skipped() -> None:
    assert _audit(_fetcher(), replace(SPEC, nuget_user=None))["login"].status is Status.SKIP


def test_environment_mismatch_fails() -> None:
    found = _audit(
        _fetcher(RELEASE_YML.replace("environment: release", "environment: { name: staging }")),
        replace(SPEC, environment="release"),
    )
    assert found["environment"].status is Status.FAIL
    assert "staging" in found["environment"].detail


def test_push_without_skip_duplicate_warns() -> None:
    found = _audit(_fetcher(RELEASE_YML.replace(" \\\n            --skip-duplicate", "")))
    assert found["push"].status is Status.WARN
    assert "--skip-duplicate" in found["push"].detail


def test_push_reading_a_long_lived_key_warns() -> None:
    keyed = RELEASE_YML.replace("${{ steps.login.outputs.NUGET_API_KEY }}", "${{ secrets.NUGET_API_KEY }}")
    found = _audit(_fetcher(keyed))
    assert found["push"].status is Status.WARN
    assert "NUGET_API_KEY" in found["push"].detail


def test_missing_login_names_the_called_reusable_workflow() -> None:
    caller = "on: workflow_dispatch\njobs:\n  release:\n    uses: ./.github/workflows/publish-nuget.yml@main\n"
    found = _audit(_fetcher(caller))
    assert found["login"].status is Status.FAIL
    assert "publish-nuget.yml" in found["login"].detail


def test_unparseable_workflow_fails_the_login_check() -> None:
    assert _audit(_fetcher("jobs: [unclosed"))["login"].status is Status.FAIL


# ---------------------------------------------------------------- registry judgements


def test_healthy_release_has_no_policy_checklist() -> None:
    found = _audit(_fetcher())
    assert found["registry"].status is Status.OK
    assert found["owners"].status is Status.OK
    assert found["provenance"].status is Status.OK
    assert "policy" not in found
    assert not {"listed", "vulnerabilities", "deprecation"} & found.keys()


def test_missing_package_warns_and_prints_the_checklist() -> None:
    found = _audit(_fetcher(release=None))
    assert found["registry"].status is Status.WARN
    assert "push-new" in found["registry"].detail
    assert "Parquet.SourceGenerator registry drifted" in found["policy"].detail


def test_owner_mismatch_is_advisory_because_search_lags() -> None:
    found = _audit(_fetcher(release=replace(RELEASE, owners=("rtk_13",))))
    assert found["owners"].status is Status.WARN
    assert "missing rtkelly-labs" in found["owners"].detail
    assert "as of the last publish" in found["owners"].detail
    assert "policy" not in found


def test_unreadable_owners_are_skipped() -> None:
    assert _audit(_fetcher(release=replace(RELEASE, owners=None)))["owners"].status is Status.SKIP


def test_stale_repository_url_reports_the_old_owner() -> None:
    stale = replace(RELEASE, source_repo_url="https://github.com/rtkelly13/Parquet.SourceGenerator.git")
    found = _audit(_fetcher(release=stale))
    assert found["provenance"].status is Status.WARN
    assert "records rtkelly13/Parquet.SourceGenerator" in found["provenance"].detail
    assert "Parquet.SourceGenerator provenance drifted" in found["policy"].detail


def test_vulnerable_latest_fails_and_deprecation_is_reported() -> None:
    flagged = replace(RELEASE, vulnerabilities=("high https://github.com/advisories/GHSA-x",), deprecation="Legacy")
    found = _audit(_fetcher(release=flagged))
    assert found["vulnerabilities"].status is Status.FAIL
    assert "GHSA-x" in found["vulnerabilities"].detail
    assert found["deprecation"].status is Status.WARN


@pytest.mark.parametrize("version", ["0.2.0-alpha.cipr003", "0.0.6-alpha.cipr037+dynamic-du001", "1.0.0+sha.abc"])
def test_listed_ci_build_as_newest_warns(version: str) -> None:
    found = _audit(_fetcher(release=replace(RELEASE, version=version)))
    assert found["listed"].status is Status.WARN
    assert f"/{version.partition('+')[0]}/Manage" in found["listed"].fix


def test_all_unlisted_package_warns() -> None:
    found = _audit(_fetcher(release=replace(RELEASE, listed=False)))
    assert found["registry"].status is Status.WARN
    assert "provenance" not in found


# ---------------------------------------------------------------- live fetcher, offline


def _parquet_routes(entry: dict[str, object], owners: list[str] | None, leaf: dict[str, object]) -> dict[str, object]:
    base = f"{NUGET_REGISTRATIONS}parquet.sourcegenerator"
    return {
        **nuget_routes("Parquet.SourceGenerator", ["0.0.1", "0.0.3"], owners),
        f"{base}/index.json": {"items": [{"@id": f"{base}/page.json", "items": [{"catalogEntry": entry}]}]},
        "https://api.nuget.org/v3/catalog0/data/x/p.json": leaf,
    }


def _live(routes: dict[str, object]) -> LiveFetcher:
    def handle(request: httpx.Request) -> httpx.Response:
        body = routes.get(str(request.url))
        return httpx.Response(404) if body is None else httpx.Response(200, json=body)

    return LiveFetcher(client=httpx.Client(transport=httpx.MockTransport(handle)))


def test_live_fetcher_reads_owners_deprecation_and_vulnerabilities() -> None:
    entry: dict[str, object] = {
        "@id": "https://api.nuget.org/v3/catalog0/data/x/p.json",
        "version": "0.0.3",
        "listed": True,
        "deprecation": {"reasons": ["Legacy"], "message": "moved", "alternatePackage": {"id": "New.Pkg"}},
        "vulnerabilities": [{"advisoryUrl": "https://github.com/advisories/GHSA-x", "severity": "2"}],
    }
    leaf: dict[str, object] = {
        "repository": {"type": "git", "url": "https://github.com/rtkelly13/Parquet.SourceGenerator.git"}
    }
    release = _live(_parquet_routes(entry, ["rtk_13"], leaf)).registry_release(SPEC)
    assert release is not None
    assert release.owners == ("rtk_13",)
    assert release.deprecation == "Legacy (moved); use New.Pkg"
    assert release.vulnerabilities == ("high https://github.com/advisories/GHSA-x",)
    assert release.source_repo_url == "https://github.com/rtkelly13/Parquet.SourceGenerator.git"


def test_live_fetcher_flat_container_404_means_no_package() -> None:
    routes = nuget_routes("Other", ["1.0.0"], ["rtk_13"])
    assert _live(routes).registry_release(SPEC) is None


def test_live_fetcher_without_a_service_index_reads_nothing() -> None:
    assert _live({}).registry_release(SPEC) is None


def test_live_fetcher_reports_an_all_unlisted_package() -> None:
    entry: dict[str, object] = {
        "@id": "https://api.nuget.org/v3/catalog0/data/x/p.json",
        "version": "0.0.3",
        "listed": False,
    }
    release = _live(_parquet_routes(entry, None, {})).registry_release(SPEC)
    assert release is not None
    assert (release.version, release.listed, release.owners) == ("0.0.3", False, None)


# ---------------------------------------------------------------- checklist


@pytest.mark.parametrize(
    ("ids", "glob"),
    [
        (("Parquet.SourceGenerator",), "Parquet.SourceGenerator"),
        (
            ("Parquet.SourceGenerator", "Parquet.SourceGenerator.Attributes", "Parquet.SourceGenerator.Legacy"),
            "Parquet.SourceGenerator*",
        ),
        (("Iceberg.Net.Core", "Iceberg.Net.Arrow"), "Iceberg.Net.*"),
        (("Resultful", "FluentAssertions.OneOf"), "Resultful, FluentAssertions.OneOf"),
    ],
)
def test_package_glob_covers_every_id(ids: tuple[str, ...], glob: str) -> None:
    assert package_glob(ids) == glob


def test_checklist_renders_every_policy_field() -> None:
    checklist = PolicyChecklist(
        "rtkelly-labs",
        "rtkelly-labs",
        "Parquet.SourceGenerator",
        ".github/workflows/release.yml",
        None,
        ("push-new",),
        ("P",),
    )
    assert checklist.render().splitlines() == [
        "policy owner (nuget.org user or organisation): rtkelly-labs",
        "repository owner: rtkelly-labs",
        "repository: Parquet.SourceGenerator",
        "workflow file: release.yml",
        "environment: (none)",
        "scopes: push-new",
        "packages: P",
        "at: https://www.nuget.org/account/trustedpublishing",
        "note: temporary until first publish, 7 days; after a transfer add the new policy, publish, "
        "then delete the old one",
    ]


def test_trust_prints_one_checklist_per_shared_policy() -> None:
    specs = [replace(SPEC, name=n) for n in ("Parquet.SourceGenerator", "Parquet.SourceGenerator.Attributes")]
    policies = [f for f in _findings(_fetcher(), *specs, trust=True) if f.check == "policy"]
    assert len(policies) == 1
    assert policies[0].package == "nuget:Parquet.SourceGenerator*"
    assert policies[0].status is Status.MANUAL
    assert "confirm it by hand" in policies[0].detail
    assert "scopes: push-new, push-version" in policies[0].detail


def test_token_auth_packages_get_no_checklist() -> None:
    assert "policy" not in _audit(_fetcher(release=None), replace(SPEC, auth="token"), trust=True)


def test_cli_trust_prints_the_checklist(tmp_path: pathlib.Path, monkeypatch: pytest.MonkeyPatch) -> None:
    pkg = {"registry": "nuget", "name": SPEC.name, "manifest": SPEC.manifest, "workflow": "release.yml"}
    cfg = tmp_path / "managed-projects.json"
    _ = cfg.write_text(json.dumps({"projects": [{"name": "p", "repo": REPO, "packages": [pkg]}]}), encoding="utf-8")
    monkeypatch.setattr(packages_cli, "LiveFetcher", lambda: _fetcher())

    plain = CliRunner().invoke(app, ["packages", "audit", "--config", str(cfg)])
    assert plain.exit_code == 0
    assert "NuGet policies have no API" in " ".join(plain.stdout.split())

    trusted = CliRunner().invoke(app, ["packages", "audit", "--config", str(cfg), "--trust", "--json"])
    assert trusted.exit_code == 0
    assert '"check": "policy"' in trusted.stdout
    assert "trustedpublishing" in trusted.stdout
