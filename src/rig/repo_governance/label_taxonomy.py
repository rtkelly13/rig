"""Standard GitHub Label Taxonomy and Synchronization Engine.

Reconciles repository labels across open source repositories to enforce
consistent naming, descriptions, and hex colors.
"""

from __future__ import annotations

from dataclasses import dataclass

from pydantic import BaseModel, Field, StrictStr

from rig import proc
from rig.proc import ProcError
from rig.repo_governance.sync_outcome import SyncOutcome
from rig.typed_json import decode_json


class StandardLabel(BaseModel):
    name: str
    color: str  # 6-char hex without #
    description: str


STANDARD_LABEL_TAXONOMY: list[StandardLabel] = [
    # Types
    StandardLabel(name="type: bug", color="d73a4a", description="Something is broken or behaving unexpectedly"),
    StandardLabel(name="type: feature", color="a2eeef", description="New feature or capability request"),
    StandardLabel(name="type: docs", color="0075ca", description="Documentation improvements, typo fixes, or guides"),
    StandardLabel(
        name="type: performance", color="ffc107", description="Performance optimization or memory improvement"
    ),
    StandardLabel(
        name="type: maintenance", color="e4e669", description="Chore, dependency update, tooling, or refactor"
    ),
    # Status
    StandardLabel(
        name="status: needs-repro",
        color="e99695",
        description="Cannot reproduce; waiting on submitter for minimal example",
    ),
    StandardLabel(
        name="status: blocked", color="6a737d", description="Blocked by upstream dependency or external issue"
    ),
    StandardLabel(name="status: in-progress", color="fbca04", description="Actively being addressed by a contributor"),
    # Community & Discovery
    StandardLabel(
        name="good first issue", color="7057ff", description="Friendly, self-contained entry point for new contributors"
    ),
    StandardLabel(name="help wanted", color="008672", description="Extra attention or community contribution welcome"),
    # Impact
    StandardLabel(
        name="breaking-change", color="b60205", description="Backwards-incompatible API or behavioral change"
    ),
    # Merge gate: a repo whose `regression` status runs the full suite only once a PR carries this.
    StandardLabel(
        name="ready-to-merge",
        color="0e8a16",
        description="Ready to merge: runs the full regression suite (required status: regression)",
    ),
]


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


def fetch_repo_labels(repo_name: str) -> dict[str, RepoLabel]:
    """Fetches all existing labels for a repository via gh CLI."""
    try:
        res = proc.gh("api", f"repos/{repo_name}/labels", "--paginate", check=True)
        labels = decode_json(list[_GhLabel], res.out)
        return {
            label.name: RepoLabel(color=(label.color or "").lower(), description=label.description or "")
            for label in labels
        }
    except (ProcError, ValueError, LookupError):
        return {}


def inspect_repo_labels(
    repo_name: str,
    existing_labels: dict[str, RepoLabel] | None = None,
) -> LabelAuditResult:
    """Audits repository labels against the standard taxonomy."""
    labels = existing_labels if existing_labels is not None else fetch_repo_labels(repo_name)
    missing: list[str] = []
    mismatched: list[str] = []
    aligned: list[str] = []
    differences: list[LabelDifference] = []

    for std in STANDARD_LABEL_TAXONOMY:
        if std.name not in labels:
            missing.append(std.name)
            differences.append(
                LabelDifference(
                    label_name=std.name,
                    action="create",
                    details="Label missing from repository",
                    color=std.color,
                    description=std.description,
                )
            )
        else:
            curr = labels[std.name]
            curr_color = curr.color.lower()
            std_color = std.color.lower()
            curr_desc = curr.description

            if curr_color != std_color or curr_desc != std.description:
                mismatched.append(std.name)
                diff_reasons: list[str] = []
                if curr_color != std_color:
                    diff_reasons.append(f"color mismatch (#{curr_color} vs #{std_color})")
                if curr_desc != std.description:
                    diff_reasons.append("description mismatch")
                differences.append(
                    LabelDifference(
                        label_name=std.name,
                        action="update",
                        details=", ".join(diff_reasons),
                        color=std.color,
                        description=std.description,
                    )
                )
            else:
                aligned.append(std.name)

    return LabelAuditResult(
        repo_name=repo_name,
        total_standard=len(STANDARD_LABEL_TAXONOMY),
        missing=missing,
        mismatched=mismatched,
        aligned=aligned,
        differences=differences,
    )


def sync_repo_labels(
    repo_name: str,
    dry_run: bool = False,
    existing_labels: dict[str, RepoLabel] | None = None,
) -> SyncOutcome:
    """Synchronizes labels in the target repository to match the standard taxonomy."""
    audit = inspect_repo_labels(repo_name, existing_labels)
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

        try:
            if diff.action == "create":
                proc.gh(
                    "api",
                    "--method",
                    "POST",
                    f"repos/{repo_name}/labels",
                    "-f",
                    f"name={diff.label_name}",
                    "-f",
                    f"color={diff.color}",
                    "-f",
                    f"description={diff.description}",
                    check=True,
                )
                messages.append(f"Created label '{diff.label_name}' (#{diff.color})")
            else:
                proc.gh(
                    "api",
                    "--method",
                    "PATCH",
                    f"repos/{repo_name}/labels/{diff.label_name}",
                    "-f",
                    f"color={diff.color}",
                    "-f",
                    f"description={diff.description}",
                    check=True,
                )
                messages.append(f"Updated label '{diff.label_name}'")
        except ProcError as e:
            messages.append(f"Failed to {diff.action} '{diff.label_name}': {e.ran.err.strip()}")

    return SyncOutcome(changed=True, messages=messages)
