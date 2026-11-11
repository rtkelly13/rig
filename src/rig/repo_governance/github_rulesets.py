from __future__ import annotations

import json
import pathlib
from collections.abc import Sequence
from dataclasses import dataclass, field

from rig.proc import ApiRequest, GitHubApi, Ran, gh_api
from rig.repo_governance.check_names import (
    discover_pr_check_names,
    lint_check_name,
)
from rig.repo_governance.policy_config import PolicyConfig, load_policy_config
from rig.repo_governance.resource import Change, GitHubReadError, Target, read_json, send
from rig.typed_json import JsonObject, JsonValue, decode_json, encode_json, try_decode_json

HARDCODED_REQUIRED_CHECKS: list[str] = [
    "Lint & Typecheck",
    "Tests & Coverage",
]


RULESET_NAME = "Main Branch Governance Ruleset"


def registered_required_checks(repo_name: str) -> list[str]:
    """Return the required check names `config/repo_policies.json` declares for *repo_name*.

    These are GitHub Actions *job* names (the job's `name:`, falling back to its id), and each must
    name a job in a workflow that triggers on `pull_request`. The ruleset is applied with
    `enforcement: active`, so a required check no job reports blocks every PR in that repo forever.
    The check-name guard (`inspect_check_names`) refuses to enforce such a list; see
    docs/repo-governance/required-checks.md. An unregistered repo gets an empty list, never another
    repo's checks.
    """
    return list(load_policy_config().required_checks(repo_name))


def get_required_checks_for_repo(repo_name: str) -> list[str]:
    """Returns required status check names for a target repository.

    An unregistered repo falls back to this project's legacy check names, which is only correct for a
    repo shaped like this one. Enforcing that fallback against, say, a .NET or Node repo makes it
    permanently unmergeable. Register a repo's `required_checks` in config before enforcing against it.
    """
    return registered_required_checks(repo_name) or list(HARDCODED_REQUIRED_CHECKS)


@dataclass(frozen=True, slots=True)
class RepoSettingsResult:
    """Whether a repo's merge settings match the conventions, from `gh api repos/<repo>`."""

    is_aligned: bool
    delete_on_merge: bool = False
    squash_only: bool = False
    auto_merge: bool = False
    default_branch: str = "main"
    expected_branch: str = "main"
    base_branch_aligned: bool = True
    error: str | None = None


@dataclass(frozen=True, slots=True)
class CheckNamesResult:
    """A repo's required check names against the `pull_request` jobs its workflows declare."""

    status: str
    required: tuple[str, ...]
    declared: dict[str, str]
    missing: tuple[str, ...]
    warnings: dict[str, list[str]]
    registered: bool
    is_aligned: bool


@dataclass(frozen=True, slots=True)
class RulesetInspection:
    """Whether the Main Branch Governance Ruleset is active on a repo."""

    status: str
    is_aligned: bool
    id: int | None = None
    drift: tuple[str, ...] = ()


@dataclass(frozen=True, slots=True)
class AlignResult:
    """Outcome of one GitHub-side alignment step."""

    status: str
    command: str | None = None
    repo: str | None = None
    error: str | None = None
    action: str | None = None
    ruleset: str | None = None
    # Where a live ruleset differs from the declared one: what the update would change.
    drift: tuple[str, ...] = ()


@dataclass(frozen=True, slots=True)
class _RepoSettings:
    default_branch: str = "main"
    delete_branch_on_merge: bool = False
    allow_squash_merge: bool | None = None
    allow_merge_commit: bool | None = None
    allow_rebase_merge: bool | None = None
    allow_auto_merge: bool = False
    private: bool = False


@dataclass(frozen=True, slots=True)
class _Ruleset:
    name: str | None = None
    enforcement: str | None = None
    id: int | None = None


@dataclass(frozen=True, slots=True)
class _StatusCheck:
    context: str = ""


