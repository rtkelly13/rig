from __future__ import annotations

from dataclasses import dataclass

from pydantic import ValidationError

from rig import proc
from rig.repo_governance.policy_config import (
    GitHubAppAccountPolicy,
    PolicyConfig,
    load_policy_config,
)
from rig.typed_json import decode_json


@dataclass(frozen=True)
class AppAuditResult:
    """Audit result for a declared GitHub App installation on an account or organization."""

    app_slug: str
    app_name: str
    account: str
    required: bool
    installed: bool
    installation_id: int | None = None
    install_url: str = ""
    selection: str = "none"  # "all", "selected", "none"
    expected_repos: tuple[str, ...] = ()
    actual_repos: tuple[str, ...] = ()
    missing_repos: tuple[str, ...] = ()
    is_aligned: bool = False
    details: tuple[str, ...] = ()
    instructions: str | None = None


@dataclass(slots=True, frozen=True)
class InstallationAccount:
    login: str
    type: str


@dataclass(slots=True, frozen=True)
class Installation:
    """One GitHub App installation, as ``gh api`` reports it."""

    id: int
    app_slug: str | None = None
    repository_selection: str | None = None
    account: InstallationAccount | None = None

    @property
    def selection(self) -> str:
        return self.repository_selection or "selected"

    @property
    def account_login(self) -> str:
        return self.account.login if self.account else ""


@dataclass(slots=True, frozen=True)
class _InstallationsPage:
    installations: tuple[Installation, ...]


@dataclass(slots=True, frozen=True)
class Outcome:
    """Whether a GitHub call succeeded, and what it said."""

    ok: bool
    message: str


@dataclass(slots=True, frozen=True)
class SyncResult:
    ok: bool
    messages: tuple[str, ...]


class GitHubLookupError(RuntimeError):
    """Raised when a GitHub API lookup fails (as opposed to returning no results)."""


def _gh_api_out(args: list[str], what: str) -> str:
    """What ``gh api <args>`` prints. Raises GitHubLookupError, naming *what*, when it cannot run or fails."""
    res = proc.gh("api", *args)
    if res.code == proc.NOT_FOUND:
        raise GitHubLookupError(f"{what} could not run: {res.err}")
    if not res.ok:
        raise GitHubLookupError(f"{what} failed: {res.err.strip()}")
    return res.out


def _gh_api[T](kind: type[T], args: list[str]) -> T:
    out = _gh_api_out(args, f"gh api {' '.join(args)}")
    try:
        return decode_json(kind, out)
    except ValidationError as e:
        raise GitHubLookupError(f"gh api {' '.join(args)} returned unexpected JSON: {e}") from e


def fetch_org_installations(org: str) -> list[Installation]:
    """Fetches GitHub App installations for *org* (organization or personal account).

    Organizations use ``orgs/{org}/installations``; personal accounts use the
    authenticated user's ``user/installations`` filtered by account login.
    Raises GitHubLookupError on API failure so callers never mistake an error
    for "no installations".
    """
    account = _gh_api(InstallationAccount, [f"users/{org}"])
    is_org = account.type == "Organization"
    endpoint = f"orgs/{org}/installations" if is_org else "user/installations"
    pages = _gh_api(list[_InstallationsPage], [endpoint, "--paginate", "--slurp"])
    installations = [inst for page in pages for inst in page.installations]
    if not is_org:
        installations = [i for i in installations if i.account_login.lower() == org.lower()]
    return installations


def fetch_installation_repos(installation_id: int) -> list[str]:
    """Fetches repositories accessible by an installation. Raises GitHubLookupError on failure."""
    out = _gh_api_out(
        [f"user/installations/{installation_id}/repositories", "--paginate", "--jq", ".repositories[].name"],
        f"listing repositories for installation {installation_id}",
    )
    return [line.strip() for line in out.splitlines() if line.strip()]


def get_repo_id(owner: str, repo: str) -> int | None:
    """Fetches numerical repository ID from GitHub API."""
    res = proc.gh("api", f"repos/{owner}/{repo}", "--jq", ".id")
    val = res.out.strip() if res.ok else ""
    return int(val) if val.isdigit() else None


