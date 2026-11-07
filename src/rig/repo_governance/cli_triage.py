"""Issue and PR triage, PR check verification, and policy application commands.

Registered on the ``repo-governance`` app by :mod:`.align_repo_conventions`.
"""

import json
import pathlib
import sys
from dataclasses import dataclass

import typer
from pydantic import ValidationError
from rich.panel import Panel

from rig import proc
from rig.repo_governance.cli_common import console
from rig.repo_governance.github_rulesets import (
    get_required_checks_for_repo,
)
from rig.repo_governance.issue_triage import (
    IssueItem,
    build_triage_dashboard,
    fetch_repo_issues,
)
from rig.repo_governance.policy_config import PolicyConfig
from rig.repo_governance.pr_triage import (
    PRItem,
    build_pr_triage_dashboard,
    fetch_repo_prs,
    merge_ready_pr,
    update_behind_pr,
)
from rig.repo_governance.repo_discovery import (
    discover_org_repos,
    get_gh_repo_name,
    resolve_target_directories,
)
from rig.repo_governance.review_threads import inspect_review_threads
from rig.typed_json import load_json, try_decode_json

app = typer.Typer()


@app.command(name="issue-triage")
def issue_triage_command(
    path: str | None = typer.Argument(None, help="Target repo directory (defaults to current directory)"),
    repo: str | None = typer.Option(None, "--repo", "-r", help="Target GitHub repo (e.g. rtkelly13/design-system)"),
    org: str | None = typer.Option(None, "--org", "-o", help="Target GitHub organization (e.g. rtkelly-labs)"),
    label: str | None = typer.Option(None, "--label", "-l", help="Filter issues by label"),
    all: bool = typer.Option(
        False, "--all", "-a", help="Run across all personal repositories under <code root>/personal"
    ),
    json_output: bool = typer.Option(False, "--json", help="Output triage findings as JSON"),
) -> None:
    """Terminal dashboard for open issues across the estate categorized into actionable buckets."""
    repos_to_scan: list[str] = []
    if repo:
        repos_to_scan.append(repo)
    elif org and not path and not all:
        repos_to_scan.extend(discover_org_repos(org))
    else:
        targets = resolve_target_directories(path, all, org=org)
        for target in targets:
            name = get_gh_repo_name(target)
            if name and name not in repos_to_scan:
                repos_to_scan.append(name)
        if org and not repos_to_scan:
            repos_to_scan.extend(discover_org_repos(org))

    all_issues: list[IssueItem] = []
    for r in repos_to_scan:
        all_issues.extend(fetch_repo_issues(r, label=label))

    dashboard = build_triage_dashboard(all_issues)

    if json_output:
        typer.echo(json.dumps(dashboard.model_dump(mode="json"), indent=2))
        return

    title = f"Open Source Issue Triage: {dashboard.total_issues} Open Issues Across {len(repos_to_scan)} Repos"
    console.print(Panel.fit(f"[bold cyan]{title}[/bold cyan]", border_style="cyan"))

    # 1. Needs Triage
    console.print(
        f"\n[bold red]🚨 NEEDS TRIAGE (No type label and 0 responses) ({len(dashboard.needs_triage)})[/bold red]"
    )
    if not dashboard.needs_triage:
        console.print("  [dim]None[/dim]")
    for item in dashboard.needs_triage:
        author_str = f" by @{item.author}" if item.author else ""
        console.print(f"  • [bold]{item.short_ref}[/bold]: {item.title}{author_str}")
        console.print(f"    [dim]{item.url}[/dim]")

    # 2. Active Bugs
    console.print(f"\n[bold red]🐛 ACTIVE BUGS ({len(dashboard.active_bugs)})[/bold red]")
    if not dashboard.active_bugs:
        console.print("  [dim]None[/dim]")
    for item in dashboard.active_bugs:
        console.print(f"  • [bold]{item.short_ref}[/bold]: {item.title} [red][type: bug][/red]")
        console.print(f"    [dim]{item.url}[/dim]")

    # 3. Community
    console.print(f"\n[bold magenta]🙋 COMMUNITY / GOOD FIRST ISSUE ({len(dashboard.community)})[/bold magenta]")
    if not dashboard.community:
        console.print("  [dim]None[/dim]")
    for item in dashboard.community:
        lbls = [label_name for label_name in item.labels if label_name.lower() in ("good first issue", "help wanted")]
        tag_str = f" [{', '.join(lbls)}]" if lbls else ""
        console.print(f"  • [bold]{item.short_ref}[/bold]: {item.title}[magenta]{tag_str}[/magenta]")
        console.print(f"    [dim]{item.url}[/dim]")

    # 4. Stale Repro
    console.print(f"\n[bold yellow]💤 STALE WAITING ON REPRO (> 14 days) ({len(dashboard.stale_repro)})[/bold yellow]")
    if not dashboard.stale_repro:
        console.print("  [dim]None[/dim]")
    for item in dashboard.stale_repro:
        console.print(f"  • [bold]{item.short_ref}[/bold]: {item.title} [yellow][status: needs-repro][/yellow]")
        console.print(f"    [dim]{item.url}[/dim]")

    if dashboard.other:
        console.print(f"\n[dim]Other open issues (already triaged): {len(dashboard.other)}[/dim]")


