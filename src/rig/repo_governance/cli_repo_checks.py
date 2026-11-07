"""Status, audit and enforcement of the per-repo conventions; branch pruning; check-name lint.

Registered on the ``repo-governance`` app by :mod:`.align_repo_conventions`.
"""

import sys

import typer
from rich.panel import Panel
from rich.table import Table

from rig.repo_governance.check_names import (
    PREFERRED_CHECK_NAMES,
)
from rig.repo_governance.cli_common import console
from rig.repo_governance.github_rulesets import (
    AlignResult,
    align_github_repo_settings,
    align_github_ruleset,
    inspect_check_names,
    inspect_github_ruleset,
    inspect_repo_settings,
    registered_required_checks,
)
from rig.repo_governance.gitignore_manager import (
    align_gitignore,
    inspect_gitignore,
)
from rig.repo_governance.policy_config import (
    load_policy_config,
)
from rig.repo_governance.repo_discovery import (
    RepoNotFound,
    get_gh_repo_name,
    inspect_repo_exists,
    inspect_repo_owner,
    resolve_target_directories,
)
from rig.repo_governance.repo_hygiene import (
    align_agents_md,
    align_git_hooks,
    align_ls_lint,
    align_npmrc,
    align_todo_md,
    inspect_agents_md,
    inspect_dependabot_renovate,
    inspect_ls_lint,
    inspect_npmrc,
    inspect_pinned_shas,
    inspect_todo_md,
)

app = typer.Typer()


def _not_adopted(profile: str) -> str:
    """The advisory for a repo whose owner maps to *profile* but which no profile names."""
    return f"not adopted: add it to profile {profile} in config/repo_policies.json"


@app.command()
def status(
    path: str | None = typer.Argument(None, help="Target repository directory path (defaults to CWD)"),
    all: bool = typer.Option(False, "--all", "-a", help="Scan all personal repositories under <code root>/personal"),
    org: str | None = typer.Option(None, "--org", "-o", help="Target GitHub organization (e.g. rtkelly-labs)"),
):
    """Inspects convention status across target repositories without mutating settings."""
    console.print(Panel.fit("[bold cyan]Repository Conventions: STATUS / PLAN[/bold cyan]", border_style="cyan"))
    targets = resolve_target_directories(path, all, org=org)
    cfg = load_policy_config()

    table = Table(title="Convention Status Overview")
    table.add_column("Repository", style="bold white")
    table.add_column("GitHub Name", style="dim")
    table.add_column("Owner", style="bold")
    table.add_column("Base Branch", style="bold")
    table.add_column("Repo Settings", style="bold")
    table.add_column("GitHub Ruleset", style="bold")
    table.add_column("NPMRC Status", style="bold")
    table.add_column("AGENTS.md", style="bold")
    table.add_column("Pinned SHAs", style="bold")
    table.add_column("ls-lint", style="bold")
    table.add_column("Overall Alignment", style="bold", no_wrap=True)
    advisories: list[str] = []

    for target in targets:
        exist_res = inspect_repo_exists(target)
        if isinstance(exist_res, RepoNotFound):
            table.add_row(
                target.name,
                exist_res.repo_name or "N/A",
                "-",
                "-",
                "-",
                "-",
                "-",
                "-",
                "-",
                "-",
                f"[dim]{exist_res.status}[/dim]",
            )
            continue

        repo_name = exist_res.repo_name
        owner_res = inspect_repo_owner(target, repo_name=repo_name, config=cfg)
        expected_branch = cfg.expected_base_branch(repo_name)
        repo_res = inspect_repo_settings(repo_name, expected_branch=expected_branch)
        ruleset_res = inspect_github_ruleset(repo_name)
        npmrc_res = inspect_npmrc(target)
        agents_res = inspect_agents_md(target)
        gitignore_res = inspect_gitignore(target)
        todo_res = inspect_todo_md(target)
        pinned_res = inspect_pinned_shas(target)
        ls_lint_res = inspect_ls_lint(target, config=cfg)
        dependabot_res = inspect_dependabot_renovate(target)

        if owner_res.inferred_profile is not None:
            advisories.append(f"{target.name} ({repo_name}): {_not_adopted(owner_res.inferred_profile)}")
        owner_str = (
            f"[green]✓ {owner_res.actual_owner}[/green]"
            if owner_res.is_aligned
            else f"[red]✗ {owner_res.actual_owner or 'N/A'}[/red]"
        )

        base_branch_str = (
            f"[green]✓ {repo_res.default_branch}[/green]"
            if repo_res.base_branch_aligned
            else f"[red]✗ {repo_res.default_branch} (need {repo_res.expected_branch})[/red]"
        )

        settings_str = "[green]SQUASH+DELETE+AUTOMERGE[/green]" if repo_res.is_aligned else "[red]DRIFTED[/red]"

        if "N/A" in ruleset_res.status:
            ruleset_str = f"[yellow]{ruleset_res.status}[/yellow]"
        elif ruleset_res.is_aligned:
            ruleset_str = "[green]ACTIVE (+THREADS RESOLVED)[/green]"
        else:
            ruleset_str = f"[red]{ruleset_res.status}[/red]"
        npmrc_str = f"[green]{npmrc_res.status}[/green]" if npmrc_res.is_aligned else f"[red]{npmrc_res.status}[/red]"
        agents_str = (
            f"[green]{agents_res.status}[/green]" if agents_res.is_aligned else f"[red]{agents_res.status}[/red]"
        )
        pinned_str = (
            f"[green]{pinned_res.status}[/green]" if pinned_res.is_aligned else f"[red]{pinned_res.status}[/red]"
        )
        ls_lint_str = (
            f"[green]{ls_lint_res.status}[/green]" if ls_lint_res.is_aligned else f"[red]{ls_lint_res.status}[/red]"
        )

        is_fully_aligned = (
            owner_res.is_aligned
            and repo_res.is_aligned
            and ruleset_res.is_aligned
            and npmrc_res.is_aligned
            and agents_res.is_aligned
            and gitignore_res.is_aligned
            and todo_res.is_aligned
            and pinned_res.is_aligned
            and ls_lint_res.is_aligned
            and dependabot_res.is_aligned
        )
        overall_str = (
            "[bold green]✓ ALIGNED[/bold green]" if is_fully_aligned else "[bold yellow]⚠ DRIFTED[/bold yellow]"
        )

        table.add_row(
            target.name,
            repo_name,
            owner_str,
            base_branch_str,
            settings_str,
            ruleset_str,
            npmrc_str,
            agents_str,
            pinned_str,
            ls_lint_str,
            overall_str,
        )

    console.print(table)
    for advisory in advisories:
        console.print(f"[dim]· {advisory}[/dim]")


