"""Published packages audited against the repositories that publish them.

A package on a registry is bound to a GitHub repository in three places that
nothing keeps in step: the manifest's repository URL, the registry's trusted
publishing config, and the provenance of each published version. Transferring a
repository (``rtkelly13`` -> ``rtkelly-labs``) moves none of them. npm then
rejects every OIDC publish with a bare ``E404`` and the package silently stops
shipping, which is how ``@rtkelly13/design-system`` sat at 0.12.0 for three
weeks while ``main`` reached 0.16.0.

The bindings are declared per project in ``config/managed-projects.json`` under
``packages`` (ADR 0020: the CLI validates, it does not edit). Every check here
uses public, unauthenticated endpoints, except the npm trust listing, which npm
gates behind a 2FA one-time password and is therefore opt-in.

What cannot be checked, and why:

- NuGet trusted publishing policies have no API at all, read or write. The audit
  checks what surrounds one instead (the publish workflow's ``NuGet/login`` job,
  the package's owners and recorded repository) and prints the policy's fields
  as a checklist when those suggest it is missing or stale, or under ``--trust``.
- npm's trust config is readable (``npm trust list``) but never unattended.

The check that would have caught the design-system failure on day one needs no
auth: npm provenance records the GitHub ``repository_owner_id`` of the run that
published the latest version. A transfer changes that ID and keeps the
``repository_id``, so a mismatch against the live repository means the
registry's trust config still names the old owner.
"""

from __future__ import annotations

import re
from collections.abc import Iterable
from dataclasses import dataclass, field, replace
from datetime import UTC, datetime
from enum import StrEnum
from pathlib import Path
from typing import Literal, Protocol

from rig.repo_slug import RepoSlug
from rig.typed_json import Lenient, decode_json, try_decode_json
from rig.workspace.layout import project_dir
from rig.workspace.nuget_governance import (
    NUGET_TRUST_URL,
    PolicyChecklist,
    read_publish_workflow,
)
from rig.workspace.package_versions import is_ahead, is_stable_version, version_key

Registry = Literal["npm", "nuget"]
AuthMode = Literal["oidc", "token"]

_ID_TOKEN_WRITE = re.compile(r"^\s*id-token:\s*write\b", re.MULTILINE)
_TOKEN_SECRET = re.compile(r"secrets\.(NPM_TOKEN|NODE_AUTH_TOKEN|NUGET_API_KEY|NUGET_TOKEN)\b")


class Status(StrEnum):
    OK = "ok"
    WARN = "warn"
    FAIL = "fail"
    MANUAL = "manual"
    SKIP = "skip"


@dataclass(slots=True, frozen=True)
class PackageSpec:
    """One published package, as declared under a project's ``packages`` list."""

    registry: Registry
    name: str
    manifest: str
    workflow: str
    auth: AuthMode = "oidc"
    environment: str | None = None
    # NuGet only: the policy rig cannot read, declared so the audit can check around it.
    #: The nuget.org user or organisation that owns the policy.
    nuget_owner: str | None = None
    #: The profile name NuGet/login sends as ``user:``; an organisation's policy is exchanged by a member.
    nuget_user: str | None = None
    #: The package's expected nuget.org owners.
    owners: tuple[str, ...] = ()
    #: The policy's scopes, e.g. ``push-new`` and ``push-version``.
    scopes: tuple[str, ...] = ()


@dataclass(slots=True, frozen=True)
class PackageBinding:
    project: str
    repo: str
    spec: PackageSpec


@dataclass(slots=True, frozen=True)
class RepoIdentity:
    full_name: str
    id: int
    owner_id: int


@dataclass(slots=True, frozen=True)
class Provenance:
    """Where the registry says a version was built. npm only."""

    repository_url: str
    workflow_path: str
    repository_id: int | None
    owner_id: int | None