@dataclass(frozen=True, slots=True)
class _RuleParameters:
    """The rule parameters `build_ruleset_payload` declares. GitHub returns more; those are ignored."""

    required_status_checks: list[_StatusCheck] | None = None
    strict_required_status_checks_policy: bool | None = None
    required_approving_review_count: int | None = None
    dismiss_stale_reviews_on_push: bool | None = None
    require_code_owner_review: bool | None = None
    require_last_push_approval: bool | None = None
    required_review_thread_resolution: bool | None = None
    require_extra_approval_for_unattributed_changes: bool | None = None
    check_response_timeout_minutes: int | None = None
    grouping_strategy: str | None = None
    max_entries_to_build: int | None = None
    max_entries_to_merge: int | None = None
    merge_method: str | None = None
    min_entries_to_merge: int | None = None
    min_entries_to_merge_wait_minutes: int | None = None


@dataclass(frozen=True, slots=True)
class _Rule:
    type: str = ""
    parameters: _RuleParameters | None = None


@dataclass(frozen=True, slots=True)
class _RefName:
    include: list[str] = field(default_factory=list)
    exclude: list[str] = field(default_factory=list)


@dataclass(frozen=True, slots=True)
class _Conditions:
    ref_name: _RefName = field(default_factory=_RefName)


@dataclass(frozen=True, slots=True)
class _RulesetRules:
    enforcement: str | None = None
    target: str | None = None
    conditions: _Conditions | None = None
    bypass_actors: list[object] = field(default_factory=list)
    rules: list[_Rule] = field(default_factory=list[_Rule])


def _ruleset_shape(ruleset: _RulesetRules) -> dict[str, str]:
    """{rule type: what it declares}, comparable between the payload and what GitHub holds."""
    shape: dict[str, str] = {}
    for rule in ruleset.rules:
        params = rule.parameters
        if params is None:
            shape[rule.type] = ""
            continue
        checks = sorted(c.context for c in params.required_status_checks or [])
        shape[rule.type] = repr(
            (
                checks,
                params.strict_required_status_checks_policy,
                params.required_approving_review_count,
                params.dismiss_stale_reviews_on_push,
                params.require_code_owner_review,
                params.require_last_push_approval,
                params.required_review_thread_resolution,
                params.require_extra_approval_for_unattributed_changes,
                params.check_response_timeout_minutes,
                params.grouping_strategy,
                params.max_entries_to_build,
                params.max_entries_to_merge,
                params.merge_method,
                params.min_entries_to_merge,
                params.min_entries_to_merge_wait_minutes,
            )
        )
    return shape


def ruleset_drift(repo_name: str, live_json: str, cfg: PolicyConfig | None = None) -> list[str]:
    """What the live ruleset lacks or holds differently from `build_ruleset_payload`, one line each.

    Existence was all `align_github_ruleset` used to check, so a ruleset created once never took a
    later change: design-system's ran for months with no required checks at all, and with an
    extra-approval rule that made its owner's own PRs unmergeable.
    """
    wanted = decode_json(_RulesetRules, json.dumps(build_ruleset_payload(repo_name, cfg)))
    return _drift(wanted, decode_json(_RulesetRules, live_json))


def _drift(wanted: _RulesetRules, detail: _RulesetRules) -> list[str]:
    declared = _ruleset_shape(wanted)
    live = _ruleset_shape(detail)
    drift: list[str] = []
    enforcement = detail.enforcement
    if enforcement != "active":
        drift.append(f"enforcement {enforcement or 'unknown'}")
    if detail.target != wanted.target:
        drift.append("ruleset must target branches")
    if detail.conditions != wanted.conditions:
        drift.append("branch conditions differ from declared coverage")
    if detail.bypass_actors:
        drift.append("bypass actors can skip required conversation resolution")
    for rule_type, want in declared.items():
        have = live.get(rule_type)
        if have is None:
            drift.append(f"missing rule {rule_type}")
        elif have != want:
            drift.append(f"{rule_type}: live {have} != declared {want}")
    drift.extend(f"undeclared rule {rule_type}" for rule_type in live if rule_type not in declared)
    return drift


