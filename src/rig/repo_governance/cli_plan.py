"""`repo-governance plan`: every GitHub-side resource, desired → observed → plan → apply.

Without ``--apply`` the command only reads. Exit codes: 0 when every repo matches its policy, 1 when
something differs (or a safety gate refuses a write), 2 when GitHub could not be read or a write failed.

Registered on the ``repo-governance`` app by :mod:`.align_repo_conventions`.
"""

from __future__ import annotations

import contextlib
import pathlib
import sys
from collections.abc import Callable, Sequence
from dataclasses import dataclass

import typer
from rich.table import Table

from rig.proc import GitHubApi, gh_api
from rig.repo_governance.cli_common import console
from rig.repo_governance.github_rulesets import RepoSettingsResource, RulesetResource
from rig.repo_governance.label_taxonomy import LabelsResource
from rig.repo_governance.pages_manager import PagesResource
from rig.repo_governance.policy_config import PolicyConfig, load_policy_config
from rig.repo_governance.release_environments import EnvironmentsResource
from rig.repo_governance.repo_discovery import get_gh_repo_name, inspect_repo_owner, resolve_target_directories
from rig.repo_governance.resource import Change, Planned, Planner, RepoPlan, Target, bind
from rig.typed_json import encode_json

__all__ = ["RESOURCES", "PlanContext", "app", "plan_repo"]

app = typer.Typer()

#: Every resource `plan` walks, in the order it applies them: settings before the ruleset that names
#: the default branch, environments before anything that deploys.
RESOURCES: tuple[Planner, ...] = (
    bind(RepoSettingsResource()),
    bind(RulesetResource()),
    bind(EnvironmentsResource()),
    bind(LabelsResource()),
    bind(PagesResource()),
)

_KIND_STYLES = {"apply": "yellow", "refused": "bold red", "note": "dim"}


@dataclass(frozen=True, slots=True)
class PlanContext:
    """The command's ports. Tests pass their own as the Typer context object."""

    api: GitHubApi = gh_api
    slug_of: Callable[[pathlib.Path], str | None] = get_gh_repo_name
    config: Callable[[], PolicyConfig] = load_policy_config


def plan_repo(
    cfg: PolicyConfig,
    api: GitHubApi,
    target: Target,
    *,
    apply: bool = False,
    planners: Sequence[Planner] = RESOURCES,
) -> RepoPlan:
    """Plan every resource for *target*; with *apply*, make the planned writes and record any that failed.

    A repo whose owner breaks the owner policy is refused outright: nothing is read or written.
    """
    owner = inspect_repo_owner(target.path, repo_name=target.slug, config=cfg)
    if not owner.is_aligned:
        refusal = Change("owner", "refused", f"owner governance failure: {owner.message}", kind="refused")
        return RepoPlan(target.slug, [refusal])

    planned: list[Planned] = [planner.plan(cfg, api, target) for planner in planners]
    changes = [change for p in planned for change in p.changes]
    errors = [f"{p.resource}: {p.error}" for p in planned if p.error is not None]
    if apply:
        errors.extend(failure for p in planned for failure in p.apply())
    return RepoPlan(target.slug, changes, errors)


def _render(plans: Sequence[RepoPlan], applied: bool) -> None:
    table = Table(title="Governance plan (applied)" if applied else "Governance plan")
    table.add_column("Repo", style="bold white")
    table.add_column("Resource", style="cyan")
    table.add_column("Action")
    table.add_column("Subject", style="dim")
    table.add_column("Detail")
    for plan in plans:
        for change in plan.changes:
            style = _KIND_STYLES[change.kind]
            table.add_row(plan.repo, change.resource, f"[{style}]{change.action}[/]", change.subject, change.detail)
        for error in plan.errors:
            table.add_row(plan.repo, "", "[bold red]error[/]", "", error)
    if table.row_count:
        console.print(table)
    else:
        console.print("[green]Every repo matches its policy.[/green]")


def _exit_code(plans: Sequence[RepoPlan], applied: bool) -> int:
    if any(plan.errors for plan in plans):
        return 2
    if applied:
        # The writes were made; what is left is what a gate refused.
        return 1 if any(c.kind == "refused" for plan in plans for c in plan.changes) else 0
    return 1 if any(plan.drifted for plan in plans) else 0


@app.command("plan")
def plan_command(
    ctx: typer.Context,
    path: str | None = typer.Argument(None, help="Target repo directory (defaults to current directory)"),
    all: bool = typer.Option(False, "--all", "-a", help="Plan every managed repo under <code root>/personal"),
    org: str | None = typer.Option(None, "--org", "-o", help="Target GitHub organization (e.g. rtkelly-labs)"),
    apply: bool = typer.Option(False, "--apply", help="Make the planned writes on GitHub"),
    json_output: bool = typer.Option(False, "--json", help="Emit the plan as JSON"),
) -> None:
    """Plan repo settings, the ruleset, environments, labels and Pages against `config/repo_policies.json`.

    Read-only unless `--apply`. A required check that no workflow reports is refused, never written:
    it would block every PR (see `check-names`).
    """
    ports = ctx.find_object(PlanContext) or PlanContext()
    # Repo selection narrates what it skipped; with --json, stdout belongs to the payload.
    with contextlib.redirect_stdout(sys.stderr) if json_output else contextlib.nullcontext():
        directories = resolve_target_directories(path, all, org=org)
    cfg = ports.config()

    plans: list[RepoPlan] = []
    for directory in directories:
        slug = ports.slug_of(directory)
        if slug is None:
            plans.append(RepoPlan(directory.name, errors=["no GitHub repository found for this checkout"]))
            continue
        plans.append(plan_repo(cfg, ports.api, Target(directory, slug), apply=apply))

    if json_output:
        typer.echo(encode_json(list[RepoPlan], plans))
    else:
        _render(plans, apply)
    raise typer.Exit(code=_exit_code(plans, apply))