@dataclass(slots=True, frozen=True)
class Release:
    """The registry's version information of a package."""

    version: str
    published: datetime | None
    # npm: whether ``_npmUser.trustedPublisher`` is set (observed, not a documented contract).
    trusted_publisher: bool | None = None
    provenance: Provenance | None = None
    # NuGet: the catalog entry's ``repository.url`` (from the nuspec).
    source_repo_url: str | None = None
    stable_version: str | None = None
    stable_published: datetime | None = None
    # NuGet: False when every version is unlisted (``version`` is then the highest unlisted one).
    listed: bool = True
    #: Search's ``owners``; ``None`` when search could not be read.
    owners: tuple[str, ...] | None = None
    #: The latest listed version's deprecation, summarised; ``None`` when it is not deprecated.
    deprecation: str | None = None
    #: The latest listed version's advisories, as ``severity advisoryUrl``.
    vulnerabilities: tuple[str, ...] = ()

    def __post_init__(self) -> None:
        if self.stable_version is None and is_stable_version(self.version):
            object.__setattr__(self, "stable_version", self.version)
            object.__setattr__(self, "stable_published", self.published)


@dataclass(slots=True, frozen=True)
class TrustConfig:
    repository: str
    file: str
    environment: str | None = None


@dataclass(slots=True, frozen=True)
class Finding:
    package: str
    check: str
    status: Status
    detail: str
    fix: str = ""


class Fetcher(Protocol):
    def github_repo(self, slug: str, /) -> RepoIdentity | None: ...
    def github_file(self, slug: str, path: str, /) -> str | None: ...
    def workflow_conclusions(self, slug: str, workflow: str, limit: int, /) -> list[str] | None: ...
    def registry_release(self, spec: PackageSpec, /) -> Release | None: ...
    def npm_trust(self, name: str, /) -> list[TrustConfig] | str: ...


# ---------------------------------------------------------------- config


@dataclass(slots=True, frozen=True)
class _ProjectEntry:
    name: str
    repo: str
    enabled: bool = True
    packages: tuple[PackageSpec, ...] = ()


@dataclass(slots=True, frozen=True)
class _ManagedProjects:
    projects: tuple[_ProjectEntry, ...] = ()


def load_package_bindings(cfg: Path) -> list[PackageBinding]:
    """Every ``projects[].packages[]`` entry in ``managed-projects.json``; a malformed one raises ``ValueError``."""
    managed = decode_json(_ManagedProjects, cfg.read_text(encoding="utf-8"))
    return [
        PackageBinding(project=project.name, repo=project.repo, spec=spec)
        for project in managed.projects
        if project.enabled
        for spec in project.packages
    ]


# ---------------------------------------------------------------- parsing


def repo_slug(url: str) -> str | None:
    """``git@github.com:o/r.git``, ``git+https://github.com/o/r.git`` and friends -> ``o/r``."""
    slug = RepoSlug.from_remote(url)
    return slug.full_name if slug else None


def _same(a: str | None, b: str | None) -> bool:
    return a is not None and b is not None and a.lower() == b.lower()


@dataclass(slots=True, frozen=True)
class ManifestFields:
    repository_url: str | None
    version: str | None


@dataclass(slots=True, frozen=True)
class _NpmRepository:
    url: Lenient[str] = None


@dataclass(slots=True, frozen=True)
class _NpmManifest:
    version: Lenient[str] = None
    repository: Lenient[_NpmRepository | str] = None


def manifest_fields(registry: Registry, text: str) -> ManifestFields:
    """The manifest's repository URL and version, either of which may be absent."""
    if registry == "npm":
        pkg = try_decode_json(_NpmManifest, text) or _NpmManifest()
        repo = pkg.repository
        url = repo.url if isinstance(repo, _NpmRepository) else repo
        return ManifestFields(url, pkg.version)
    url = re.search(r"<RepositoryUrl>\s*([^<]+?)\s*</RepositoryUrl>", text)
    version = re.search(r"<(?:Version|VersionPrefix)>\s*([^<$]+?)\s*</(?:Version|VersionPrefix)>", text)
    return ManifestFields(url.group(1) if url else None, version.group(1) if version else None)


_version_key = version_key


def consecutive_failures(conclusions: Iterable[str]) -> int:
    """Failures since the last success, newest first; cancelled and skipped runs neither break nor extend it."""
    count = 0
    for conclusion in conclusions:
        if conclusion == "success":
            break
        if conclusion in ("failure", "timed_out", "startup_failure"):
            count += 1
    return count


# ---------------------------------------------------------------- checks