def build_ruleset_payload(repo_name: str, cfg: PolicyConfig | None = None) -> JsonObject:
    """Dynamically builds Main Branch Governance Ruleset payload with repo-specific required checks.

    It covers the repo's own default branch (`expected_base_branch`; a fork keeps upstream's). On a
    managed fork the stack tool rebuilds that branch on every rebaseline (docs/forks.md), so it is
    not protected against non-fast-forward pushes and needs no pull request.
    """
    cfg = cfg or load_policy_config()
    branch = cfg.expected_base_branch(repo_name)
    is_fork = cfg.fork(repo_name) is not None
    req_checks: list[JsonValue] = [{"context": c} for c in cfg.required_checks(repo_name)]
    status_checks: list[JsonValue] = (
        [
            {
                "type": "required_status_checks",
                "parameters": {
                    "strict_required_status_checks_policy": True,
                    "required_status_checks": req_checks,
                },
            }
        ]
        if req_checks
        else []
    )
    rules: list[JsonValue] = [{"type": "deletion"}]
    if not is_fork:
        rules.append({"type": "non_fast_forward"})
    rules.append({"type": "required_linear_history"})
    if not is_fork:
        extra_approval = cfg.require_extra_approval_for_unattributed_changes(repo_name)
        rules.append(_pull_request_rule(extra_approval))
    rules.extend(status_checks)
    if cfg.merge_queue(repo_name):
        rules.append({
            "type": "merge_queue",
            "parameters": {
                "check_response_timeout_minutes": 60,
                "grouping_strategy": "ALLGREEN",
                "max_entries_to_build": 5,
                "max_entries_to_merge": 5,
                "merge_method": "SQUASH",
                "min_entries_to_merge": 1,
                "min_entries_to_merge_wait_minutes": 1,
            },
        })
    return {
        "name": RULESET_NAME,
        "target": "branch",
        "enforcement": "active",
        "bypass_actors": [],
        "conditions": {"ref_name": {"include": [f"refs/heads/{branch}"], "exclude": []}},
        "rules": rules,
    }


def _pull_request_rule(require_extra_approval: bool = False) -> JsonObject:
    """A fresh `pull_request` rule each call, so a caller editing one payload cannot change the next."""
    return {
        "type": "pull_request",
        "parameters": {
            "required_approving_review_count": 0,
            "dismiss_stale_reviews_on_push": True,
            "require_code_owner_review": False,
            "require_last_push_approval": False,
            "required_review_thread_resolution": True,
            # State every parameter: GitHub defaults anything omitted, so an undeclared one
            # is reset on every `enforce`. This one made design-system#69 unmergeable by
            # anyone with all checks green — see docs/repo-governance/rulesets.md.
            "require_extra_approval_for_unattributed_changes": require_extra_approval,
        },
    }


@dataclass(frozen=True, slots=True)
class RepoSettings:
    """A repo's default branch and merge settings; also the body of the ``PATCH repos/<repo>`` that sets them."""

    default_branch: str
    delete_branch_on_merge: bool
    allow_squash_merge: bool
    allow_merge_commit: bool
    allow_rebase_merge: bool
    allow_auto_merge: bool


def desired_settings(cfg: PolicyConfig, repo_name: str, default_branch: str) -> RepoSettings:
    """The settings `config/repo_policies.json` (`repo_settings`) declares for *repo_name*."""
    merge = cfg.repo_settings(repo_name)
    return RepoSettings(
        default_branch=default_branch,
        delete_branch_on_merge=merge.delete_branch_on_merge,
        allow_squash_merge=merge.allow_squash_merge,
        allow_merge_commit=merge.allow_merge_commit,
        allow_rebase_merge=merge.allow_rebase_merge,
        allow_auto_merge=merge.allow_auto_merge,
    )