@app.command(name="pr-triage")
def pr_triage_command(
    path: str | None = typer.Argument(None, help="Target repo directory (defaults to current directory)"),
    repo: str | None = typer.Option(None, "--repo", "-r", help="Target GitHub repo (e.g. rtkelly13/design-system)"),
    org: str | None = typer.Option(None, "--org", "-o", help="Target GitHub organization (e.g. rtkelly-labs)"),
    all: bool = typer.Option(
        False, "--all", "-a", help="Run across all personal repositories under <code root>/personal"
    ),
    update_behind: bool = typer.Option(False, "--update-behind", help="Update branch for PRs behind main"),
    merge_ready: bool = typer.Option(False, "--merge-ready", help="Squash and merge PRs that are ready"),
    json_output: bool = typer.Option(False, "--json", help="Output PR triage findings as JSON"),
) -> None:
    """Terminal dashboard for open PRs with automated batch actions."""
    repos_to_scan: list[str] = []
    if repo:
        repos_to_scan.append(repo)
    elif org and not path and not all:
        repos_to_scan.extend(discover_org_repos(org))
    else:
        targets = resolve_target_directories(path, all, org=org)
        for target in targets:
            name = get_gh_repo_name(target)
            if name and name not in repos_to_scan:
                repos_to_scan.append(name)
        if org and not repos_to_scan:
            repos_to_scan.extend(discover_org_repos(org))

    all_prs: list[PRItem] = []
    for r in repos_to_scan:
        all_prs.extend(fetch_repo_prs(r))

    dashboard = build_pr_triage_dashboard(all_prs)

    if json_output:
        typer.echo(json.dumps(dashboard.model_dump(mode="json"), indent=2))
        return

    title = f"Open Source Pull Request Triage: {dashboard.total_prs} Open PRs Across {len(repos_to_scan)} Repos"
    console.print(Panel.fit(f"[bold cyan]{title}[/bold cyan]", border_style="cyan"))

    # 1. Ready
    console.print(f"\n[bold green]🟢 READY TO MERGE ({len(dashboard.ready)})[/bold green]")
    if not dashboard.ready:
        console.print("  [dim]None[/dim]")
    for item in dashboard.ready:
        console.print(f"  • [bold]{item.short_ref}[/bold]: {item.title} (branch: {item.head_branch})")
        console.print(f"    [dim]{item.url}[/dim]")

    # 2. Behind
    console.print(f"\n[bold yellow]🟡 BEHIND MAIN ({len(dashboard.behind)})[/bold yellow]")
    if not dashboard.behind:
        console.print("  [dim]None[/dim]")
    for item in dashboard.behind:
        console.print(f"  • [bold]{item.short_ref}[/bold]: {item.title} (branch: {item.head_branch})")
        console.print(f"    [dim]{item.url}[/dim]")

    # 3. Blocked
    console.print(f"\n[bold red]🔴 BLOCKED / FAILING ({len(dashboard.blocked)})[/bold red]")
    if not dashboard.blocked:
        console.print("  [dim]None[/dim]")
    for item in dashboard.blocked:
        reasons: list[str] = []
        if item.is_draft:
            reasons.append("draft")
        if item.mergeable == "CONFLICTING":
            reasons.append("conflict")
        if item.failed_checks:
            reasons.append(f"failing: {', '.join(item.failed_checks)}")
        if item.missing_required_checks:
            reasons.append(f"missing: {', '.join(item.missing_required_checks)}")
        reason_str = f" [{', '.join(reasons)}]" if reasons else ""
        console.print(f"  • [bold]{item.short_ref}[/bold]: {item.title}[red]{reason_str}[/red]")
        console.print(f"    [dim]{item.url}[/dim]")

    # 4. Pending
    console.print(f"\n[bold cyan]⏳ IN FLIGHT / PENDING ({len(dashboard.pending)})[/bold cyan]")
    if not dashboard.pending:
        console.print("  [dim]None[/dim]")
    for item in dashboard.pending:
        pending_str = f" [pending: {', '.join(item.pending_checks)}]" if item.pending_checks else ""
        console.print(f"  • [bold]{item.short_ref}[/bold]: {item.title}[cyan]{pending_str}[/cyan]")
        console.print(f"    [dim]{item.url}[/dim]")

    # 5. Stale
    console.print(f"\n[bold magenta]💤 STALE / IDLE (> 14 days) ({len(dashboard.stale)})[/bold magenta]")
    if not dashboard.stale:
        console.print("  [dim]None[/dim]")
    for item in dashboard.stale:
        console.print(f"  • [bold]{item.short_ref}[/bold]: {item.title}")
        console.print(f"    [dim]{item.url}[/dim]")

    # Batch Actions
    if update_behind and dashboard.behind:
        console.print("\n[bold yellow]── Updating behind branches ──[/bold yellow]")
        for item in dashboard.behind:
            result = update_behind_pr(item)
            if result.ok:
                console.print(f"  [green]✓ {result.message}[/green]")
            else:
                console.print(f"  [red]✗ {result.message}[/red]")

    if merge_ready and dashboard.ready:
        console.print("\n[bold green]── Merging ready PRs ──[/bold green]")
        for item in dashboard.ready:
            result = merge_ready_pr(item)
            if result.ok:
                console.print(f"  [green]✓ {result.message}[/green]")
            else:
                console.print(f"  [red]✗ {result.message}[/red]")