def add_repo_to_installation(installation_id: int, repo_id: int, dry_run: bool = False) -> Outcome:
    """Connects a repository to an existing GitHub App installation."""
    if dry_run:
        return Outcome(True, f"dry-run: would add repo {repo_id} to installation {installation_id}")
    res = proc.gh("api", "-X", "PUT", f"user/installations/{installation_id}/repositories/{repo_id}")
    if not res.ok:
        return Outcome(False, f"Failed to connect repo {repo_id}: {res.err.strip()}")
    return Outcome(True, f"Successfully connected repo {repo_id} to installation {installation_id}")


def remove_repo_from_installation(installation_id: int, repo_id: int, dry_run: bool = False) -> Outcome:
    """Disconnects a repository from an existing GitHub App installation."""
    if dry_run:
        return Outcome(True, f"dry-run: would remove repo {repo_id} from installation {installation_id}")
    res = proc.gh("api", "-X", "DELETE", f"user/installations/{installation_id}/repositories/{repo_id}")
    if not res.ok:
        return Outcome(False, f"Failed to remove repo {repo_id}: {res.err.strip()}")
    return Outcome(True, f"Successfully removed repo {repo_id} from installation {installation_id}")


def _available_repos(account: str, cfg: PolicyConfig, acc_policy: GitHubAppAccountPolicy) -> tuple[str, ...]:
    """Repositories an ``all_repos`` policy applies to: policy-registered plus GitHub-enumerated."""
    registered = cfg.repos_for_org(account)
    if not acc_policy.all_repos:
        return registered
    from rig.repo_governance.repo_discovery import discover_org_repos

    names = list(registered)
    for full in discover_org_repos(account):
        owner, _, bare = full.rpartition("/")
        if (not owner or owner.lower() == account.lower()) and bare not in names:
            names.append(bare)
    return tuple(names)


def audit_account_apps(account: str, config: PolicyConfig | None = None) -> list[AppAuditResult]:
    """Audits installed GitHub Apps and repository connections for *account*."""
    cfg = config or load_policy_config()
    declared_apps = cfg.apps_for_account(account)
    if not declared_apps:
        return []

    installed_map: dict[str, Installation] = {}
    for inst in fetch_org_installations(account):
        if inst.app_slug:
            installed_map[inst.app_slug.lower()] = inst

    results: list[AppAuditResult] = []

    for app in declared_apps:
        acc_policy = app.accounts.get(account)
        if not acc_policy:
            continue

        available_repos = _available_repos(account, cfg, acc_policy)
        expected_repos = acc_policy.effective_repos(available_repos)

        slug_lower = app.slug.lower()
        inst = installed_map.get(slug_lower)

        if not inst:
            instructions = (
                f"App '{app.name}' ({app.slug}) is not installed on '{account}'. "
                f"Interactive authorization required: {app.install_url}"
            )
            details = [f"Missing installation on account '{account}'."]
            results.append(
                AppAuditResult(
                    app_slug=app.slug,
                    app_name=app.name,
                    account=account,
                    required=acc_policy.required,
                    installed=False,
                    install_url=app.install_url,
                    selection="none",
                    expected_repos=expected_repos,
                    actual_repos=(),
                    missing_repos=expected_repos,
                    is_aligned=not acc_policy.required,
                    details=tuple(details),
                    instructions=instructions,
                )
            )
            continue

        inst_id = inst.id
        selection = inst.selection

        if selection == "all":
            results.append(
                AppAuditResult(
                    app_slug=app.slug,
                    app_name=app.name,
                    account=account,
                    required=acc_policy.required,
                    installed=True,
                    installation_id=inst_id,
                    install_url=app.install_url,
                    selection="all",
                    expected_repos=expected_repos,
                    actual_repos=("all",),
                    missing_repos=(),
                    is_aligned=True,
                    details=(f"App is enabled on all repositories across '{account}'.",),
                )
            )
            continue

        actual_repos = tuple(fetch_installation_repos(inst_id))
        missing_repos = tuple(r for r in expected_repos if r not in actual_repos)

        details_list: list[str] = []
        if missing_repos:
            details_list.append(f"Repositories missing connection: {', '.join(missing_repos)}")
        else:
            details_list.append(f"All {len(expected_repos)} expected repositories are connected.")

        is_aligned = len(missing_repos) == 0

        results.append(
            AppAuditResult(
                app_slug=app.slug,
                app_name=app.name,
                account=account,
                required=acc_policy.required,
                installed=True,
                installation_id=inst_id,
                install_url=app.install_url,
                selection=selection,
                expected_repos=expected_repos,
                actual_repos=actual_repos,
                missing_repos=missing_repos,
                is_aligned=is_aligned,
                details=tuple(details_list),
            )
        )

    return results