def _no_findings() -> list[Finding]:
    return []


@dataclass(slots=True)
class _Report:
    package: str
    findings: list[Finding] = field(default_factory=_no_findings)

    def add(self, check: str, status: Status, detail: str, fix: str = "") -> None:
        self.findings.append(Finding(self.package, check, status, detail, fix))


def audit_binding(binding: PackageBinding, fetcher: Fetcher, *, now: datetime, trust: bool = False) -> list[Finding]:
    spec = binding.spec
    report = _Report(f"{spec.registry}:{spec.name}")
    declared = repo_slug(binding.repo)
    live = fetcher.github_repo(declared) if declared else None
    if declared is None or live is None:
        report.add("repo", Status.FAIL, f"cannot resolve {binding.repo} on GitHub")
        return report.findings

    canonical = live.full_name
    if _same(declared, canonical):
        report.add("repo", Status.OK, canonical)
    else:
        report.add(
            "repo",
            Status.FAIL,
            f"managed-projects.json says {declared}; GitHub redirects to {canonical}",
            f"set {binding.project}'s repo to git@github.com:{canonical}.git",
        )

    _check_manifest(report, spec, fetcher, canonical, now)
    workflow = _check_workflow(report, spec, fetcher, canonical)
    release = fetcher.registry_release(spec)
    if spec.registry == "npm":
        _check_npm_release(report, spec, release, live)
        if trust:
            _check_npm_trust(report, spec, fetcher, canonical)
    else:
        _check_nuget_login(report, spec, workflow)
        _check_nuget_release(report, spec, release, canonical)
    _check_runs(report, spec, fetcher, canonical)
    return report.findings


def _check_manifest(report: _Report, spec: PackageSpec, fetcher: Fetcher, canonical: str, now: datetime) -> None:
    text = fetcher.github_file(canonical, spec.manifest)
    if text is None:
        report.add("manifest", Status.FAIL, f"{spec.manifest} not found on the default branch")
        return
    fields = manifest_fields(spec.registry, text)
    url, version = fields.repository_url, fields.version
    if url is None:
        report.add("manifest", Status.WARN, f"{spec.manifest} declares no repository URL")
    else:
        slug = repo_slug(url)
        matches = (slug == canonical) if spec.registry == "npm" else _same(slug, canonical)
        if matches:
            report.add("manifest", Status.OK, url)
        else:
            # npm verifies provenance against repository.url server-side and refuses the
            # publish with a 422; NuGet only ships the wrong source link.
            consequence = (
                "npm will reject the publish (422, provenance)" if spec.registry == "npm" else "wrong source link"
            )
            report.add(
                "manifest",
                Status.FAIL if spec.registry == "npm" else Status.WARN,
                f"{spec.manifest} points at {url}: {consequence}",
                f"set the repository URL to https://github.com/{canonical}.git",
            )

    release = fetcher.registry_release(spec)
    if version is None or release is None:
        return
    if is_ahead(version, release.version):
        age = f", published {(now - release.published).days} days ago" if release.published else ""
        report.add(
            "version",
            Status.WARN,
            f"manifest is at {version}; the registry's latest is {release.version}{age}",
        )
    else:
        report.add("version", Status.OK, f"{release.version} published")


def _check_workflow(report: _Report, spec: PackageSpec, fetcher: Fetcher, canonical: str) -> str | None:
    """The file-wide checks; returns the workflow's text for the registry-specific ones."""
    path = f".github/workflows/{spec.workflow}"
    text = fetcher.github_file(canonical, path)
    if text is None:
        report.add("workflow", Status.FAIL, f"{path} not found on the default branch")
        return None
    has_oidc = bool(_ID_TOKEN_WRITE.search(text))
    secrets = sorted(set(_TOKEN_SECRET.findall(text)))
    if spec.auth == "token":
        report.add(
            "workflow",
            Status.WARN,
            f"publishes with a long-lived token ({', '.join(secrets) or 'secret'})",
            "move to trusted publishing and set auth to oidc",
        )
    elif not has_oidc:
        report.add("workflow", Status.FAIL, f"{path} lacks `id-token: write`; OIDC publishing cannot work")
    elif secrets:
        report.add("workflow", Status.WARN, f"OIDC enabled but {', '.join(secrets)} still referenced")
    else:
        report.add("workflow", Status.OK, f"{path} has id-token: write")
    return text