@app.command()
def audit(
    path: str | None = typer.Argument(None, help="Target repository directory path (defaults to CWD)"),
    all: bool = typer.Option(False, "--all", "-a", help="Audit all personal repositories under <code root>/personal"),
    org: str | None = typer.Option(None, "--org", "-o", help="Target GitHub organization (e.g. rtkelly-labs)"),
):
    """Audits repository conventions for CI compliance. Exits with 0 if compliant, 1 if drift detected."""
    console.print(Panel.fit("[bold yellow]Repository Conventions: AUDIT CHECK[/bold yellow]", border_style="yellow"))
    targets = resolve_target_directories(path, all, org=org)
    cfg = load_policy_config()

    drift_count = 0
    total_scanned = 0

    for target in targets:
        total_scanned += 1
        exist_res = inspect_repo_exists(target)
        if isinstance(exist_res, RepoNotFound):
            console.print(f"[bold red]✗ {target.name}: Repo check failed ({exist_res.status})[/bold red]")
            drift_count += 1
            continue

        repo_name = exist_res.repo_name
        owner_res = inspect_repo_owner(target, repo_name=repo_name, config=cfg)
        expected_branch = cfg.expected_base_branch(repo_name)
        repo_res = inspect_repo_settings(repo_name, expected_branch=expected_branch)
        ruleset_res = inspect_github_ruleset(repo_name)
        npmrc_res = inspect_npmrc(target)
        agents_res = inspect_agents_md(target)
        gitignore_res = inspect_gitignore(target)
        todo_res = inspect_todo_md(target)

        pinned_res = inspect_pinned_shas(target)
        ls_lint_res = inspect_ls_lint(target, config=cfg)
        names_res = inspect_check_names(target, repo_name)
        dependabot_res = inspect_dependabot_renovate(target)

        is_fully_aligned = (
            owner_res.is_aligned
            and repo_res.is_aligned
            and ruleset_res.is_aligned
            and npmrc_res.is_aligned
            and agents_res.is_aligned
            and gitignore_res.is_aligned
            and todo_res.is_aligned
            and pinned_res.is_aligned
            and ls_lint_res.is_aligned
            and names_res.is_aligned
            and dependabot_res.is_aligned
        )

        if is_fully_aligned:
            console.print(f"[bold green]✓ {target.name} ({repo_name}): FULLY COMPLIANT[/bold green]")
        else:
            drift_count += 1
            console.print(f"[bold yellow]⚠ {target.name} ({repo_name}): DRIFT DETECTED[/bold yellow]")
            if not owner_res.is_aligned:
                console.print(f"   - Repository owner/org governance failure: {owner_res.message}")
            if not repo_res.base_branch_aligned:
                console.print(
                    f"   - Base branch deviation: default branch is '{repo_res.default_branch}', "
                    f"expected '{repo_res.expected_branch}'"
                )
            if not (repo_res.delete_on_merge and repo_res.squash_only):
                console.print("   - Repo settings (squash merge / delete branch) not enforced")
            elif not repo_res.is_aligned and repo_res.base_branch_aligned:
                console.print("   - Repo settings not enforced")
            if not ruleset_res.is_aligned:
                console.print(f"   - GitHub Ruleset: {ruleset_res.status}")
            if not npmrc_res.is_aligned:
                console.print(f"   - .npmrc status: {npmrc_res.status}")
            if not agents_res.is_aligned:
                console.print(f"   - AGENTS.md status: {agents_res.status}")
            if not gitignore_res.is_aligned:
                console.print(f"   - .gitignore status: {gitignore_res.status}")
            if not todo_res.is_aligned:
                console.print(f"   - TODO.md status: {todo_res.status}")
            if not pinned_res.is_aligned:
                console.print(f"   - Workflow Pinned SHAs status: {pinned_res.status}")
            if not ls_lint_res.is_aligned:
                console.print(f"   - .ls-lint.yml status: {ls_lint_res.status}")
            if not names_res.is_aligned:
                console.print(f"   - Required check(s) no pull_request job declares: {list(names_res.missing)}")
                console.print(
                    f"     [dim]declared: {sorted(names_res.declared)} — "
                    "run 'repo-governance check-names' for detail[/dim]"
                )
            if not dependabot_res.is_aligned:
                console.print(f"   - Dependabot / Renovate conflict: {dependabot_res.reason}")

        # Not being adopted is advisory, not drift: the owner checks already passed.
        if owner_res.inferred_profile is not None:
            console.print(f"   [dim]· {_not_adopted(owner_res.inferred_profile)}[/dim]")

        # Shape is advisory, so it is reported outside the drift block: a repo can be fully
        # compliant and still carry a check name that will break the next time CI is touched.
        if names_res.warnings:
            for name, warns in names_res.warnings.items():
                console.print(f"   [dim]· check name {name!r}: {'; '.join(warns)}[/dim]")

    console.print("\n" + "=" * 50)
    if total_scanned == 0:
        # `--org rtkelly-labs` once reported "All 0 repositories are fully compliant" with no clones present.
        console.print("[bold red]AUDIT FAILED: no repositories found to audit.[/bold red]")
        sys.exit(1)
    if drift_count == 0:
        console.print(
            f"[bold green]SUCCESS: All {total_scanned} repositories are fully compliant with conventions![/bold green]"
        )
        sys.exit(0)
    else:
        console.print(
            f"[bold red]AUDIT FAILED: {drift_count}/{total_scanned} repositories have configuration drift.[/bold red]"
        )
        console.print("[dim]Run align-repo-conventions enforce to apply fixes.[/dim]")
        sys.exit(1)


