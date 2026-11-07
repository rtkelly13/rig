import typer
from rich.console import Console
from rich.table import Table

from rig.obsidian.commands.apps import (
    graph_app,
    graph_links_app,
    graph_metrics_app,
    graph_orphans_app,
    graph_repair_app,
)
from rig.obsidian.config import Config

console = Console()

graph_app.add_typer(graph_links_app, name="links")
graph_app.add_typer(graph_metrics_app, name="metrics")
graph_app.add_typer(graph_orphans_app, name="orphans")
graph_app.add_typer(graph_repair_app, name="repair")


@graph_links_app.command("list")
def graph_list_broken() -> None:
    """Identify dead wikilinks in the vault."""
    from rig.obsidian.link_manager import run_list_links

    config = Config()
    report = run_list_links(config.tvs_vault_path, log=console.print)
    if not report.broken and not report.unlinked:
        console.print("[green]✓ No broken links or unlinked mentions found.[/green]")


@graph_links_app.command("fix")
def graph_fix_broken(apply: bool = typer.Option(False, "--apply", help="Apply fixes to files")) -> None:
    """Repair dead wikilinks in the vault."""
    from rig.obsidian.link_manager import run_fix_links

    if not apply:
        console.print("[yellow]Dry run only. Re-run with --apply to write fixes.[/yellow]")
        return

    config = Config()
    result = run_fix_links(config.tvs_vault_path, log=console.print)
    console.print(f"[green]✓ Fixed {result.fixed} of {result.broken_found} broken links.[/green]")


@graph_orphans_app.command("list")
def graph_list_orphans() -> None:
    """Find notes with no incoming links."""
    from rig.obsidian.orphan_manager import run_list_orphans

    config = Config()
    orphans = run_list_orphans(config.tvs_vault_path, log=console.print)
    if not orphans:
        console.print("[green]✓ No orphan notes found.[/green]")


@graph_orphans_app.command("link")
def graph_link_orphans(apply: bool = typer.Option(False, "--apply", help="Apply changes to files")) -> None:
    """Automatically link orphans to PARA hub notes."""
    from rig.obsidian.orphan_manager import run_link_orphans

    if not apply:
        console.print("[yellow]Dry run only. Re-run with --apply to link orphan notes.[/yellow]")
        return

    config = Config()
    result = run_link_orphans(config.tvs_vault_path, log=console.print)
    console.print(f"[green]✓ Linked {result.linked} of {result.orphans_found} orphan notes.[/green]")


@graph_app.command("hubs")
def graph_hubs(apply: bool = typer.Option(False, "--apply", help="Create or update PARA root hubs")) -> None:
    """Create or maintain PARA root hub notes (Home, Areas, etc)."""
    from rig.obsidian.orphan_manager import run_root_hub

    if not apply:
        console.print("[yellow]Dry run only. Re-run with --apply to create and update hub notes.[/yellow]")
        return

    result = run_root_hub(log=console.print)
    console.print(f"[green]✓ Created {result.hubs_created} hubs and added {result.links_added} root links.[/green]")


@graph_app.command("lint")
def graph_lint(json_output: bool = typer.Option(False, "--json", help="Emit machine-readable JSON")) -> None:
    """Scan for duplicate URLs and other vault inconsistencies.

    Codes:

    - TVS000-TVS004 (error): invalid frontmatter, missing type, missing or empty required
      field or list
    - TVS005 (warning): unknown note type
    - TVS101-TVS108 (warning): filename is URL-shaped, non-ASCII, has mixed or repeated
      separators, dots in the stem, a handle prefix, can be simplified, or is not portable to Windows
    - TVS201 (error): duplicate note group

    A note silences codes with `lint_ignore` (or `lint-ignore`) in its frontmatter: one
    code, a list of codes, `TVS` for every TVS code, or `ALL`. Prefer fixing the note.
    """
    from rig.obsidian.lint_manager import run_vault_lint

    config = Config()
    run_vault_lint(config.tvs_vault_path, json_output=json_output)