def _check_npm_release(report: _Report, spec: PackageSpec, release: Release | None, live: RepoIdentity) -> None:
    if release is None:
        report.add(
            "registry",
            Status.WARN,
            "not on npm",
            "a new package must exist before `npm trust`: publish once with 2FA, then run `npm trust github`",
        )
        return
    if spec.auth == "oidc" and release.trusted_publisher is False:
        report.add("trusted-publisher", Status.WARN, f"{release.version} was published with a token, not OIDC")
    elif release.trusted_publisher:
        report.add("trusted-publisher", Status.OK, f"{release.version} published via OIDC")

    prov = release.provenance
    if prov is None:
        status = Status.WARN if spec.auth == "oidc" else Status.SKIP
        report.add("provenance", status, f"{release.version} carries no provenance")
        return
    built_from = repo_slug(prov.repository_url)
    trust_fix = (
        f"npm trust revoke {spec.name} --id <id>; npm trust github {spec.name} "
        f"--repo {live.full_name} --file {spec.workflow} --allow-publish"
        + (f" --env {spec.environment}" if spec.environment else "")
    )
    if prov.repository_id is not None and prov.repository_id != live.id:
        report.add(
            "provenance",
            Status.FAIL,
            f"{release.version} was built by a different repository ({built_from}, id {prov.repository_id})",
        )
    elif prov.owner_id is not None and prov.owner_id != live.owner_id:
        report.add(
            "provenance",
            Status.FAIL,
            f"repository moved since {release.version} was published: {built_from} -> {live.full_name} "
            f"(owner id {prov.owner_id} -> {live.owner_id}); the trust config still names the old owner",
            trust_fix,
        )
    elif not _same(built_from, live.full_name):
        report.add("provenance", Status.FAIL, f"{release.version} built from {built_from}", trust_fix)
    else:
        report.add("provenance", Status.OK, f"{release.version} built from {built_from}/{prov.workflow_path}")


def _check_npm_trust(report: _Report, spec: PackageSpec, fetcher: Fetcher, canonical: str) -> None:
    result = fetcher.npm_trust(spec.name)
    if isinstance(result, str):
        report.add("trust", Status.WARN, f"npm trust list failed: {result}")
        return
    want_file = Path(spec.workflow).name.lower()
    match = [
        t
        for t in result
        if _same(t.repository, canonical)
        and Path(t.file).name.lower() == want_file
        and t.environment == spec.environment
    ]
    if match:
        report.add("trust", Status.OK, f"trusts {canonical} {spec.workflow}")
    else:
        seen = ", ".join(f"{t.repository} {t.file}" for t in result) or "nothing"
        report.add(
            "trust",
            Status.FAIL,
            f"trust config names {seen}; expected {canonical} {spec.workflow}",
            f"npm trust github {spec.name} --repo {canonical} --file {spec.workflow} --allow-publish"
            + (f" --env {spec.environment}" if spec.environment else ""),
        )