def sync_account_app_repos(
    account: str,
    app_slug: str,
    config: PolicyConfig | None = None,
    dry_run: bool = False,
) -> SyncResult:
    """Synchronizes repository connections for *app_slug* on *account*."""
    cfg = config or load_policy_config()
    app_policy = cfg.github_apps().get(app_slug)
    if not app_policy:
        return SyncResult(False, (f"App '{app_slug}' is not declared in policy configuration.",))

    acc_policy = app_policy.accounts.get(account)
    if not acc_policy:
        return SyncResult(False, (f"Account '{account}' has no declared policy for app '{app_slug}'.",))

    try:
        installations = fetch_org_installations(account)
    except GitHubLookupError as e:
        return SyncResult(False, (f"Cannot sync repositories: installation lookup failed: {e}",))
    inst = next((i for i in installations if (i.app_slug or "").lower() == app_slug.lower()), None)

    if not inst:
        return SyncResult(
            False,
            (
                f"Cannot sync repositories: App '{app_policy.name}' ({app_slug}) is NOT installed on '{account}'.",
                f"Please install it first: {app_policy.install_url}",
            ),
        )

    inst_id = inst.id
    if inst.selection == "all":
        return SyncResult(True, (f"App '{app_slug}' has access to all repositories on '{account}' (selection='all').",))

    available_repos = _available_repos(account, cfg, acc_policy)
    expected = acc_policy.effective_repos(available_repos)
    try:
        actual_repos = fetch_installation_repos(inst_id)
    except GitHubLookupError as e:
        return SyncResult(False, (f"Cannot sync repositories: repository lookup failed: {e}",))
    missing = [r for r in expected if r not in actual_repos]
    disallowed_connected = [r for r in actual_repos if r in acc_policy.disallow_repos]

    if not missing and not disallowed_connected:
        return SyncResult(True, (f"All declared repositories are correctly connected to '{app_slug}'.",))

    messages: list[str] = []
    success = True

    for repo_name in missing:
        repo_id = get_repo_id(account, repo_name)
        if not repo_id:
            success = False
            messages.append(f"Could not resolve repository ID for {account}/{repo_name}")
            continue

        outcome = add_repo_to_installation(inst_id, repo_id, dry_run=dry_run)
        if not outcome.ok:
            success = False
        messages.append(f"{repo_name}: {outcome.message}")

    for repo_name in disallowed_connected:
        repo_id = get_repo_id(account, repo_name)
        if not repo_id:
            success = False
            messages.append(f"Could not resolve repository ID for {account}/{repo_name}")
            continue

        outcome = remove_repo_from_installation(inst_id, repo_id, dry_run=dry_run)
        if not outcome.ok:
            success = False
        messages.append(f"{repo_name} (disallowed): {outcome.message}")

    return SyncResult(success, tuple(messages))


def is_repo_disallowed(repo_name: str, org: str, app_slug: str, config: PolicyConfig | None = None) -> bool:
    """Checks if repo_name is in disallow_repos for the given provider/app on org."""
    cfg = config or load_policy_config()
    app = cfg.github_apps().get(app_slug)
    if not app:
        return False
    acc_policy = app.accounts.get(org)
    if not acc_policy:
        return False
    bare = repo_name.rsplit("/", 1)[-1]
    return bare in acc_policy.disallow_repos