@app.command()
def enforce(
    path: str | None = typer.Argument(None, help="Target repository directory path (defaults to CWD)"),
    all: bool = typer.Option(
        False,
        "--all",
        "-a",
        help="Enforce conventions across all personal repositories under <code root>/personal",
    ),
    org: str | None = typer.Option(None, "--org", "-o", help="Target GitHub organization (e.g. rtkelly-labs)"),
    dry_run: bool = typer.Option(False, "--dry-run", help="Preview mutations without applying changes"),
):
    """Enforces conventions on target repositories (mutates repo settings, .npmrc, AGENTS.md, TODO.md).

    The GitHub ruleset is compared rule by rule with the declared payload and
    replaced on any drift. Run --dry-run first: it lists each drifted rule
    before anything is overwritten. A required check that no workflow reports is
    refused, because it would block every PR (see `check-names`).
    """
    console.print(Panel.fit("[bold green]Repository Conventions: ENFORCE[/bold green]", border_style="green"))
    targets = resolve_target_directories(path, all, org=org)
    cfg = load_policy_config()

    table = Table(title="Enforcement Execution")
    table.add_column("Repository Path", style="bold white")
    table.add_column("GitHub Repo", style="dim")
    table.add_column("Repo Settings", style="bold")
    table.add_column("GitHub Ruleset", style="bold")
    table.add_column("NPMRC Config", style="bold")
    table.add_column("AGENTS.md Config", style="bold")
    table.add_column(".gitignore /temp/", style="bold")
    table.add_column("TODO.md File", style="bold")
    table.add_column("Pre-Push Hook", style="bold")
    table.add_column("Pinned SHAs", style="bold")
    table.add_column("ls-lint Config", style="bold")

    ruleset_drift: dict[str, tuple[str, ...]] = {}

    for target in targets:
        exist_res = inspect_repo_exists(target)
        if isinstance(exist_res, RepoNotFound):
            table.add_row(
                target.name,
                exist_res.repo_name or "N/A",
                "skipped",
                "skipped",
                "skipped",
                "skipped",
                "skipped",
                "skipped",
                "skipped",
                "skipped",
                "skipped",
            )
            continue

        repo_name = exist_res.repo_name
        owner_res = inspect_repo_owner(target, repo_name=repo_name, config=cfg)
        if not owner_res.is_aligned:
            console.print(
                f"[bold red]✗ {repo_name}: refusing to enforce conventions — "
                f"owner governance failure: {owner_res.message}[/bold red]"
            )
            table.add_row(
                target.name,
                repo_name,
                "blocked_owner_governance",
                "skipped",
                "skipped",
                "skipped",
                "skipped",
                "skipped",
                "skipped",
                "skipped",
                "skipped",
            )
            continue

        expected_branch = cfg.expected_base_branch(repo_name)
        repo_res = align_github_repo_settings(repo_name, expected_branch=expected_branch, dry_run=dry_run)
        # Refuse to push a required check no `pull_request` job declares — the one mutation here
        # with no local undo, since recovery means editing the ruleset in the GitHub UI. Everything
        # else on this row still runs. See docs/repo-governance/required-checks.md.
        names_res = inspect_check_names(target, repo_name)
        # A repo with no registered checks gets a ruleset without a status-checks rule, so there is
        # nothing to verify against its workflows; it still gets the resolved-threads requirement.
        if not registered_required_checks(repo_name):
            ruleset_res = align_github_ruleset(repo_name, dry_run=dry_run)
        elif names_res.status == "no_pr_workflows" and names_res.required:
            console.print(
                f"[bold red]✗ {repo_name}: refusing to set required checks {list(names_res.required)} — "
                "no pull_request workflow exists to report them[/bold red]"
            )
            ruleset_res = AlignResult(status="blocked_no_pr_workflows")
        elif names_res.missing:
            console.print(
                f"[bold red]✗ {repo_name}: refusing to set required checks {list(names_res.missing)} — "
                "no pull_request job declares them[/bold red]"
            )
            console.print(
                f"   [dim]declared: {sorted(names_res.declared)} — "
                "run 'repo-governance check-names' and fix the map or the job name[/dim]"
            )
            ruleset_res = AlignResult(status="blocked_stale_check_names")
        else:
            ruleset_res = align_github_ruleset(repo_name, dry_run=dry_run)
        npmrc_res = align_npmrc(target, dry_run=dry_run)
        agents_res = align_agents_md(target, dry_run=dry_run)
        gitignore_res = align_gitignore(target, dry_run=dry_run)
        todo_res = align_todo_md(target, dry_run=dry_run)
        hook_res = align_git_hooks(target, dry_run=dry_run)
        # Reported, not rewritten: pinning is pinact + Dependabot + zizmor in each repo.
        pinned_res = inspect_pinned_shas(target)
        ls_lint_res = align_ls_lint(target, dry_run=dry_run, config=cfg)

        if ruleset_res.drift:
            ruleset_drift[repo_name] = ruleset_res.drift

        table.add_row(
            target.name,
            repo_name,
            repo_res.status,
            ruleset_res.status,
            npmrc_res.status,
            agents_res.status,
            gitignore_res.status,
            todo_res.status,
            hook_res.status,
            pinned_res.status,
            ls_lint_res.status,
        )

    console.print(table)
    for repo_name, drift in ruleset_drift.items():
        console.print(f"\n[bold yellow]Ruleset drift on {repo_name} (dry run — would update):[/bold yellow]")
        for line in drift:
            console.print(f"   - {line}", style="yellow", markup=False, highlight=False)
    console.print("\n[bold green]✓ Conventions Enforcement Complete![/bold green]")