def _check_nuget_login(report: _Report, spec: PackageSpec, text: str | None) -> None:
    """The job a policy has to match: it runs NuGet/login as the policy owner, in its environment."""
    if text is None or spec.auth != "oidc":
        return
    workflow = read_publish_workflow(text)
    if workflow is None or not workflow.login_jobs:
        called = workflow.called if workflow else ()
        hint = f"; it calls {', '.join(called)}, and the policy must name the called file" if called else ""
        report.add("login", Status.FAIL, f"no job in {spec.workflow} runs NuGet/login{hint}")
        return
    job = workflow.login_jobs[0]
    where = f"job {job.name}"
    if not job.id_token_write:
        report.add("login", Status.FAIL, f"{where} runs NuGet/login without `id-token: write`")
    elif job.user is None:
        report.add("login", Status.WARN, f"{where} sends user {job.user_input or '(none)'}: no literal to check")
    elif spec.nuget_user is None:
        report.add("login", Status.SKIP, f"{where} logs in as {job.user}; declare nuget_user to check it")
    elif not _same(job.user, spec.nuget_user):
        report.add(
            "login",
            Status.FAIL,
            f"{where} logs in as {job.user}; expected {spec.nuget_user}",
            f"set NuGet/login's user to {spec.nuget_user} (the nuget.org profile name, not an email)",
        )
    else:
        report.add("login", Status.OK, f"{where} logs in as {job.user} with id-token: write")

    if spec.environment is not None and not _same(job.environment, spec.environment):
        report.add(
            "environment",
            Status.FAIL,
            f"{where} runs in {job.environment or 'no environment'}; the policy requires {spec.environment}",
        )
    elif spec.environment is not None:
        report.add("environment", Status.OK, f"{where} runs in {spec.environment}")
    elif job.environment is not None:
        report.add(
            "environment", Status.SKIP, f"{where} runs in {job.environment}; declare it if the policy requires it"
        )

    if not job.push_found:
        report.add("push", Status.WARN, f"{where} has no `nuget push` after NuGet/login; its key lasts one hour")
    elif job.push_secrets:
        report.add("push", Status.WARN, f"push still reads {', '.join(job.push_secrets)}; use NuGet/login's key")
    elif not job.skip_duplicate:
        report.add("push", Status.WARN, "push lacks --skip-duplicate, so a rerun after a partial publish fails")
    else:
        report.add("push", Status.OK, "pushes with NuGet/login's key and --skip-duplicate")


_CI_BUILD = re.compile(r"cipr|\+", re.IGNORECASE)


def _check_nuget_release(report: _Report, spec: PackageSpec, release: Release | None, canonical: str) -> None:
    if release is None:
        report.add(
            "registry",
            Status.WARN,
            "not on nuget.org: the first publish needs a policy with the push-new scope",
            NUGET_TRUST_URL,
        )
        return
    if release.listed:
        report.add("registry", Status.OK, f"{release.version} is the newest listed version")
    else:
        report.add("registry", Status.WARN, f"every version is unlisted (newest {release.version})")
    _check_nuget_owners(report, spec, release)
    if not release.listed:
        return

    if release.source_repo_url is None:
        report.add("provenance", Status.SKIP, f"{release.version} records no source repository")
    elif _same(repo_slug(release.source_repo_url), canonical):
        report.add("provenance", Status.OK, f"{release.version} built from {canonical}")
    else:
        # The catalog copies the nuspec, so this is the manifest's claim at publish
        # time, not the build's identity. It still dates the last publish to the
        # old name, and a policy made then matches neither name nor owner ID now.
        report.add(
            "provenance",
            Status.WARN,
            f"{release.version} records {repo_slug(release.source_repo_url)}; the repo is now {canonical}. "
            "A policy made for the old name will not match: NuGet checks owner and repo by name, "
            "and locks their IDs on the first publish",
            f"add a policy for {canonical} at {NUGET_TRUST_URL}, publish, then delete the old one",
        )

    if _CI_BUILD.search(release.version):
        report.add(
            "listed",
            Status.WARN,
            f"the newest listed version, {release.version}, is a CI build",
            f"unlist it at https://www.nuget.org/packages/{spec.name}/{release.version.partition('+')[0]}/Manage",
        )
    if release.vulnerabilities:
        report.add(
            "vulnerabilities", Status.FAIL, f"{release.version} has advisories: {', '.join(release.vulnerabilities)}"
        )
    if release.deprecation is not None:
        report.add("deprecation", Status.WARN, f"{release.version} is deprecated: {release.deprecation}")


def _check_nuget_owners(report: _Report, spec: PackageSpec, release: Release) -> None:
    # Search reindexes on a publish, not on an owner change, so its list can be stale: an
    # organisation added since the last publish is missing. Advisory only, never a failure.
    if release.owners is None:
        report.add("owners", Status.SKIP, "could not read owners from NuGet search")
        return
    live = ", ".join(release.owners) or "nobody"
    expected = {o.lower() for o in spec.owners} | ({spec.nuget_owner.lower()} if spec.nuget_owner else set[str]())
    missing = sorted(expected - {o.lower() for o in release.owners})
    if missing:
        report.add(
            "owners",
            Status.WARN,
            f"search lists {live} (as of the last publish); missing {', '.join(missing)}",
            f"confirm at https://www.nuget.org/packages/{spec.name}/Manage",
        )
    else:
        report.add("owners", Status.OK, f"search lists {live} (as of the last publish)")