def settings_drift(desired: RepoSettings, live: _RepoSettings) -> list[str]:
    """Each setting GitHub holds differently from *desired*, as ``name live -> wanted``.

    GitHub silently ignores ``allow_auto_merge`` on private repos under Free plans (auto-merge
    needs branch protection, which is Pro-only there), so it is not drift on a private repo.
    """
    pairs = {
        "default_branch": (live.default_branch or "main", desired.default_branch),
        "delete_branch_on_merge": (live.delete_branch_on_merge, desired.delete_branch_on_merge),
        "allow_squash_merge": (live.allow_squash_merge, desired.allow_squash_merge),
        "allow_merge_commit": (live.allow_merge_commit, desired.allow_merge_commit),
        "allow_rebase_merge": (live.allow_rebase_merge, desired.allow_rebase_merge),
    }
    drift = [f"{name} {have} -> {want}" for name, (have, want) in pairs.items() if have != want]
    if not live.private and live.allow_auto_merge != desired.allow_auto_merge:
        drift.append(f"allow_auto_merge {live.allow_auto_merge} -> {desired.allow_auto_merge}")
    return drift


def inspect_repo_settings(
    repo_name: str, expected_branch: str = "main", api: GitHubApi = gh_api, cfg: PolicyConfig | None = None
) -> RepoSettingsResult:
    """Inspects current GitHub repo settings through the REST port against the policy's `repo_settings`."""
    try:
        settings = read_json(api, _RepoSettings, f"repos/{repo_name}")
    except GitHubReadError as e:
        return RepoSettingsResult(error=str(e), is_aligned=False)
    default_branch = settings.default_branch or "main"
    drift = settings_drift(desired_settings(cfg or load_policy_config(), repo_name, expected_branch), settings)
    return RepoSettingsResult(
        delete_on_merge=settings.delete_branch_on_merge,
        squash_only=settings.allow_squash_merge is True
        and settings.allow_merge_commit is False
        and settings.allow_rebase_merge is False,
        auto_merge=settings.allow_auto_merge,
        default_branch=default_branch,
        expected_branch=expected_branch,
        base_branch_aligned=default_branch == expected_branch,
        is_aligned=not drift,
    )


def align_github_repo_settings(
    repo_name: str,
    expected_branch: str = "main",
    dry_run: bool = False,
    api: GitHubApi = gh_api,
    cfg: PolicyConfig | None = None,
) -> AlignResult:
    """Sets the policy's merge settings and the default branch with one ``PATCH repos/<repo>``."""
    desired = desired_settings(cfg or load_policy_config(), repo_name, expected_branch)
    body = encode_json(RepoSettings, desired)
    if dry_run:
        return AlignResult(status="dry_run", command=f"PATCH repos/{repo_name} {body}")
    ran = send(api, "PATCH", f"repos/{repo_name}", body)
    if ran.ok:
        return AlignResult(status="success", repo=repo_name)
    return AlignResult(status="error", error=ran.err.strip())


@dataclass(frozen=True, slots=True)
class RepoSettingsResource:
    """Merge settings and default branch (`gh api repos/<repo>`)."""

    name: str = "repo-settings"

    def desired(self, cfg: PolicyConfig, target: Target) -> RepoSettings:
        return desired_settings(cfg, target.slug, cfg.expected_base_branch(target.slug))

    def read(self, api: GitHubApi, target: Target) -> _RepoSettings:
        return read_json(api, _RepoSettings, f"repos/{target.slug}")

    def diff(self, desired: RepoSettings, observed: _RepoSettings) -> list[Change]:
        drift = settings_drift(desired, observed)
        return [Change(self.name, "update", "; ".join(drift))] if drift else []

    def apply(self, api: GitHubApi, target: Target, desired: RepoSettings, changes: Sequence[Change]) -> list[str]:
        if not any(change.kind == "apply" for change in changes):
            return []
        ran = send(api, "PATCH", f"repos/{target.slug}", encode_json(RepoSettings, desired))
        return [] if ran.ok else [f"PATCH repos/{target.slug}: {ran.err.strip()}"]