@app.command()
def prune_branches(
    path: str = typer.Argument(None, help="Target repository directory path (defaults to CWD)"),
    all_repos: bool = typer.Option(False, "--all", "-a", help="Prune across all personal repositories"),
    org: str | None = typer.Option(None, "--org", "-o", help="Target GitHub organization (e.g. rtkelly-labs)"),
    apply: bool = typer.Option(False, "--apply", help="Actually delete. Without this, reports only."),
    show_no_pr: bool = typer.Option(True, "--show-no-pr/--hide-no-pr", help="List branches that never had a PR"),
) -> None:
    """Deletes remote branches whose PR was merged. Dry run unless --apply.

    `delete_branch_on_merge` only covers branches merged while it is switched
    on; this clears the tail left behind.

    Merged-ness comes from GitHub, not git: under squash merge a branch's commits
    never become ancestors of main, so `git branch --merged` finds nothing. The
    PR that merged a branch is the only reliable record that it landed.

    Each branch gets one verdict:

    - merged: a PR with this head ref merged. Deleted with --apply.
    - open-pr: a PR with this head ref is still open. Never deleted; it also wins
      over a merged PR when a branch was reused.
    - no-pr: no PR ever referenced it. Reported, never deleted, because that is
      where unmerged work lives.
    - protected: named in `protected` or under a `protect_prefixes` prefix
      (default dependabot/ and renovate/, since bots retire their own branches).

    Two checkouts of one remote produce identical plans, so --all dedupes by
    owner/name and reports the skip.
    """
    from rig.repo_governance.branch_cleanup import delete_remote_branch, plan_cleanup

    targets = resolve_target_directories(path, all_repos, org=org)
    if not targets:
        console.print("[yellow]No target repositories found.[/yellow]")
        raise typer.Exit(code=0)

    console.print(
        Panel(
            f"Branch Cleanup: {'APPLY' if apply else 'DRY RUN'}",
            style="red" if apply else "cyan",
        )
    )

    total_deletable = 0
    total_deleted = 0
    seen_slugs: dict[str, str] = {}

    for repo_path in targets:
        if not (repo_path / ".git").exists():
            continue
        plan = plan_cleanup(repo_path)

        if plan.skipped:
            console.print(f"[dim]- {plan.repo}: skipped ({plan.skipped})[/dim]")
            continue

        # Two checkouts of one remote produce identical plans; acting on both
        # double-counts and makes the second pass operate on stale refs.
        if plan.slug and plan.slug in seen_slugs:
            console.print(f"[dim]- {plan.repo}: same remote as {seen_slugs[plan.slug]} ({plan.slug})[/dim]")
            continue
        if plan.slug:
            seen_slugs[plan.slug] = plan.repo

        deletable = plan.deletable
        no_pr = plan.of("no-pr")
        total_deletable += len(deletable)

        if not deletable and not (show_no_pr and no_pr):
            console.print(f"[green]✓[/green] {plan.repo}: nothing to prune")
            continue

        console.print(f"\n[bold white]{plan.repo}[/bold white]")

        for verdict in deletable:
            if apply:
                result = delete_remote_branch(repo_path, verdict.branch)
                if result.status == "deleted":
                    total_deleted += 1
                    console.print(f"  [red]✗ deleted[/red] {verdict.branch} [dim](PR #{verdict.pr})[/dim]")
                else:
                    console.print(f"  [yellow]! failed[/yellow] {verdict.branch}: {result.error}")
            else:
                console.print(f"  [yellow]would delete[/yellow] {verdict.branch} [dim](PR #{verdict.pr} merged)[/dim]")

        if show_no_pr and no_pr:
            console.print(f"  [dim]{len(no_pr)} branch(es) with no PR — review manually, never auto-deleted:[/dim]")
            for verdict in no_pr:
                console.print(f"    [dim]· {verdict.branch}[/dim]")

    console.print()
    if apply:
        console.print(f"[bold]Deleted {total_deleted}/{total_deletable} merged branch(es).[/bold]")
    elif total_deletable:
        console.print(f"[bold]{total_deletable} merged branch(es) would be deleted. Re-run with --apply.[/bold]")
    else:
        console.print("[bold green]No merged branches left behind.[/bold green]")