def _check_runs(report: _Report, spec: PackageSpec, fetcher: Fetcher, canonical: str) -> None:
    conclusions = fetcher.workflow_conclusions(canonical, spec.workflow, 20)
    if conclusions is None:
        report.add("runs", Status.SKIP, "could not read workflow runs")
        return
    failed = consecutive_failures(conclusions)
    if failed:
        report.add("runs", Status.FAIL, f"last {failed} completed publish run(s) failed")
    elif conclusions:
        report.add("runs", Status.OK, f"latest publish run: {conclusions[0]}")
    else:
        report.add("runs", Status.SKIP, "no completed publish runs")


# Findings that suggest a policy is missing or names an old owner: the package does not
# exist or is all unlisted, or its last publish recorded another repo. Owners are left
# out because search's list goes stale between publishes.
_POLICY_DRIFT = frozenset({"registry", "provenance"})


def _policy(binding: PackageBinding) -> PolicyChecklist:
    """The policy a binding needs, minus its package IDs: bindings sharing one share a policy."""
    spec = binding.spec
    owner, _, name = (repo_slug(binding.repo) or binding.repo).partition("/")
    return PolicyChecklist(spec.nuget_owner, owner, name, spec.workflow, spec.environment, spec.scopes, ())


def _add_policy_checklists(bindings: list[PackageBinding], per_binding: list[list[Finding]], trust: bool) -> None:
    """One checklist per NuGet policy, after its last package, when asked for or when drift suggests it."""
    groups: dict[PolicyChecklist, list[int]] = {}
    for i, binding in enumerate(bindings):
        if binding.spec.registry == "nuget" and binding.spec.auth == "oidc":
            groups.setdefault(_policy(binding), []).append(i)
    for policy, members in groups.items():
        drift = [
            f"{bindings[i].spec.name} {f.check}"
            for i in members
            for f in per_binding[i]
            if f.check in _POLICY_DRIFT and f.status in (Status.WARN, Status.FAIL)
        ]
        if not (trust or drift):
            continue
        checklist = replace(policy, package_ids=tuple(bindings[i].spec.name for i in members))
        why = f"check it exists ({', '.join(drift)} drifted)" if drift else "confirm it by hand"
        detail = f"no API to read the policy; {why}:\n{checklist.render()}"
        finding = Finding(f"nuget:{checklist.package_glob}", "policy", Status.MANUAL, detail, NUGET_TRUST_URL)
        per_binding[members[-1]].append(finding)


def audit_packages(
    bindings: Iterable[PackageBinding], fetcher: Fetcher, *, now: datetime | None = None, trust: bool = False
) -> list[Finding]:
    moment = now or datetime.now(UTC)
    declared = list(bindings)
    per_binding = [audit_binding(b, fetcher, now=moment, trust=trust) for b in declared]
    _add_policy_checklists(declared, per_binding, trust)
    return [f for findings in per_binding for f in findings]


class PackageSyncStatus(StrEnum):
    SYNCED = "synced"
    UNRELEASED = "unreleased"
    BEHIND = "behind"
    UNPUBLISHED = "unpublished"
    UNKNOWN = "unknown"


@dataclass(slots=True, frozen=True)
class PackageTrackingRecord:
    project: str
    package: str
    registry: Registry
    manifest_path: str
    local_version: str | None
    deployed_version: str | None
    status: PackageSyncStatus
    published_at: datetime | None
    registry_url: str
    detail: str = ""
    latest_version: str | None = None
    latest_stable_version: str | None = None
    latest_published_at: datetime | None = None
    stable_published_at: datetime | None = None

    def __post_init__(self) -> None:
        if self.latest_version is None and self.deployed_version is not None:
            object.__setattr__(self, "latest_version", self.deployed_version)
        if self.deployed_version is None and self.latest_version is not None:
            object.__setattr__(self, "deployed_version", self.latest_version)
        if self.latest_published_at is None and self.published_at is not None:
            object.__setattr__(self, "latest_published_at", self.published_at)
        if self.published_at is None and self.latest_published_at is not None:
            object.__setattr__(self, "published_at", self.latest_published_at)


