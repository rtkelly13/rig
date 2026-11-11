"""Commands that sync licenses and community templates.

Registered on the ``repo-governance`` app by :mod:`.align_repo_conventions`.
"""

import sys

import typer
from rich.panel import Panel

from rig.repo_governance.cli_common import console
from rig.repo_governance.license_manager import (
    LicenseAuditResult,
    align_license,
    audit_spdx_headers,
    inspect_license,
)
from rig.repo_governance.oss_templates import (
    OssTemplateContext,
    inspect_oss_templates,
    sync_oss_templates,
)
from rig.repo_governance.policy_config import (
    load_policy_config,
)
from rig.repo_governance.policy_key import policy_key
from rig.repo_governance.repo_discovery import (
    get_gh_repo_name,
    resolve_target_directories,
)

app = typer.Typer()


@app.command(name="license")
def license_command(
    path: str | None = typer.Argument(None, help="Target repo directory (defaults to current directory)"),
    audit: bool = typer.Option(False, "--audit", help="Audit license without mutating"),
    enforce: bool = typer.Option(False, "--enforce", help="Write/update canonical LICENSE file"),
    spdx: bool = typer.Option(False, "--spdx", help="Scan source files for SPDX headers"),
    license_type: str | None = typer.Option(
        None, "--type", "-t", help="Expected license type (MIT, Apache-2.0, BSD-3-Clause, Unlicense)"
    ),
    holder: str | None = typer.Option(None, "--holder", help="Expected copyright holder"),
    org: str | None = typer.Option(None, "--org", "-o", help="Target GitHub organization (e.g. rtkelly-labs)"),
    all: bool = typer.Option(
        False, "--all", "-a", help="Run across all personal repositories under <code root>/personal"
    ),
) -> None:
    """Audits and enforces canonical open source licenses and SPDX headers."""
    if not enforce:
        audit = True

    targets = resolve_target_directories(path, all, org=org)
    cfg = load_policy_config()
    display_type = license_type or "policy config"
    console.print(Panel.fit(f"[bold cyan]License Governance ({display_type})[/bold cyan]", border_style="cyan"))

    failures = 0
    res: LicenseAuditResult | None = None
    for target in targets:
        repo_name = get_gh_repo_name(target) or target.name
        if enforce:
            aligned = align_license(target, license_type=license_type, copyright_holder=holder)
            if aligned.changed:
                console.print(f"[green]✓ {target.name} ({repo_name}): {aligned.message}[/green]")
            else:
                console.print(f"[dim]· {target.name} ({repo_name}): {aligned.message}[/dim]")
        else:
            res = inspect_license(target, expected_type=license_type, copyright_holder=holder)
            if res.is_aligned:
                console.print(f"[green]✓ {target.name} ({repo_name}): {res.detected_type} (valid)[/green]")
            else:
                failures += 1
                console.print(f"[red]✗ {target.name} ({repo_name}): not aligned[/red]")
                for d in res.details:
                    console.print(f"   [dim]- {d}[/dim]")

        if spdx:
            fallback_type = res.expected_type if res is not None else cfg.license_type(policy_key(target))
            target_spdx: str = license_type or fallback_type
            spdx_res = audit_spdx_headers(target, expected_spdx_id=target_spdx)
            if spdx_res.missing_spdx:
                count = len(spdx_res.missing_spdx)
                total = spdx_res.total_files
                console.print(f"   [yellow]SPDX: {count}/{total} source files missing headers[/yellow]")
            else:
                console.print(f"   [green]SPDX: all {spdx_res.total_files} files compliant[/green]")

    if audit and failures > 0:
        console.print(f"\n[bold red]FAILED: {failures} repo(s) failed license compliance.[/bold red]")
        sys.exit(1)
    elif audit:
        console.print("\n[bold green]SUCCESS: all inspected repos have valid licenses.[/bold green]")


@app.command(name="sync-templates")
def sync_templates_command(
    path: str | None = typer.Argument(None, help="Target repo directory (defaults to current directory)"),
    audit: bool = typer.Option(False, "--audit", help="Audit templates without mutating"),
    apply: bool = typer.Option(False, "--apply", help="Write/update community and issue templates"),
    dry_run: bool = typer.Option(False, "--dry-run", help="Preview template writes without saving"),
    email: str | None = typer.Option(None, "--email", help="Contact email for Code of Conduct"),
    org: str | None = typer.Option(None, "--org", "-o", help="Target GitHub organization (e.g. rtkelly-labs)"),
    all: bool = typer.Option(
        False, "--all", "-a", help="Run across all personal repositories under <code root>/personal"
    ),
) -> None:
    """Audits and synchronizes GitHub Issue Forms, PR templates, and community health files."""
    if not apply:
        audit = True

    targets = resolve_target_directories(path, all, org=org)
    console.print(Panel.fit("[bold cyan]Community & Issue Templates[/bold cyan]", border_style="cyan"))

    cfg = load_policy_config()
    failures = 0
    for target in targets:
        repo_name = get_gh_repo_name(target) or target.name
        contact = email or cfg.contact_email(policy_key(target))
        ctx: OssTemplateContext = {
            "repo_name": target.name,
            "repo_full_name": repo_name,
            "contact_email": contact,
        }

        if apply:
            synced = sync_oss_templates(target, context=ctx, dry_run=dry_run)
            if synced.changed:
                console.print(f"[green]✓ {target.name} ({repo_name}):[/green]")
                for m in synced.messages:
                    console.print(f"   [dim]• {m}[/dim]")
            else:
                console.print(f"[dim]· {target.name} ({repo_name}): all templates up to date[/dim]")
        else:
            res = inspect_oss_templates(target)
            if res.is_fully_aligned:
                console.print(
                    f"[green]✓ {target.name} ({repo_name}): all {res.total_templates} templates present[/green]"
                )
            else:
                failures += 1
                missing_count = len(res.missing)
                msg = f"! {target.name} ({repo_name}): missing {missing_count}/{res.total_templates} templates"
                console.print(f"[yellow]{msg}[/yellow]")
                for m in res.missing:
                    console.print(f"   [dim]- {m}[/dim]")

    if audit and failures > 0:
        console.print(f"\n[bold yellow]Audit complete: {failures} repo(s) missing community templates.[/bold yellow]")
        console.print("[dim]Run with --apply to write missing templates.[/dim]")
    elif audit:
        console.print("\n[bold green]SUCCESS: all inspected repos have complete community templates.[/bold green]")