@app.command()
def check_names(
    path: str | None = typer.Argument(None, help="Target repository directory path (defaults to CWD)"),
    all: bool = typer.Option(False, "--all", "-a", help="Check all personal repositories under <code root>/personal"),
    org: str | None = typer.Option(None, "--org", "-o", help="Target GitHub organization (e.g. rtkelly-labs)"),
    strict: bool = typer.Option(
        False, "--strict", help="Fail on badly-shaped names too, not just on names no job declares"
    ),
    lexicon: bool = typer.Option(False, "--lexicon", help="Print the preferred check-name lexicon and exit"),
) -> None:
    """Verifies required check names exist in the workflows, and warns on names that will drift.

    Two findings. Drift — a required check that no `pull_request` job declares — always fails,
    because enforcing it blocks every PR in that repo on a check which cannot report. Shape — a
    name that breaks the lexicon — warns, and fails only under `--strict`, so aligned repos can be
    held to the rule without the unaligned ones failing CI on day one.

    Name checks in lowercase snake_case, at most two words, preferably from --lexicon.
    A descriptive name has to change when the job changes, and branch protection
    matches on the string. Add to the lexicon rather than inventing a synonym.

    Renaming a job that is already required means updating its `required_checks` in
    config/repo_policies.json and re-running `enforce` in the same change, or the repo blocks
    until they agree.
    """
    if lexicon:
        table = Table(title="Preferred check names (lowercase snake_case, max 2 words)")
        table.add_column("Name", style="bold cyan")
        table.add_column("Use for", style="white")
        for name, meaning in PREFERRED_CHECK_NAMES.items():
            table.add_row(name, meaning)
        console.print(table)
        return

    console.print(Panel.fit("[bold cyan]Required Check Names: DRIFT & SHAPE[/bold cyan]", border_style="cyan"))
    targets = resolve_target_directories(path, all, org=org)

    drifted: list[str] = []
    warned: list[str] = []

    for target in targets:
        repo_name = get_gh_repo_name(target)
        if not repo_name:
            console.print(f"[yellow]? {target.name}: no GitHub remote, skipped[/yellow]")
            continue

        res = inspect_check_names(target, repo_name)

        if res.status == "no_pr_workflows":
            console.print(f"[dim]· {target.name} ({repo_name}): no pull_request workflows, skipped[/dim]")
            continue

        if res.missing:
            drifted.append(repo_name)
            console.print(f"[bold red]✗ {target.name} ({repo_name})[/bold red]")
            for name in res.missing:
                console.print(f"   [red]required check {name!r} is declared by no pull_request job[/red]")
            console.print(f"   [dim]declared: {sorted(res.declared)}[/dim]")
            console.print(f"   [dim]suggested required_checks: {sorted(res.declared)}[/dim]")
        elif res.warnings:
            console.print(f"[bold yellow]⚠ {target.name} ({repo_name})[/bold yellow]")
        else:
            console.print(f"[bold green]✓ {target.name} ({repo_name}): {list(res.required)}[/bold green]")

        for name, warns in res.warnings.items():
            warned.append(repo_name)
            declared_at = res.declared.get(name)
            where = f" [{declared_at}]" if declared_at else ""
            console.print(f"   [yellow]{name!r}{where}[/yellow]")
            for warn in warns:
                console.print(f"      [dim]- {warn}[/dim]")

    console.print("\n" + "=" * 50)
    if drifted:
        console.print(f"[bold red]FAILED: {len(set(drifted))} repo(s) require a check no job declares.[/bold red]")
        console.print(
            "[dim]Rename the job and update required_checks in config/repo_policies.json in the same change.[/dim]"
        )
        sys.exit(1)
    if warned and strict:
        console.print(f"[bold red]FAILED (--strict): {len(set(warned))} repo(s) have badly-shaped names.[/bold red]")
        console.print("[dim]See 'repo-governance check-names --lexicon' for the preferred names.[/dim]")
        sys.exit(1)
    if warned:
        console.print(
            f"[bold yellow]OK with warnings: {len(set(warned))} repo(s) have names that will drift.[/bold yellow]"
        )
        console.print("[dim]See 'repo-governance check-names --lexicon' for the preferred names.[/dim]")
        sys.exit(0)
    console.print("[bold green]SUCCESS: every required check is declared and well-named.[/bold green]")
    sys.exit(0)
