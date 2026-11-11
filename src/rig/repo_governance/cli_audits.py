"""Vercel gating and Vercel project audits.

Registered on the ``repo-governance`` app by :mod:`.align_repo_conventions`.
"""

import os
import pathlib

import typer
from rich.markup import escape
from rich.table import Table

from rig.repo_governance.cli_common import SEVERITY_STYLES, console
from rig.repo_governance.policy_config import (
    load_policy_config,
)
from rig.repo_governance.repo_discovery import (
    personal_repos_dir,
    resolve_target_directories,
)
from rig.repo_governance.vercel_gating import audit_projects

app = typer.Typer()


@app.command(name="vercel-projects")
def vercel_projects_command() -> None:
    """Audits which repository each Vercel project belongs to, against `vercel` in managed-projects.json.

    Each project's repository comes from Vercel: its git link, or for a project
    deployed from the CLI, the managed repo on GitHub holding its newest
    deployment's commit. Errors: a project nobody declares, one declared under
    the wrong repo, or a declared name Vercel does not have. Warnings: a git
    link still naming a repo's old owner, or a project with no link or commit,
    which rests on its declaration. Reports only; edit the config to fix.
    """
    from rig.repo_governance.vercel_projects import gather, plan_findings
    from rig.workspace.engine import config_file_path
    from rig.workspace.vercel_names import load_repo_slugs, load_vercel_claims

    team_id = load_policy_config().vercel_gating("").team_id or os.environ.get("VERCEL_TEAM_ID", "")
    if not team_id:
        console.print("[bold red]❌ No Vercel team id: set defaults.vercel.team_id or VERCEL_TEAM_ID[/bold red]")
        raise typer.Exit(2)
    cfg = config_file_path()
    claims, repos = load_vercel_claims(cfg), load_repo_slugs(cfg)
    try:
        findings = plan_findings(gather(team_id, claims, repos), claims, repos)
    except RuntimeError as exc:
        console.print(f"[bold red]❌ {exc}[/bold red]")
        raise typer.Exit(2) from exc

    table = Table(title="Vercel project ownership")
    table.add_column("Vercel project", style="bold white")
    table.add_column("Managed project", style="dim")
    table.add_column("Verdict", style="bold")
    table.add_column("Detail")
    for finding in findings:
        style = {"error": "red", "warn": "yellow", "ok": "green"}[finding.severity]
        verdict = f"[{style}]{finding.rule}[/{style}]"
        table.add_row(finding.vercel_project, finding.managed or "—", verdict, escape(finding.detail))
    console.print(table)
    if any(finding.severity == "error" for finding in findings):
        raise typer.Exit(1)


@app.command(name="vercel-gating")
def vercel_gating_command(
    path: str | None = typer.Argument(None, help="Target repository directory path (defaults to CWD)"),
    all: bool = typer.Option(False, "--all", "-a", help="Check every Vercel project in the team, not just this repo's"),
    days: int | None = typer.Option(None, "--days", help="Window for observed deployments (default: config)"),
) -> None:
    """Audits that Vercel-linked repos do not *create* deployments for branch pushes.

    The Hobby quota is spent when a deployment is created, not when it builds, so
    ``ignoreCommand`` skipping is not gating — see docs/repo-governance/vercel-gating.md.

    Rules:

    - creation-gate (error): the project creates deployments for every branch push
      and the repo's vercel.json has no deny-all `git.deploymentEnabled` map.
    - allowlist-has-no-deny-all (warn): a deploymentEnabled map without
      `'**': false`, so unlisted branches still create. Vercel matches with
      minimatch, so `'*': false` does not cover branches with a slash.
    - budget-leak (error): deployments were observed being created off the
      allowed refs in the window, whatever the flags say.
    - production-stale (warn): the latest Ready production deployment does not
      match origin/main.

    Team id and window come from defaults.vercel in config/repo_policies.json.
    The token is the Vercel CLI's own (run `vercel whoami` if it has expired);
    VERCEL_TOKEN overrides it.
    """
    cfg = load_policy_config()
    if path and not pathlib.Path(path).expanduser().exists():
        named = personal_repos_dir() / pathlib.Path(path).name
        if (named / ".git").exists():
            path = str(named)
    target_dirs = resolve_target_directories(path, all)
    if path and not target_dirs:
        console.print(f"[bold red]❌ No repository found at {path}[/bold red]")
        raise typer.Exit(2)
    only_repo = None if all or not target_dirs else target_dirs[0].name

    rules = cfg.vercel_gating(only_repo or "")
    team_id = rules.team_id or os.environ.get("VERCEL_TEAM_ID", "")
    if not team_id:
        console.print("[bold red]❌ No Vercel team id: set defaults.vercel.team_id or VERCEL_TEAM_ID[/bold red]")
        raise typer.Exit(2)
    window = days if days else rules.window_days

    try:
        results = audit_projects(
            team_id=team_id,
            window_days=window,
            repos_dir=personal_repos_dir(),
            only_repo=only_repo,
        )
    except RuntimeError as exc:
        console.print(f"[bold red]❌ {exc}[/bold red]")
        raise typer.Exit(2) from exc

    if not results:
        console.print(f"[yellow]No linked Vercel projects for {only_repo or 'the team'}[/yellow]")
        raise typer.Exit(0)

    table = Table(title=f"Vercel deployment-creation gating (window: {window}d)")
    table.add_column("Project", style="bold white")
    table.add_column("Repo", style="dim")
    table.add_column("Creates on push")
    table.add_column("vercel.json gate")
    table.add_column("Verdict", style="bold")

    errors = 0
    for audit in results:
        facts, findings = audit.facts, audit.findings
        errors += sum(1 for f in findings if f.severity == "error")
        deny_all = any(p in ("*", "**") for p in (facts.deny or ()))
        if facts.allow is None:
            gate = "—"
        else:
            gate = f"{len(facts.allow)} allowed / {'deny-all' if deny_all else 'NO FALLBACK'}"
        create = {"enabled": "all branches", "disabled": "off"}.get(facts.create_deployments or "", "legacy")
        error_rules = [f.rule for f in findings if f.severity == "error"]
        warn_rules = [f.rule for f in findings if f.severity == "warn"]
        if error_rules:
            verdict = "[red]" + "; ".join(error_rules) + "[/red]"
        elif warn_rules:
            verdict = "[yellow]" + "; ".join(warn_rules) + "[/yellow]"
        else:
            verdict = "[green]✓ gated[/green]"
        table.add_row(facts.name, facts.repo or "—", create, gate, verdict)
    console.print(table)

    for audit in results:
        facts, findings = audit.facts, audit.findings
        for finding in findings:
            style = SEVERITY_STYLES.get(finding.severity, "white")
            console.print(f"  [{style}]{finding.rule}[/{style}] {facts.name}: {finding.detail}")

    if errors:
        raise typer.Exit(1)