@graph_repair_app.command("duplicates")
def graph_cleanup_dupes(apply: bool = typer.Option(False, "--apply", help="Apply cleanup fixes")) -> None:
    """Remove redundant URLs from social posts and consolidate duplicate resources."""
    from rig.obsidian.lint_manager import VaultLinter

    config = Config()
    linter = VaultLinter(config.tvs_vault_path)

    console.print("[bold cyan]Phase 1: Social Media Metadata Cleanup[/bold cyan]")
    linter.cleanup_social_duplicates(dry_run=not apply)

    console.print("\n[bold cyan]Phase 2: Resource Consolidation[/bold cyan]")
    linter.cleanup_resource_duplicates(dry_run=not apply)


@graph_repair_app.command("backfill-types")
def graph_backfill_types(apply: bool = typer.Option(False, "--apply", help="Apply type backfills")) -> None:
    """Automatically add 'type' field to notes based on folder location."""
    from rig.obsidian.lint_manager import VaultLinter

    config = Config()
    linter = VaultLinter(config.tvs_vault_path)
    linter.backfill_types(dry_run=not apply)


@graph_repair_app.command("metadata")
def graph_repair_metadata(apply: bool = typer.Option(False, "--apply", help="Apply metadata repairs")) -> None:
    """Repair missing metadata across the vault."""
    from rig.obsidian.lint_manager import VaultLinter

    config = Config()
    linter = VaultLinter(config.tvs_vault_path)
    linter.repair_metadata(dry_run=not apply)


@graph_repair_app.command("normalize")
def graph_repair_normalize(apply: bool = typer.Option(False, "--apply", help="Apply normalization changes")) -> None:
    """Sync note metadata based on PARA folder location."""
    from rig.obsidian.lint_manager import VaultLinter

    config = Config()
    linter = VaultLinter(config.tvs_vault_path)
    linter.backfill_types(dry_run=not apply)
    linter.repair_metadata(dry_run=not apply)


@graph_repair_app.command("filenames")
def graph_repair_filenames(apply: bool = typer.Option(False, "--apply", help="Apply filename repairs")) -> None:
    """Rename ugly note filenames and update wikilinks."""
    from rig.obsidian.lint_manager import VaultLinter

    config = Config()
    linter = VaultLinter(config.tvs_vault_path)
    linter.cleanup_filenames(dry_run=not apply)


@graph_metrics_app.command("folder-scores")
def graph_folder_scores() -> None:
    """Display health scores for each folder based on linting failures."""
    from rig.obsidian.lint_manager import VaultLinter

    config = Config()
    linter = VaultLinter(config.tvs_vault_path)
    stats = linter.get_folder_lint_stats()

    table = Table(title="Vault Folder Health Scores")
    table.add_column("Folder", style="cyan")
    table.add_column("Notes", justify="right")
    table.add_column("Errors", justify="right", style="red")
    table.add_column("Dupes", justify="right", style="yellow")
    table.add_column("Score", justify="right", style="bold green")

    for folder, s in sorted(stats.items(), key=lambda item: item[1].score):
        score_color = "green"
        if s.score < 90:
            score_color = "yellow"
        if s.score < 70:
            score_color = "red"
        table.add_row(
            folder,
            str(s.total_notes),
            str(s.metadata_errors),
            str(s.duplicates),
            f"[{score_color}]{s.score:.1f}[/]",
        )

    console.print(table)


@graph_metrics_app.command("health")
def graph_health() -> None:
    """Assess the health of vault folders based on density and organization."""
    from rig.obsidian.utils import get_vault_stats

    config = Config()
    stats = get_vault_stats(config.tvs_vault_path)

    table = Table(title="Vault Health by Folder")
    table.add_column("Folder", style="cyan")
    table.add_column("Density (Notes)", justify="right")
    table.add_column("Status", justify="center")

    for folder, count in stats.items():
        status = "[green]Healthy[/green]"
        if count > 50:
            status = "[yellow]Dense[/yellow]"
        if count > 100:
            status = "[red]Overcrowded[/red]"
        if count == 0:
            status = "[dim]Empty[/dim]"
        table.add_row(folder, str(count), status)

    console.print(table)
