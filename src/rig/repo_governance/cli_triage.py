"""Policy application commands: ``apply-policy`` and ``sync-policies``.

Registered on the ``repo-governance`` app by :mod:`.align_repo_conventions`. The triage commands run on C#.
"""

import pathlib
import sys

import typer
from pydantic import ValidationError
from rich.panel import Panel

from rig.repo_governance.cli_common import console
from rig.repo_governance.policy_config import config_path, load_policy_config

app = typer.Typer()


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
    public_only: bool = typer.Option(True, "--public-only/--all", help="Only sync public open source profile repos"),
    dry_run: bool = typer.Option(False, "--dry-run", help="Preview changes without writing files"),
) -> None:
    """Synchronizes governance policies across the profiles `config/repo_policies.json` declares."""
    from rig.repo_governance.policy_template_engine import (
        apply_ai_policy,
        apply_security_policy,
        detect_repo_metadata,
        inject_readme_ai_disclosure,
    )

    try:
        config = load_policy_config()
    except ValidationError as exc:  # unreadable JSON is logged and skipped by the loader
        console.print(f"[bold red]❌ Invalid policy config {config_path()}: {exc}[/bold red]")
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

            meta = detect_repo_metadata(target_path, config)
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
