"""`workspace packages`: track and audit published packages across estate repositories."""

from __future__ import annotations

import json
import pathlib

import typer
from rich.console import Console
from rich.markup import escape
from rich.table import Table

from rig.workspace.engine import config_file_path, estate_root_path
from rig.workspace.package_sources import LiveFetcher
from rig.workspace.packages import (
    PackageSyncStatus,
    Status,
    audit_packages,
    format_relative_age,
    load_package_bindings,
    track_packages,
)

packages_cli = typer.Typer(
    name="packages",
    help="Track and audit published npm/NuGet packages against the GitHub repos that publish them.",
    no_args_is_help=False,
    add_completion=False,
)
console = Console()

_STYLE = {
    Status.OK: "green",
    Status.WARN: "yellow",
    Status.FAIL: "bold red",
    Status.MANUAL: "cyan",
    Status.SKIP: "dim",
}

_SYNC_STYLE = {
    PackageSyncStatus.SYNCED: "bold green",
    PackageSyncStatus.UNRELEASED: "bold yellow",
    PackageSyncStatus.BEHIND: "bold red",
    PackageSyncStatus.UNPUBLISHED: "cyan",
    PackageSyncStatus.UNKNOWN: "dim",
}


@packages_cli.callback(invoke_without_command=True)
def packages_main(
    ctx: typer.Context,
    config: str | None = typer.Option(None, "--config", "-c", help="Path to managed-projects.json registry file"),
    project: str | None = typer.Option(None, "--project", "-p", help="Only this project"),
    json_output: bool = typer.Option(False, "--json", help="Output tracking records as JSON"),
) -> None:
    """Track deployed packages, versions, and publication status."""
    if ctx.invoked_subcommand is not None:
        return
    status_command(config=config, project=project, json_output=json_output)


@packages_cli.command(name="status")
def status_command(
    config: str | None = typer.Option(None, "--config", "-c", help="Path to managed-projects.json registry file"),
    project: str | None = typer.Option(None, "--project", "-p", help="Only this project"),
    json_output: bool = typer.Option(False, "--json", help="Output tracking records as JSON"),
) -> None:
    """Track deployed packages, local versions, registry releases, and sync status."""
    cfg = pathlib.Path(config).resolve() if config else config_file_path()
    bindings = load_package_bindings(cfg)
    if project:
        bindings = [b for b in bindings if b.project.lower() == project.lower()]

    estate_root = estate_root_path()
    records = track_packages(bindings, LiveFetcher(), estate_root=estate_root)

    if json_output:
        payload = [
            {
                "project": r.project,
                "package": r.package,
                "registry": r.registry,
                "manifest_path": r.manifest_path,
                "local_version": r.local_version,
                "latest_version": r.latest_version,
                "latest_stable_version": r.latest_stable_version,
                "deployed_version": r.deployed_version,
                "status": r.status.value,
                "published_at": r.published_at.isoformat() if r.published_at else None,
                "latest_published_at": r.latest_published_at.isoformat() if r.latest_published_at else None,
                "stable_published_at": r.stable_published_at.isoformat() if r.stable_published_at else None,
                "registry_url": r.registry_url,
                "detail": r.detail,
            }
            for r in records
        ]
        typer.echo(json.dumps(payload, indent=2))
        return

    table = Table(title=f"Deployed Packages & Artifacts ({len(records)})", expand=True)
    table.add_column("Project", style="bold", no_wrap=True)
    table.add_column("Package", style="bold cyan", no_wrap=True)
    table.add_column("Registry", style="magenta", no_wrap=True)
    table.add_column("Local", style="bold", no_wrap=True)
    table.add_column("Latest", style="bold", no_wrap=True)
    table.add_column("Stable", style="bold", no_wrap=True)
    table.add_column("Status", no_wrap=True)
    table.add_column("Published", no_wrap=True)
    table.add_column("Registry URL", style="dim")

    for r in records:
        style = _SYNC_STYLE[r.status]
        status_badge = f"[{style}]{r.status.value}[/{style}]"
        published_dt = r.latest_published_at or r.published_at
        published_str = (
            f"{published_dt.strftime('%Y-%m-%d')} ({format_relative_age(published_dt)})"
            if published_dt
            else "[dim]--[/dim]"
        )
        local_str = r.local_version or "[dim]--[/dim]"
        latest_str = r.latest_version or "[dim]--[/dim]"
        stable_str = r.latest_stable_version or "[dim]--[/dim]"
        table.add_row(
            r.project,
            r.package,
            r.registry,
            local_str,
            latest_str,
            stable_str,
            status_badge,
            published_str,
            r.registry_url,
        )

    console.print(table)


@packages_cli.command(name="list")
def list_command(
    config: str | None = typer.Option(None, "--config", "-c", help="Path to managed-projects.json registry file"),
    project: str | None = typer.Option(None, "--project", "-p", help="Only this project"),
    json_output: bool = typer.Option(False, "--json", help="Output tracking records as JSON"),
) -> None:
    """Alias for 'workspace packages status'."""
    status_command(config=config, project=project, json_output=json_output)