def inspect_check_names(repo_path: pathlib.Path, repo_name: str, cfg: PolicyConfig | None = None) -> CheckNamesResult:
    """Checks a repo's required check names against its workflows on disk, and against the lexicon.

    `missing` is drift — a required name no `pull_request` job declares — and is an error.
    `warnings` is shape — a name that breaks the lexicon — and is advisory.
    See docs/repo-governance/required-checks.md.
    """
    required = list((cfg or load_policy_config()).required_checks(repo_name))
    declared = discover_pr_check_names(repo_path)
    # Unregistered repositories require no check names until their workflows are registered.
    registered = bool(required)

    if not declared:
        return CheckNamesResult(
            status="no_pr_workflows",
            required=tuple(required),
            declared={},
            missing=(),
            warnings={},
            registered=registered,
            is_aligned=True,
        )

    missing = [name for name in required if name not in declared]
    warnings = {name: lint_check_name(name) for name in required}
    warnings = {name: warns for name, warns in warnings.items() if warns}

    return CheckNamesResult(
        status="drift" if missing else ("warn" if warnings else "aligned"),
        required=tuple(required),
        declared=declared,
        missing=tuple(missing),
        warnings=warnings,
        registered=registered,
        is_aligned=not missing,
    )


#: What GitHub answers a rulesets call on a private repo whose plan does not serve them.
_RULESETS_UNSERVED = "Upgrade to GitHub Pro or make this repository public"


@dataclass(frozen=True, slots=True)
class LiveRuleset:
    """The managed ruleset as GitHub holds it. ``rules`` is None when it does not exist yet."""

    served: bool = True
    id: int | None = None
    rules: _RulesetRules | None = None


@dataclass(frozen=True, slots=True)
class DeclaredRuleset:
    """The ruleset a repo should carry, and why it must not be written when the check-name guard refuses."""

    payload: str
    rules: _RulesetRules
    refusal: str | None = None


def read_ruleset(api: GitHubApi, repo_name: str) -> LiveRuleset:
    """Read the managed ruleset; raises :class:`GitHubReadError` when GitHub cannot be read."""
    listed = api(ApiRequest("GET", f"repos/{repo_name}/rulesets"))
    if not listed.ok:
        if _RULESETS_UNSERVED in listed.err:
            return LiveRuleset(served=False)
        raise GitHubReadError(f"GET repos/{repo_name}/rulesets: {listed.err.strip() or f'exit {listed.code}'}")
    rulesets = try_decode_json(list[_Ruleset], listed.out)
    if rulesets is None:
        raise GitHubReadError(f"GET repos/{repo_name}/rulesets: unreadable response")
    named = [r for r in rulesets if r.name == RULESET_NAME and r.id is not None]
    if not named:
        return LiveRuleset()
    chosen = next((r for r in named if r.enforcement == "active"), named[0])
    return LiveRuleset(id=chosen.id, rules=read_json(api, _RulesetRules, f"repos/{repo_name}/rulesets/{chosen.id}"))


def check_name_refusal(repo_path: pathlib.Path, repo_name: str, cfg: PolicyConfig) -> str | None:
    """Why the ruleset must not be written, or None. The check-name guard, on every enforcement path.

    A required check no `pull_request` job reports blocks every PR, and undoing it means editing the
    ruleset in the GitHub UI. A repo with no registered checks gets no status-checks rule, so there is
    nothing to verify. See docs/repo-governance/required-checks.md.
    """
    if not cfg.required_checks(repo_name):
        return None
    names = inspect_check_names(repo_path, repo_name, cfg)
    if names.status == "no_pr_workflows" and names.required:
        return f"required checks {list(names.required)}: no pull_request workflow exists to report them"
    if names.missing:
        declared = sorted(names.declared)
        return f"required checks {list(names.missing)}: no pull_request job declares them (declared: {declared})"
    return None


