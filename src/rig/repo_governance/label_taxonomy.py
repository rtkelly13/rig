"""GitHub label governance: holds repos to the `labels` taxonomy in `config/repo_policies.json`.

Labels outside the taxonomy are left alone; a taxonomy label that is missing is created, and one whose
colour or description differs is updated.
"""

from __future__ import annotations

from collections.abc import Sequence
from dataclasses import dataclass
from urllib.parse import quote

from pydantic import BaseModel, Field, StrictStr

from rig.proc import GitHubApi, gh_api
from rig.repo_governance.policy_config import LabelSpec, PolicyConfig, load_policy_config
from rig.repo_governance.resource import Change, GitHubReadError, Target, read_json, send
from rig.repo_governance.sync_outcome import SyncOutcome
from rig.typed_json import encode_json


@dataclass(frozen=True, slots=True)
class LabelDifference:
    label_name: str
    action: str  # "create" or "update"
    details: str
    color: str
    description: str


class LabelAuditResult(BaseModel):
    repo_name: str
    total_standard: int
    missing: list[str] = Field(default_factory=list[str])
    mismatched: list[str] = Field(default_factory=list[str])
    aligned: list[str] = Field(default_factory=list[str])
    differences: list[LabelDifference] = Field(default_factory=list[LabelDifference])

    @property
    def is_fully_aligned(self) -> bool:
        return len(self.missing) == 0 and len(self.mismatched) == 0


@dataclass(frozen=True, slots=True)
class RepoLabel:
    """A label as it exists on a repository, normalised for comparison against the taxonomy."""

    color: str
    description: str


@dataclass(frozen=True, slots=True)
class _GhLabel:
    name: StrictStr
    color: StrictStr | None = None
    description: StrictStr | None = None


@dataclass(frozen=True, slots=True)
class _LabelUpdate:
    """The body of a label update; the route names the label."""

    color: str
    description: str


@dataclass(frozen=True, slots=True)
class _NewLabel:
    name: str
    color: str
    description: str


#: GitHub's largest page for the labels endpoint.
_PAGE_SIZE = 100


def read_repo_labels(api: GitHubApi, repo_name: str) -> dict[str, RepoLabel]:
    """Every label on *repo_name*; raises :class:`GitHubReadError` when they cannot be read.

    Pages are read until a short one: labels outside the taxonomy are kept, so a repo can carry
    more than one page, and a governed label on a later page must not read as missing.
    """
    route = f"repos/{repo_name}/labels?per_page={_PAGE_SIZE}"
    labels = read_json(api, list[_GhLabel], route)
    page = labels
    number = 1
    while len(page) == _PAGE_SIZE:
        number += 1
        page = read_json(api, list[_GhLabel], f"{route}&page={number}")
        labels.extend(page)
    return {
        label.name: RepoLabel(color=(label.color or "").lower(), description=label.description or "")
        for label in labels
    }


def fetch_repo_labels(repo_name: str, api: GitHubApi = gh_api) -> dict[str, RepoLabel]:
    """:func:`read_repo_labels`, reading an unreadable list as no labels."""
    try:
        return read_repo_labels(api, repo_name)
    except GitHubReadError:
        return {}


def _write_label(api: GitHubApi, repo_name: str, diff: LabelDifference) -> str | None:
    """Create or update one label; returns why it failed, or None."""
    if diff.action == "create":
        created = _NewLabel(name=diff.label_name, color=diff.color, description=diff.description)
        ran = send(api, "POST", f"repos/{repo_name}/labels", encode_json(_NewLabel, created))
    else:
        updated = _LabelUpdate(color=diff.color, description=diff.description)
        route = f"repos/{repo_name}/labels/{quote(diff.label_name, safe='')}"
        ran = send(api, "PATCH", route, encode_json(_LabelUpdate, updated))
    return None if ran.ok else ran.err.strip()


