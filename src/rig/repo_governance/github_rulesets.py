from __future__ import annotations

import json
import pathlib
from dataclasses import dataclass, field

from pydantic import ValidationError

from rig import proc
from rig.proc import ProcError
from rig.repo_governance.check_names import (
    discover_pr_check_names,
    lint_check_name,
)
from rig.repo_governance.policy_config import load_policy_config
from rig.typed_json import JsonObject, JsonValue, decode_json

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
            )
        )
    return shape


def ruleset_drift(repo_name: str, live_json: str) -> list[str]:
    """What the live ruleset lacks or holds differently from `build_ruleset_payload`, one line each.

    Existence was all `align_github_ruleset` used to check, so a ruleset created once never took a
    later change: design-system's ran for months with no required checks at all, and with an
    extra-approval rule that made its owner's own PRs unmergeable.
    """
    wanted = decode_json(_RulesetRules, json.dumps(build_ruleset_payload(repo_name)))
    declared = _ruleset_shape(wanted)
    detail = decode_json(_RulesetRules, live_json)
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


def build_ruleset_payload(repo_name: str) -> JsonObject:
    """Dynamically builds Main Branch Governance Ruleset payload with repo-specific required checks.

    It covers the repo's own default branch (`expected_base_branch`; a fork keeps upstream's). On a
    managed fork the stack tool rebuilds that branch on every rebaseline (docs/forks.md), so it is
    not protected against non-fast-forward pushes and needs no pull request.
    """
    cfg = load_policy_config()
    branch = cfg.expected_base_branch(repo_name)
    is_fork = cfg.fork(repo_name) is not None
    req_checks: list[JsonValue] = [{"context": c} for c in registered_required_checks(repo_name)]
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
        rules.append(_pull_request_rule())
    rules.extend(status_checks)
    return {
        "name": RULESET_NAME,
        "target": "branch",
        "enforcement": "active",
        "bypass_actors": [],
        "conditions": {"ref_name": {"include": [f"refs/heads/{branch}"], "exclude": []}},
        "rules": rules,
    }


def _pull_request_rule() -> JsonObject:
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
            "require_extra_approval_for_unattributed_changes": False,
        },
    }


def inspect_repo_settings(repo_name: str, expected_branch: str = "main") -> RepoSettingsResult:
    """Inspects current GitHub repo settings via gh CLI."""
    try:
        res = proc.gh("api", f"repos/{repo_name}", check=True)
        settings = decode_json(_RepoSettings, res.out)

        default_branch = settings.default_branch or "main"
        base_branch_aligned = default_branch == expected_branch
        delete_on_merge = settings.delete_branch_on_merge
        squash_only = (
            settings.allow_squash_merge is True
            and settings.allow_merge_commit is False
            and settings.allow_rebase_merge is False
        )

        auto_merge = settings.allow_auto_merge
        # GitHub silently ignores allow_auto_merge on private repos under Free plans
        # (auto-merge requires branch protection, which is Pro-only for private repos).
        auto_merge_ok = auto_merge or settings.private

        is_aligned = delete_on_merge and squash_only and auto_merge_ok and base_branch_aligned
        return RepoSettingsResult(
            delete_on_merge=delete_on_merge,
            squash_only=squash_only,
            auto_merge=auto_merge,
            default_branch=default_branch,
            expected_branch=expected_branch,
            base_branch_aligned=base_branch_aligned,
            is_aligned=is_aligned,
        )
    except (ProcError, ValidationError) as e:
        return RepoSettingsResult(error=str(e), is_aligned=False)


def align_github_repo_settings(repo_name: str, expected_branch: str = "main", dry_run: bool = False) -> AlignResult:
    """Applies repo settings via gh repo edit (squash, delete-on-merge, auto-merge, default branch)."""
    cmd = [
        "gh",
        "repo",
        "edit",
        repo_name,
        "--delete-branch-on-merge=true",
        "--enable-auto-merge=true",
        "--enable-squash-merge=true",
        "--enable-merge-commit=false",
        "--enable-rebase-merge=false",
        f"--default-branch={expected_branch}",
    ]
    if dry_run:
        return AlignResult(status="dry_run", command=" ".join(cmd))

    try:
        proc.gh(*cmd[1:], check=True)
        return AlignResult(status="success", repo=repo_name)
    except ProcError as e:
        return AlignResult(status="error", error=e.ran.err.strip())


def inspect_check_names(repo_path: pathlib.Path, repo_name: str) -> CheckNamesResult:
    """Checks a repo's required check names against its workflows on disk, and against the lexicon.

    `missing` is drift — a required name no `pull_request` job declares — and is an error.
    `warnings` is shape — a name that breaks the lexicon — and is advisory.
    See docs/repo-governance/required-checks.md.
    """
    required = registered_required_checks(repo_name)
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


def inspect_github_ruleset(repo_name: str) -> RulesetInspection:
    """Read the managed ruleset and check its enforcement and declared rules."""
    try:
        res = proc.gh("api", f"repos/{repo_name}/rulesets", check=True)
        named = [r for r in decode_json(list[_Ruleset], res.out) if r.name == RULESET_NAME and r.id is not None]
        if not named:
            return RulesetInspection(status="MISSING", is_aligned=False)
        chosen = next((r for r in named if r.enforcement == "active"), named[0])
        detail = proc.gh("api", f"repos/{repo_name}/rulesets/{chosen.id}", check=True)
        drift = tuple(ruleset_drift(repo_name, detail.out))
        return RulesetInspection(
            status=f"DRIFTED: {'; '.join(drift)}" if drift else "ACTIVE",
            is_aligned=not drift,
            id=chosen.id,
            drift=drift,
        )
    except ProcError as e:
        if "Upgrade to GitHub Pro or make this repository public" in e.ran.err:
            return RulesetInspection(status="N/A (Private Repo)", is_aligned=True)
        return RulesetInspection(status="error", is_aligned=False)
    except ValidationError:
        return RulesetInspection(status="error", is_aligned=False)


def align_github_ruleset(repo_name: str, dry_run: bool = False) -> AlignResult:
    """Applies the Main Branch Governance Ruleset, or updates it where it has drifted from the declared one."""
    existing = inspect_github_ruleset(repo_name)
    if existing.status == "N/A (Private Repo)":
        return AlignResult(status="skipped (private repo restriction)")

    if existing.status == "error":
        return AlignResult(status="error", error="could not read the existing rulesets")
    if existing.is_aligned:
        return AlignResult(status="already_active")
    cmd_type = "PUT" if existing.id is not None else "POST"
    api_endpoint = f"repos/{repo_name}/rulesets"
    if existing.id is not None:
        api_endpoint += f"/{existing.id}"
    drift = existing.drift

    if dry_run:
        return AlignResult(status="dry_run", action=cmd_type, drift=tuple(drift))

    try:
        proc.gh(
            "api",
            "-X",
            cmd_type,
            api_endpoint,
            "--input",
            "-",
            check=True,
            stdin_text=json.dumps(build_ruleset_payload(repo_name)),
        )
        return AlignResult(status="success", ruleset=RULESET_NAME)
    except ProcError as e:
        if "Upgrade to GitHub Pro or make this repository public" in e.ran.err:
            return AlignResult(status="skipped (private repo restriction)")
        return AlignResult(status="error", error=e.ran.err.strip())
