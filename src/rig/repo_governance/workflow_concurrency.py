"""Lint and enforce GitHub Actions workflow concurrency across estate repositories."""

from __future__ import annotations

import pathlib
from dataclasses import dataclass
from typing import Annotated

import yaml
from pydantic import AliasChoices, Field

from rig.repo_governance.workflow_yaml import Triggers, workflow_keys
from rig.typed_json import Lenient, is_object, try_decode
from rig.typed_yaml import load_yaml


@dataclass(frozen=True, slots=True)
class ConcurrencyIssue:
    repo_name: str
    workflow_path: pathlib.Path
    message: str


@dataclass(frozen=True, slots=True)
class _WorkflowConcurrency:
    group: Lenient[str] = None
    cancel_in_progress: Annotated[
        Lenient[bool], Field(validation_alias=AliasChoices("cancel-in-progress", "cancel_in_progress"))
    ] = None


@dataclass(frozen=True, slots=True)
class _WorkflowDef:
    on: Lenient[Triggers] = None
    #: A mapping, a bare group name, or absent; only a mapping can say whether superseded runs are cancelled.
    concurrency: object = None


def inspect_workflow_concurrency(workflow_path: pathlib.Path) -> list[str]:
    """Inspect whether a workflow triggered by pull_request has cancel-in-progress concurrency configured."""
    try:
        content = workflow_path.read_text(encoding="utf-8")
        loaded = load_yaml(content)
    except (yaml.YAMLError, OSError):
        return []

    wf = try_decode(_WorkflowDef, workflow_keys(loaded))
    if wf is None or wf.on is None or "pull_request" not in wf.on:
        return []

    if wf.concurrency is None:
        return ["missing top-level 'concurrency' block (runs continue billing when superseded)"]

    parsed_c = try_decode(_WorkflowConcurrency, workflow_keys(wf.concurrency)) if is_object(wf.concurrency) else None
    if parsed_c is not None and not parsed_c.cancel_in_progress:
        return ["concurrency block is missing 'cancel-in-progress: true' or dynamic PR cancellation"]

    return []


def audit_repo_workflow_concurrency(repo_dir: pathlib.Path) -> list[ConcurrencyIssue]:
    """Audit all GitHub Actions workflows in a repository."""
    workflows_dir = repo_dir / ".github" / "workflows"
    if not workflows_dir.is_dir():
        return []

    issues: list[ConcurrencyIssue] = []
    for wf in sorted(workflows_dir.glob("*.y*ml")):
        reasons = inspect_workflow_concurrency(wf)
        for reason in reasons:
            issues.append(ConcurrencyIssue(repo_name=repo_dir.name, workflow_path=wf, message=reason))

    return issues
