"""AGENTS.md, Vercel gating, .gitignore and GitHub Apps audits.

Registered on the ``repo-governance`` app by :mod:`.align_repo_conventions`.
"""

import contextlib
import json
import os
import pathlib
import sys

import typer
from rich.markup import escape
from rich.panel import Panel
from rich.table import Table

from rig.repo_governance.agents_md_audit import (
    GATING_CHECKS,
    REPORT_ONLY_CHECKS,
    WAIVER_FILENAME,
    AuditResult,
    Waiver,
    audit_repo,
    load_waivers,
)
from rig.repo_governance.cli_common import SEVERITY_STYLES, console
from rig.repo_governance.gitignore_manager import (
    RepoGitignoreAlignment,
    RepoGitignoreInspection,
    align_gitignore,
    inspect_gitignore,
)
from rig.repo_governance.policy_config import (
    load_policy_config,
)
from rig.repo_governance.policy_key import policy_key
from rig.repo_governance.repo_discovery import (
    personal_repos_dir,
    resolve_target_directories,
)
from rig.repo_governance.vercel_gating import audit_projects

app = typer.Typer()


@app.command(name="agents-audit")
def agents_audit_command(
    path: str | None = typer.Argument(None, help="Target repository directory path (defaults to CWD)"),
    all: bool = typer.Option(False, "--all", "-a", help="Audit all personal repositories under <code root>/personal"),
    json_output: bool = typer.Option(False, "--json", help="Emit findings as JSON for an agent to act on"),
    strict: bool = typer.Option(False, "--strict", help="Fail on warnings too, not only errors"),
    write_waivers: bool = typer.Option(
        False, "--write-waivers", help=f"Append a stub for every surviving finding to {WAIVER_FILENAME}"
    ),
) -> None:
    """Audits AGENTS.md and the docs tree against the machine-decidable rules.

    Only the deterministic half of `docs/agents/agent-consistency.md` is gated here. Whether an
    instruction belongs in the root file, whether it is vague, and whether two contradict each
    other need judgement — `--json` exists so an agent can pick those up with that document as
    its rubric.

    A repo dismisses a finding by committing a waiver to `.agents-audit.json`. Waivers need a
    reason, may carry an `expires` date, and are reported once they stop suppressing anything —
    so the dismissal list cannot quietly become the ball of mud the rules exist to prevent.
    `--write-waivers` emits the stubs with an empty reason, which the audit then fails on until
    each one is justified.
    """
    # Repo selection narrates which repos it skipped. In `--json` mode stdout
    # belongs to the payload, so that narration goes to stderr instead.
    if json_output:
        with contextlib.redirect_stdout(sys.stderr):
            targets = resolve_target_directories(path, all)
    else:
        targets = resolve_target_directories(path, all)
    config = load_policy_config()
    results: list[AuditResult] = []

    for target in targets:
        rules = config.agents_md(policy_key(target))
        results.append(audit_repo(target, rules))

    if json_output:
        payload = {
            "results": [r.model_dump(mode="json") for r in results],
            "gating_checks": list(GATING_CHECKS),
            "report_only_checks": list(REPORT_ONLY_CHECKS),
            "rubric": "docs/agents/agent-consistency.md",
        }
        # Plain stdout, not `console.print_json` — Rich adds ANSI colour and
        # wrapping, which is the structured-output anti-pattern
        # `docs/agents/agent-consistency.md` § *Agent DX* warns about. This stream is
        # for a machine.
        typer.echo(json.dumps(payload, indent=2))
    else:
        _render_audit(results)

    if write_waivers:
        for target, result in zip(targets, results, strict=True):
            written = _append_waiver_stubs(target, result)
            if written:
                console.print(
                    f"[yellow]{target.name}: wrote {written} stub(s) to {WAIVER_FILENAME} — "
                    f"fill in each `reason` or the audit stays red.[/yellow]"
                )

    failed = any(r.failed or (strict and r.findings) for r in results)
    if failed:
        raise typer.Exit(code=1)