@dataclass(frozen=True, slots=True)
class _GhPrCheck:
    """One row of ``gh pr checks --json name,state,bucket``."""

    name: str
    state: str = ""
    bucket: str | None = None


@dataclass(frozen=True, slots=True)
class _GhPrViewNumber:
    number: int = 0


@app.command()
def verify_pr_checks(
    pr: str = typer.Argument(..., help="PR number or branch name"),
    repo: str | None = typer.Option(None, "--repo", "-r", help="Target GitHub repo (e.g. rtkelly13/design-system)"),
) -> None:
    """Strictly verifies that all repo-specific required CI status checks pass for a given PR before merging."""
    target_repo = repo or get_gh_repo_name(pathlib.Path.cwd()) or "rtkelly13/shared-utilities"
    required_checks = get_required_checks_for_repo(target_repo)

    res = proc.gh("pr", "checks", pr, "--repo", target_repo, "--json", "name,state,bucket")
    if not res.ok:
        console.print(f"[bold red]❌ Failed to fetch checks for PR {pr}: {res.err.strip()}[/bold red]")
        sys.exit(1)

    checks = try_decode_json(list[_GhPrCheck], res.out)
    if checks is None:
        console.print(f"[bold red]❌ Unreadable check list from `gh pr checks` for PR {pr}[/bold red]")
        sys.exit(1)
    check_map = {c.name: c for c in checks}

    missing = [name for name in required_checks if name not in check_map]
    if missing:
        console.print(
            f"[bold red]❌ BLOCKED: PR {pr} in {target_repo} is missing required CI checks: {missing}[/bold red]"
        )
        console.print("[dim]Every PR must trigger and pass all required checks before merging.[/dim]")
        sys.exit(1)

    failed: dict[str, str] = {}  # check name -> state
    pending: dict[str, str] = {}

    for name in required_checks:
        chk = check_map[name]
        if chk.bucket in ("fail", "cancel"):
            failed[name] = chk.state
        elif chk.bucket == "pending":
            pending[name] = chk.state

    if failed:
        console.print(f"[bold red]❌ BLOCKED: Required CI check(s) failed for PR {pr}:[/bold red]")
        for name, state in failed.items():
            console.print(f"  • [red]{name}: {state}[/red]")
        sys.exit(1)

    if pending:
        console.print(f"[bold yellow]⏳ BLOCKED: Required CI check(s) still pending for PR {pr}:[/bold yellow]")
        for name, state in pending.items():
            console.print(f"  • [yellow]{name}: {state}[/yellow]")
        sys.exit(1)

    # Resolve a branch name or URL before querying the review-thread connection.
    pr_number: int | None = int(pr) if pr.isdigit() else None
    if pr_number is None:
        parsed_view = proc.gh_json(_GhPrViewNumber, "pr", "view", pr, "--repo", target_repo, "--json", "number")
        if parsed_view is None or parsed_view.number <= 0:
            console.print(f"[bold red]Failed to resolve PR '{pr}' in {target_repo}[/bold red]")
            sys.exit(1)
        pr_number = parsed_view.number
    threads = inspect_review_threads(target_repo, pr_number)
    if not threads.is_aligned:
        console.print(f"BLOCKED: PR {pr} in {target_repo}: {threads.message}", markup=False)
        sys.exit(1)

    console.print(
        f"[bold green]✓ All required status checks passed and every conversation thread "
        f"is resolved for PR {pr}![/bold green]"
    )