def registry_web_url(spec: PackageSpec) -> str:
    if spec.registry == "npm":
        return f"https://www.npmjs.com/package/{spec.name}"
    if spec.registry == "nuget":
        return f"https://www.nuget.org/packages/{spec.name}"
    return ""


def format_relative_age(dt: datetime | None, now: datetime | None = None) -> str:
    if dt is None:
        return "--"
    current_time = now or datetime.now(UTC)
    if dt.tzinfo is None:
        dt = dt.replace(tzinfo=UTC)
    diff = current_time - dt
    total_seconds = int(diff.total_seconds())
    if total_seconds < 0:
        return "in future"
    days = diff.days
    if days < 1:
        hours = total_seconds // 3600
        return f"{hours}h ago" if hours > 0 else "just now"
    if days < 7:
        return f"{days}d ago"
    if days < 30:
        weeks = days // 7
        return f"{weeks}w ago"
    if days < 365:
        months = days // 30
        return f"{months}mo ago"
    years = days // 365
    return f"{years}y ago"


def track_package(
    binding: PackageBinding,
    fetcher: Fetcher,
    *,
    estate_root: Path | None = None,
) -> PackageTrackingRecord:
    spec = binding.spec
    canonical = repo_slug(binding.repo)

    manifest_text: str | None = None
    if estate_root is not None:
        local_file = project_dir(estate_root, binding.project, binding.repo) / spec.manifest
        if local_file.is_file():
            try:
                manifest_text = local_file.read_text(encoding="utf-8")
            except OSError:
                manifest_text = None

    if manifest_text is None and canonical:
        manifest_text = fetcher.github_file(canonical, spec.manifest)

    local_version: str | None = None
    if manifest_text is not None:
        fields = manifest_fields(spec.registry, manifest_text)
        local_version = fields.version
        if local_version is None and spec.registry == "nuget":
            if estate_root is not None:
                props_file = (
                    project_dir(estate_root, binding.project, binding.repo) / "Directory.Build.props"
                ).resolve()
                if props_file.is_file():
                    try:
                        pfields = manifest_fields(spec.registry, props_file.read_text(encoding="utf-8"))
                        local_version = pfields.version
                    except OSError:
                        pass
            if local_version is None:
                local_version = "1.0.0"

    release = fetcher.registry_release(spec)
    deployed_version = release.version if release else None
    latest_version = release.version if release else None
    latest_stable_version = release.stable_version if release else None
    published_at = release.published if release else None
    latest_published_at = release.published if release else None
    stable_published_at = release.stable_published if release else None
    url = registry_web_url(spec)

    if release is None:
        status = PackageSyncStatus.UNPUBLISHED
        detail = "not found on registry"
    elif manifest_text is None:
        status = PackageSyncStatus.UNKNOWN
        detail = "local manifest not found"
    elif local_version is None:
        status = PackageSyncStatus.UNKNOWN
        detail = "version not specified in manifest"
    elif local_version == release.version:
        status = PackageSyncStatus.SYNCED
        detail = "up to date"
    elif is_ahead(local_version, release.version):
        status = PackageSyncStatus.UNRELEASED
        detail = f"local ahead ({local_version} > {release.version})"
    elif is_ahead(release.version, local_version):
        status = PackageSyncStatus.BEHIND
        detail = f"local behind ({local_version} < {release.version})"
    else:
        status = PackageSyncStatus.SYNCED
        detail = "up to date"

    return PackageTrackingRecord(
        project=binding.project,
        package=spec.name,
        registry=spec.registry,
        manifest_path=spec.manifest,
        local_version=local_version,
        deployed_version=deployed_version,
        status=status,
        published_at=published_at,
        registry_url=url,
        detail=detail,
        latest_version=latest_version,
        latest_stable_version=latest_stable_version,
        latest_published_at=latest_published_at,
        stable_published_at=stable_published_at,
    )


def track_packages(
    bindings: Iterable[PackageBinding],
    fetcher: Fetcher,
    *,
    estate_root: Path | None = None,
) -> list[PackageTrackingRecord]:
    return [track_package(b, fetcher, estate_root=estate_root) for b in bindings]