def _render_audit(results: list[AuditResult]) -> None:
    """Print one table per repo that has findings, plus the report-only metrics."""

    for result in results:
        if not result.findings and not result.suppressed:
            console.print(f"[green]✓ {result.repo}: clean[/green]")
            continue

        table = Table(title=f"{result.repo} — AGENTS.md audit", title_style="bold")
        table.add_column("Check", style="cyan", no_wrap=True)
        table.add_column("Where", no_wrap=True)
        table.add_column("Finding")
        for finding in result.findings:
            where = finding.path if finding.line is None else f"{finding.path}:{finding.line}"
            table.add_row(
                f"[{SEVERITY_STYLES.get(finding.severity, 'white')}]{finding.check}[/]",
                where,
                finding.message,
            )
        console.print(table)

        if result.suppressed:
            console.print(f"  [dim]{len(result.suppressed)} finding(s) waived in {WAIVER_FILENAME}[/dim]")
        metrics = ", ".join(f"{k} {v}" for k, v in result.metrics.items())
        console.print(f"  [dim]metrics (not gated): {metrics}[/dim]\n")


def _append_waiver_stubs(repo_path: pathlib.Path, result: AuditResult) -> int:
    """Add an unreasoned stub per surviving finding. Returns how many were new."""
    waiver_path = repo_path / WAIVER_FILENAME
    existing = load_waivers(repo_path)
    known = {w.key for w in existing.waivers}
    additions = [
        Waiver(check=f.check, path=f.path, reason="")
        for f in result.findings
        if f.key not in known and f.check in GATING_CHECKS
    ]
    if not additions:
        return 0
    existing.waivers.extend(additions)
    waiver_path.write_text(json.dumps(existing.model_dump(mode="json"), indent=2) + "\n", encoding="utf-8")
    return len(additions)


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


@app.command(name="gitignore")
def gitignore_command(
    path: str | None = typer.Argument(None, help="Target repository directory path (defaults to CWD)"),
    all: bool = typer.Option(False, "--all", "-a", help="Scan all personal repositories under <code root>/personal"),
    org: str | None = typer.Option(None, "--org", "-o", help="Target GitHub organization (e.g. rtkelly-labs)"),
    language: str | None = typer.Option(
        None, "--language", "-l", help="Language preset(s) to enforce (e.g. 'python', 'dotnet', 'node')"
    ),
    apply: bool = typer.Option(False, "--apply", help="Apply missing gitignore entries. Without this, audits only."),
    dry_run: bool = typer.Option(False, "--dry-run", help="Preview mutations without applying changes"),
    json_output: bool = typer.Option(False, "--json", help="Output results as JSON"),
) -> None:
    """Audit or synchronize common .gitignore rules across repositories.

    The entries are `gitignore_entries` in config/repo_policies.json: by default
    temp/ for scratch files and worktrees, and both TODO.md and todo.md, since
    case-sensitive filesystems treat them as different files. Each entry's
    `write` is appended when missing; any spelling in `satisfied_by` counts as
    present, so --apply never adds a second spelling and is idempotent.

    It also asks git whether temp/.rig/, rig's per-worktree state folder, is
    really ignored, and reports NOT IGNORED for a rule that re-includes it;
    --apply leaves that for a hand edit (docs/repo-governance/worktree-state.md).
    """
    targets = resolve_target_directories(path, all, org=org)
    if not targets:
        console.print("[bold red]❌ No target repositories found.[/bold red]")
        raise typer.Exit(2)

    languages = [part.strip() for part in language.split(",") if part.strip()] if language else None
    alignments: list[RepoGitignoreAlignment] = []
    inspections: list[RepoGitignoreInspection] = []

    for target in targets:
        if apply:
            alignment = align_gitignore(target, dry_run=dry_run, languages=languages)
            alignments.append(RepoGitignoreAlignment(repo_name=target.name, repo_path=str(target), result=alignment))
        else:
            inspection = inspect_gitignore(target, languages=languages)
            inspections.append(RepoGitignoreInspection(repo_name=target.name, repo_path=str(target), result=inspection))
    has_drift = any(not r.result.is_aligned for r in inspections)

    if json_output:
        rows = [r.to_json() for r in alignments] if apply else [r.to_json() for r in inspections]
        typer.echo(json.dumps(rows, indent=2))
        if not apply and has_drift:
            raise typer.Exit(1)
        return

    mode_title = "ENFORCE (APPLY)" if apply else "AUDIT"
    if apply and dry_run:
        mode_title = "ENFORCE (DRY-RUN)"
    console.print(Panel.fit(f"[bold cyan]Common .gitignore: {mode_title}[/bold cyan]", border_style="cyan"))

    table = Table(title="Repository .gitignore Status")
    table.add_column("Repository", style="bold white")
    table.add_column("Languages", style="cyan")
    table.add_column("Status", style="bold")
    table.add_column("Details")

    for aligned in alignments:
        status = aligned.result.status
        langs = ", ".join(aligned.result.languages) or "[dim]common[/dim]"
        added = aligned.result.added
        details = f"Added: {', '.join(added)}" if added else "Already configured"
        style = "green" if status in ("aligned", "already_configured") else "red"
        table.add_row(aligned.repo_name, langs, f"[{style}]{escape(status)}[/{style}]", escape(details))
    for inspected in inspections:
        status = inspected.result.status
        langs = ", ".join(inspected.result.languages) or "[dim]common[/dim]"
        missing = inspected.result.missing_lines
        details = f"Missing: {', '.join(missing)}" if missing else "Fully compliant"
        style = "green" if inspected.result.is_aligned else "yellow"
        table.add_row(inspected.repo_name, langs, f"[{style}]{escape(status)}[/{style}]", escape(details))

    console.print(table)
    if not apply and has_drift:
        console.print(
            "\n[yellow]Run 'repo-governance gitignore --apply' or 'workspace gitignore sync' to fix.[/yellow]"
        )
        raise typer.Exit(1)