def inspect_github_ruleset(repo_name: str, api: GitHubApi = gh_api) -> RulesetInspection:
    """Read the managed ruleset and check its enforcement and declared rules."""
    try:
        live = read_ruleset(api, repo_name)
    except GitHubReadError:
        return RulesetInspection(status="error", is_aligned=False)
    if not live.served:
        return RulesetInspection(status="N/A (Private Repo)", is_aligned=True)
    if live.rules is None:
        return RulesetInspection(status="MISSING", is_aligned=False)
    wanted = decode_json(_RulesetRules, json.dumps(build_ruleset_payload(repo_name)))
    drift = tuple(_drift(wanted, live.rules))
    return RulesetInspection(
        status=f"DRIFTED: {'; '.join(drift)}" if drift else "ACTIVE",
        is_aligned=not drift,
        id=live.id,
        drift=drift,
    )


def _write_ruleset(api: GitHubApi, repo_name: str, ruleset_id: int | None, payload: str) -> Ran:
    """Create the ruleset, or replace it in place when it exists, so a drifted one is never duplicated."""
    if ruleset_id is None:
        return send(api, "POST", f"repos/{repo_name}/rulesets", payload)
    return send(api, "PUT", f"repos/{repo_name}/rulesets/{ruleset_id}", payload)


def align_github_ruleset(repo_name: str, dry_run: bool = False, api: GitHubApi = gh_api) -> AlignResult:
    """Applies the Main Branch Governance Ruleset, or updates it where it has drifted from the declared one."""
    existing = inspect_github_ruleset(repo_name, api)
    if existing.status == "N/A (Private Repo)":
        return AlignResult(status="skipped (private repo restriction)")
    if existing.status == "error":
        return AlignResult(status="error", error="could not read the existing rulesets")
    if existing.is_aligned:
        return AlignResult(status="already_active")
    if dry_run:
        return AlignResult(status="dry_run", action="PUT" if existing.id is not None else "POST", drift=existing.drift)
    ran = _write_ruleset(api, repo_name, existing.id, json.dumps(build_ruleset_payload(repo_name)))
    if ran.ok:
        return AlignResult(status="success", ruleset=RULESET_NAME)
    if _RULESETS_UNSERVED in ran.err:
        return AlignResult(status="skipped (private repo restriction)")
    return AlignResult(status="error", error=ran.err.strip())


@dataclass(frozen=True, slots=True)
class RulesetResource:
    """The Main Branch Governance Ruleset, behind the check-name guard."""

    name: str = "ruleset"

    def desired(self, cfg: PolicyConfig, target: Target) -> DeclaredRuleset:
        payload = json.dumps(build_ruleset_payload(target.slug, cfg))
        return DeclaredRuleset(
            payload=payload,
            rules=decode_json(_RulesetRules, payload),
            refusal=check_name_refusal(target.path, target.slug, cfg),
        )

    def read(self, api: GitHubApi, target: Target) -> LiveRuleset:
        return read_ruleset(api, target.slug)

    def diff(self, desired: DeclaredRuleset, observed: LiveRuleset) -> list[Change]:
        if not observed.served:
            return [Change(self.name, "unserved", "rulesets are not served for this private repo's plan", kind="note")]
        if desired.refusal is not None:
            return [Change(self.name, "refused", desired.refusal, kind="refused")]
        if observed.rules is None:
            return [Change(self.name, "create", f"{RULESET_NAME} is missing")]
        drift = _drift(desired.rules, observed.rules)
        return [Change(self.name, "update", "; ".join(drift), subject=str(observed.id))] if drift else []

    def apply(self, api: GitHubApi, target: Target, desired: DeclaredRuleset, changes: Sequence[Change]) -> list[str]:
        failures: list[str] = []
        for change in changes:
            if change.kind != "apply":
                continue
            ruleset_id = int(change.subject) if change.subject else None
            ran = _write_ruleset(api, target.slug, ruleset_id, desired.payload)
            if not ran.ok:
                failures.append(f"{change.action} {RULESET_NAME} on {target.slug}: {ran.err.strip()}")
        return failures