@packages_cli.command(name="audit")
def audit_command(
    config: str | None = typer.Option(None, "--config", "-c", help="Path to managed-projects.json registry file"),
    project: str | None = typer.Option(None, "--project", "-p", help="Only this project"),
    trust: bool = typer.Option(
        False,
        "--trust",
        help="Also read npm trust configs (`npm trust list`; prompts for a 2FA code) "
        "and print every NuGet policy's checklist, not only those that look missing or stale",
    ),
    json_output: bool = typer.Option(False, "--json", help="Output findings as JSON"),
    strict: bool = typer.Option(False, "--strict", help="Exit non-zero on any failing check"),
) -> None:
    """Check each declared package's manifest, workflow, registry provenance and publish runs.

    Packages are the `packages` entries in config/managed-projects.json. Checks:

    - repo: the declared repo redirects, so it was moved.
    - manifest: its repository URL names another repo. Fails for npm, which
      refuses the publish with a 422; warns for NuGet.
    - version: warns when the manifest is ahead of the registry's latest.
    - workflow: `auth: oidc` without `id-token: write`; warns on a token secret.
    - login (NuGet): no job runs NuGet/login (naming any reusable workflow it
      calls, whose file the policy must name instead), that job lacks
      `id-token: write`, or its `user:` differs from `nuget_user`.
    - environment (NuGet): the login job's `environment:` differs from the
      declared `environment`.
    - push (NuGet): warns when `nuget push` is missing, reads a secrets.NUGET_*
      key, or lacks --skip-duplicate, so a rerun after a partial publish fails.
    - trusted-publisher: warns when npm's latest was published with a token.
    - provenance: the last publish recorded a different repo id or owner,
      so the repo moved since the last publish.
    - registry (NuGet): warns when the package does not exist (the first publish
      needs a push-new policy) or every version is unlisted.
    - owners (NuGet): warns when search owners lack `nuget_owner`. Search
      reindexes only on a publish, so confirm on the package's Manage page.
    - listed (NuGet): warns when the newest listed version is a CI build.
    - vulnerabilities (NuGet): fails when the newest listed version has an advisory.
    - deprecation (NuGet): warns when the newest listed version is deprecated.
    - trust: --trust only. The npm trust config does not name the repo and workflow.
    - policy (NuGet): not a check. The fields to enter on nuget.org, printed when
      registry or provenance suggests the policy is missing, and always under --trust.
    - runs: the latest completed publish runs failed.

    See docs/packages/README.md for the publishing runbooks.
    """
    cfg = pathlib.Path(config).resolve() if config else config_file_path()
    bindings = load_package_bindings(cfg)
    if project:
        bindings = [b for b in bindings if b.project.lower() == project.lower()]
    findings = audit_packages(bindings, LiveFetcher(), trust=trust)
    failed = any(f.status is Status.FAIL for f in findings)

    if json_output:
        payload = [
            {"package": f.package, "check": f.check, "status": f.status.value, "detail": f.detail, "fix": f.fix}
            for f in findings
        ]
        typer.echo(json.dumps(payload, indent=2))
    else:
        table = Table(title=f"Published packages ({len(bindings)})", expand=True)
        table.add_column("Package", style="bold", no_wrap=True)
        table.add_column("Check", no_wrap=True)
        table.add_column("Status", no_wrap=True)
        table.add_column("Detail")
        last = ""
        for f in findings:
            name = f.package if f.package != last else ""
            last = f.package
            detail = escape(f.detail) + (f"\n[dim]fix: {escape(f.fix)}[/dim]" if f.fix else "")
            style = _STYLE[f.status]
            table.add_row(name, f.check, f"[{style}]{f.status.value}[/{style}]", detail)
        console.print(table)
        if not trust and any(b.spec.registry == "npm" and b.spec.auth == "oidc" for b in bindings):
            console.print("[dim]npm trust configs not read; pass --trust (needs a 2FA code).[/dim]")
        if not trust and any(b.spec.registry == "nuget" and b.spec.auth == "oidc" for b in bindings):
            console.print("[dim]NuGet policies have no API; pass --trust to print every policy's checklist.[/dim]")

    if strict and failed:
        raise typer.Exit(code=1)


@packages_cli.command(name="dashboard")
def dashboard_command(
    root: str | None = typer.Option(None, "--root", help="Code root to scan"),
    config: str | None = typer.Option(None, "--config", "-c", help="Path to managed-projects.json registry file"),
    project: str | None = typer.Option(None, "--project", "-p", help="Only this project"),
    json_output: bool = typer.Option(False, "--json", help="Emit inventory as JSON for a machine"),
) -> None:
    """Every published package with its registry version, matching git tag and unreleased changelog entries."""
    from rig.workspace import package_dashboard

    package_dashboard.run_packages(root=root, config=config, project=project, json_output=json_output)