@app.command(name="apps")
def apps_command(
    target_dir: str | None = typer.Argument(None, help="Target repository directory path (defaults to org scan)"),
    org: str = typer.Option("rtkelly-labs", "--org", "-o", help="Target GitHub organization or account"),
    audit: bool = typer.Option(False, "--audit", help="Audit GitHub Apps installations and repo scoping"),
    enforce: bool = typer.Option(False, "--enforce", help="Sync missing repo connections and apply in-repo config"),
    dry_run: bool = typer.Option(False, "--dry-run", help="Preview API mutations without applying"),
    coderabbit: bool = typer.Option(
        False, "--coderabbit", help="Also audit/enforce .coderabbit.yaml across target repos"
    ),
    review_tools: bool = typer.Option(
        False,
        "--review-tools",
        help="Also audit/enforce code review tool configs (.coderabbit.yaml, codecov.yml)",
    ),
) -> None:
    """Audits and governs GitHub Apps and PR reviewer integrations across organizations.

    Apps are declared under `github_apps` in config/repo_policies.json, per
    account. `all_repos` means the policy-registered repos plus every repo GitHub
    lists for the account, minus `disallow_repos`.

    An app missing from an account cannot be installed by API: --audit prints its
    install URL for a person to approve. Once installed, --enforce scopes the
    missing repos into the installation. A failed GitHub lookup fails the command
    rather than reporting the app as not installed.
    """
    if not enforce:
        audit = True

    from rig.repo_governance.github_apps_manager import (
        GitHubLookupError,
        audit_account_apps,
        sync_account_app_repos,
    )
    from rig.repo_governance.review_tool_configs import (
        align_coderabbit_config,
        align_review_configs,
        inspect_coderabbit_config,
        inspect_review_configs,
    )

    mode_title = "ENFORCE (SYNC)" if enforce else "AUDIT"
    if enforce and dry_run:
        mode_title = "ENFORCE (DRY-RUN)"
    panel_title = f"[bold cyan]GitHub Apps Governance ({org}): {mode_title}[/bold cyan]"
    console.print(Panel.fit(panel_title, border_style="cyan"))

    try:
        results = audit_account_apps(org)
    except GitHubLookupError as e:
        console.print(f"[bold red]✗ GitHub lookup failed for '{org}': {e}[/bold red]")
        sys.exit(1)
    if not results:
        console.print(f"[dim]No GitHub Apps declared for account/organization '{org}' in repo_policies.json.[/dim]")
        return

    table = Table(title=f"Declared GitHub Apps for @{org}")
    table.add_column("App Name", style="bold white")
    table.add_column("Slug", style="dim")
    table.add_column("Installed", style="bold")
    table.add_column("Selection", style="cyan")
    table.add_column("Connected Repos")
    table.add_column("Status", style="bold")

    failures = 0
    for r in results:
        installed_str = "[green]✓ Yes[/green]" if r.installed else "[red]✗ No[/red]"
        status_str = (
            "[green]ALIGNED[/green]"
            if r.is_aligned
            else ("[bold red]DRIFTED[/bold red]" if r.required else "[yellow]UNINSTALLED[/yellow]")
        )
        if not r.is_aligned and r.required:
            failures += 1

        connected_summary = f"{len(r.actual_repos)} repos" if r.selection != "all" else "All repos"
        if r.missing_repos:
            connected_summary += f" [yellow](missing: {len(r.missing_repos)})[/yellow]"

        table.add_row(
            r.app_name,
            r.app_slug,
            installed_str,
            r.selection,
            connected_summary,
            status_str,
        )

    console.print(table)

    # Detailed report / instructions
    for r in results:
        if not r.installed:
            console.print(f"\n[bold yellow]⚠ {r.app_name} ({r.app_slug}) is not installed on @{org}:[/bold yellow]")
            if r.instructions:
                console.print(f"   [cyan]Install link:[/cyan] {r.install_url}")
                console.print(
                    "   [dim]GitHub security requires an organization admin to authorize third-party apps.[/dim]"
                )
        elif r.missing_repos:
            console.print(f"\n[yellow]⚠ {r.app_name} is missing connections to:[/yellow] {', '.join(r.missing_repos)}")

    # Enforce logic
    sync_failures = 0
    if enforce:
        console.print("\n[bold cyan]Syncing GitHub App repository connections...[/bold cyan]")
        for r in results:
            if not r.installed:
                console.print(f"[yellow]Skipping {r.app_slug}: install manually before connecting repos.[/yellow]")
                continue
            sync = sync_account_app_repos(org, r.app_slug, dry_run=dry_run)
            if not sync.ok:
                sync_failures += 1
            for m in sync.messages:
                style = "green" if sync.ok else "yellow"
                console.print(f"   [{style}]· {m}[/{style}]")

    # In-repo config (e.g. CodeRabbit, Codecov, SonarCloud)
    target_path = pathlib.Path(target_dir).resolve() if target_dir else None
    targets = [target_path] if target_path else resolve_target_directories(None, all_repos=True, org=org)

    if coderabbit:
        console.print("\n[bold cyan]Auditing/Enforcing .coderabbit.yaml configurations...[/bold cyan]")
        for target in targets:
            if enforce:
                change = align_coderabbit_config(target, dry_run=dry_run)
                style = "green" if change.changed else "dim"
                console.print(f"   [{style}]· {target.name}: {change.message}[/{style}]")
            else:
                res = inspect_coderabbit_config(target)
                style = "green" if res.is_aligned else "yellow"
                console.print(f"   [{style}]· {target.name}: {res.status}[/{style}]")

    if review_tools:
        console.print(
            "\n[bold cyan]Auditing/Enforcing code review tool configurations "
            "(.coderabbit.yaml, codecov.yml)...[/bold cyan]"
        )
        for target in targets:
            console.print(f" [bold white]{target.name}:[/bold white]")
            if enforce:
                align_results = align_review_configs(target, org=org, dry_run=dry_run)
                for tool in align_results:
                    style = "green" if tool.changed else "dim"
                    console.print(f"   [{style}]· {tool.tool}: {tool.message}[/{style}]")
            else:
                review_status = inspect_review_configs(target)
                for row in review_status.by_tool():
                    style = "green" if row.status.is_aligned else "yellow"
                    console.print(f"   [{style}]· {row.tool}: {row.status.status}[/{style}]")

    if enforce and sync_failures > 0:
        console.print(f"\n[bold red]FAILED: {sync_failures} GitHub App sync(s) failed on @{org}.[/bold red]")
        sys.exit(1)
    if audit and failures > 0:
        console.print(f"\n[bold red]FAILED: {failures} required GitHub App(s) drifted on @{org}.[/bold red]")
        sys.exit(1)
    elif audit:
        console.print("\n[bold green]SUCCESS: all declared GitHub Apps conform to policy.[/bold green]")