@app.command()
def apply_policy(
    policy: str = typer.Option("all", "--policy", "-p", help="Policy to apply: ai, security, or all"),
    repo: str | None = typer.Option(None, "--repo", "-r", help="Target repo directory or name"),
    dry_run: bool = typer.Option(False, "--dry-run", help="Preview changes without writing files"),
) -> None:
    """Applies parameterized governance policies (AI policy, security policy, README disclosure) to a target repo."""
    from rig.repo_governance.policy_template_engine import (
        apply_ai_policy,
        apply_security_policy,
        detect_repo_metadata,
        inject_readme_ai_disclosure,
    )

    repo_dir = pathlib.Path(repo) if repo else pathlib.Path.cwd()
    if not repo_dir.exists() and (pathlib.Path.cwd().parent / repo_dir.name).exists():
        repo_dir = pathlib.Path.cwd().parent / repo_dir.name

    if not repo_dir.exists() or not (repo_dir / ".git").exists():
        console.print(f"[bold red]❌ Directory {repo_dir} is not a valid git repository.[/bold red]")
        sys.exit(1)

    meta = detect_repo_metadata(repo_dir)
    vis = "Public" if meta.is_public else "Private"
    console.print(
        Panel(
            f"[bold white]Applying Governance Policies to [cyan]{meta.repo_name}[/cyan][/bold white]\n"
            f"[dim]Visibility: {vis} | Runtimes: {meta.supported_runtimes}[/dim]"
        )
    )

    if policy in ("ai", "all"):
        apply_ai_policy(repo_dir, meta, dry_run=dry_run)
        inject_readme_ai_disclosure(repo_dir, meta, dry_run=dry_run)
        console.print("  [green]✓ AI_POLICY.md and README.md disclosure applied[/green]")

    if policy in ("security", "all") and meta.is_public:
        apply_security_policy(repo_dir, meta, dry_run=dry_run)
        console.print("  [green]✓ SECURITY.md applied[/green]")

    console.print("\n[bold green]✓ Policy application complete![/bold green]")


@app.command()
def sync_policies(
    config_file: str = typer.Option("config/repo_policies.json", "--config", "-c", help="Path to repo_policies.json"),
    public_only: bool = typer.Option(True, "--public-only/--all", help="Only sync public open source profile repos"),
    dry_run: bool = typer.Option(False, "--dry-run", help="Preview changes without writing files"),
) -> None:
    """Synchronizes governance policies across declared repository profiles."""
    from rig.repo_governance.policy_template_engine import (
        apply_ai_policy,
        apply_security_policy,
        detect_repo_metadata,
        inject_readme_ai_disclosure,
    )

    cfg_path = pathlib.Path(config_file)
    if not cfg_path.exists() and (pathlib.Path(__file__).parents[3] / config_file).exists():
        cfg_path = pathlib.Path(__file__).parents[3] / config_file

    if not cfg_path.exists():
        console.print(f"[bold red]❌ Config file not found at {config_file}[/bold red]")
        sys.exit(1)

    try:
        config = PolicyConfig.parse(load_json(cfg_path.read_text(encoding="utf-8")))
    except (json.JSONDecodeError, ValidationError) as exc:
        console.print(f"[bold red]❌ Invalid policy config {cfg_path}: {exc}[/bold red]")
        sys.exit(1)

    base_dir = pathlib.Path.cwd() if (pathlib.Path.cwd() / "shared-utilities").exists() else pathlib.Path.cwd().parent

    for prof_key, prof in config.file.profiles.items():
        if public_only and prof_key != "public_open_source":
            continue

        console.print(f"\n[bold yellow]── Syncing Profile: {prof_key} ──[/bold yellow]")
        for r_name in prof.repos:
            target_path = base_dir / r_name
            if not target_path.exists():
                console.print(f"  [dim]Skipping {r_name} (not found locally at {target_path})[/dim]")
                continue

            meta = detect_repo_metadata(target_path)
            console.print(f"  • [bold cyan]{r_name}[/bold cyan] ({'Public' if meta.is_public else 'Private'})")

            if "ai_policy" in prof.policies:
                apply_ai_policy(target_path, meta, dry_run=dry_run)
                console.print("    [green]✓ AI_POLICY.md[/green]")

            if "readme_ai_disclosure" in prof.policies:
                inject_readme_ai_disclosure(target_path, meta, dry_run=dry_run)
                console.print("    [green]✓ README.md AI Section[/green]")

            if "security_policy" in prof.policies:
                apply_security_policy(target_path, meta, dry_run=dry_run)
                console.print("    [green]✓ SECURITY.md[/green]")

    console.print("\n[bold green]✓ Multi-repo policy synchronization complete![/bold green]")