def label_differences(taxonomy: Sequence[LabelSpec], labels: dict[str, RepoLabel]) -> list[LabelDifference]:
    """What it takes to bring *labels* up to *taxonomy*; labels outside the taxonomy are left alone."""
    differences: list[LabelDifference] = []
    for std in taxonomy:
        current = labels.get(std.name)
        if current is None:
            differences.append(
                LabelDifference(std.name, "create", "Label missing from repository", std.color, std.description)
            )
            continue
        reasons: list[str] = []
        if current.color.lower() != std.color.lower():
            reasons.append(f"color mismatch (#{current.color.lower()} vs #{std.color.lower()})")
        if current.description != std.description:
            reasons.append("description mismatch")
        if reasons:
            differences.append(LabelDifference(std.name, "update", ", ".join(reasons), std.color, std.description))
    return differences


def inspect_repo_labels(
    repo_name: str,
    existing_labels: dict[str, RepoLabel] | None = None,
    taxonomy: Sequence[LabelSpec] | None = None,
) -> LabelAuditResult:
    """Audits repository labels against the repo's taxonomy (from the policy unless given)."""
    wanted = taxonomy if taxonomy is not None else load_policy_config().labels(repo_name)
    labels = existing_labels if existing_labels is not None else fetch_repo_labels(repo_name)
    differences = label_differences(wanted, labels)
    changed = {d.label_name for d in differences}
    return LabelAuditResult(
        repo_name=repo_name,
        total_standard=len(wanted),
        missing=[d.label_name for d in differences if d.action == "create"],
        mismatched=[d.label_name for d in differences if d.action == "update"],
        aligned=[label.name for label in wanted if label.name not in changed],
        differences=differences,
    )


def sync_repo_labels(
    repo_name: str,
    dry_run: bool = False,
    existing_labels: dict[str, RepoLabel] | None = None,
    api: GitHubApi = gh_api,
    taxonomy: Sequence[LabelSpec] | None = None,
) -> SyncOutcome:
    """Synchronizes labels in the target repository to match its taxonomy."""
    labels = existing_labels if existing_labels is not None else fetch_repo_labels(repo_name, api)
    audit = inspect_repo_labels(repo_name, labels, taxonomy)
    if audit.is_fully_aligned:
        return SyncOutcome(changed=False, messages=["All standard labels already aligned."])

    messages: list[str] = []
    for diff in audit.differences:
        if dry_run:
            if diff.action == "create":
                messages.append(f"[Dry Run] Would create label '{diff.label_name}' (#{diff.color})")
            else:
                messages.append(f"[Dry Run] Would update label '{diff.label_name}' ({diff.details})")
            continue
        error = _write_label(api, repo_name, diff)
        if error is not None:
            messages.append(f"Failed to {diff.action} '{diff.label_name}': {error}")
        elif diff.action == "create":
            messages.append(f"Created label '{diff.label_name}' (#{diff.color})")
        else:
            messages.append(f"Updated label '{diff.label_name}'")

    return SyncOutcome(changed=True, messages=messages)


@dataclass(frozen=True, slots=True)
class LabelsResource:
    """The `labels` taxonomy; a repo whose policy declares none is left alone."""

    name: str = "labels"

    def desired(self, cfg: PolicyConfig, target: Target) -> tuple[LabelSpec, ...] | None:
        return cfg.labels(target.slug) or None

    def read(self, api: GitHubApi, target: Target) -> dict[str, RepoLabel]:
        return read_repo_labels(api, target.slug)

    def diff(self, desired: tuple[LabelSpec, ...], observed: dict[str, RepoLabel]) -> list[Change]:
        return [
            Change(self.name, d.action, d.details, subject=d.label_name) for d in label_differences(desired, observed)
        ]

    def apply(
        self, api: GitHubApi, target: Target, desired: tuple[LabelSpec, ...], changes: Sequence[Change]
    ) -> list[str]:
        wanted = {label.name: label for label in desired}
        failures: list[str] = []
        for change in changes:
            label = wanted[change.subject]
            diff = LabelDifference(label.name, change.action, change.detail, label.color, label.description)
            error = _write_label(api, target.slug, diff)
            if error is not None:
                failures.append(f"{change.action} label '{label.name}' on {target.slug}: {error}")
        return failures
