"""`estate orca`: Orca's sidebar against the layout managed-projects.json declares.

Kept apart from `cli.py`, which registers it, so neither module grows past the
size ratchet. The comparison is in `orca.py` and the Orca client in
`orca_runtime.py`.
"""

from __future__ import annotations

import pathlib

import typer
from pydantic import TypeAdapter
from rich.console import Console
from rich.table import Table

from rig import roots, scopes
from rig.estate import orca
from rig.estate.github_archived import archived_slugs
from rig.estate.orca_runtime import (
    ORCA_UNAVAILABLE_EXIT,
    OrcaRuntime,
    OrcaUnavailableError,
    estate_user_data,
    work_user_data,
)
from rig.workspace_config import load_skip_repos

console = Console()
err = Console(stderr=True)

_FINDINGS: TypeAdapter[list[orca.Finding]] = TypeAdapter(list[orca.Finding])


def _print(findings: list[orca.Finding]) -> None:
    if not findings:
        console.print("[green]Orca matches managed-projects.json[/green]")
        return
    table = Table(title=f"{len(findings)} difference(s) from the declared Orca layout", title_style="bold")
    table.add_column("kind")
    table.add_column("repo")
    table.add_column("path", style="dim")
    table.add_column("reason")
    table.add_column("action")
    table.add_column("command", style="cyan")
    for item in findings:
        table.add_row(item.kind, item.repo, item.path, item.reason, item.suggested_action, item.command)
    console.print(table)


def _archived(declared: list[orca.Declared], state: orca.Snapshot) -> frozenset[str]:
    """Which declared or registered estate repos GitHub reports archived; warns when it cannot be asked."""
    registered = [r.slug for r in state.registrations if r.slug and scopes.is_estate_owner(r.slug.split("/")[0])]
    found = archived_slugs([p.slug for p in declared] + registered)
    if found is None:
        err.print(
            "[yellow]GitHub could not be asked which repos are archived; only archived-projects.json counts[/yellow]"
        )
        return frozenset()
    return found


def _work_profile(work_dir: pathlib.Path, *, apply: bool) -> list[orca.Finding]:
    """The work profile's estate repos, removed first with --apply; skipped with a warning when it is not running."""
    try:
        runtime = OrcaRuntime.connect(work_dir)
        if apply:
            orca.apply_work(runtime, log=lambda line: err.print(f"[green]{line}[/green]"))
        return orca.audit_work(orca.snapshot(runtime))
    except OrcaUnavailableError as e:
        err.print(f"[yellow]work Orca profile not checked: {e}[/yellow]")
        return []


def orca_layout(
    config: str | None = typer.Option(None, "--config", "-c", help="Path to managed-projects.json"),
    code_root: str | None = typer.Option(None, "--code-root", help="Folder holding the group folders (default ~/code)"),
    user_data: str | None = typer.Option(
        None,
        "--orca-data",
        help="The estate's Orca user-data folder (default: %APPDATA%\\orca-personal on Windows, Orca's own elsewhere)",
    ),
    work_data: str | None = typer.Option(
        None,
        "--work-orca-data",
        help="The work Orca profile, kept free of estate repos (default: Orca's own folder on Windows; none elsewhere)",
    ),
    apply: bool = typer.Option(
        False,
        "--apply",
        help="Register, create, move and delete to match, and clear estate repos from the work profile.",
    ),
    repos: list[str] | None = typer.Option(
        None,
        "--repo",
        "-r",
        help="Act on and report only this managed project (repeatable). With --apply, registers and groups only "
        "the named projects, deletes no empty group and leaves the work profile alone.",
    ),
    as_json: bool = typer.Option(False, "--json", help="Write the findings as JSON to stdout"),
) -> None:
    """Orca's projects and groups against the `orca.group` each managed project declares.

    Reports unregistered clones, projects in the wrong group, missing groups,
    empty undeclared groups, stale registrations, and estate repos in Orca that
    managed-projects.json does not place. On Windows it also reports estate
    repos in the work Orca profile. Read-only unless --apply, which registers
    clones, creates groups, moves projects and deletes empty groups through the
    running Orca app, and removes estate repos (and the groups they leave
    empty) from the work profile; what is left afterwards needs a config change
    or a clone. Projects in this machine's `tools.estate.skip_repos`
    (`~/.workspace/config.json`) are never registered; one already in Orca is
    reported as `skipped`, not removed.
    """
    root = pathlib.Path(code_root).expanduser() if code_root else roots.code_root()
    estate_dir = pathlib.Path(user_data).expanduser() if user_data else estate_user_data()
    work_dir = pathlib.Path(work_data).expanduser() if work_data else work_user_data()
    try:
        declared = orca.load_declared(pathlib.Path(config).resolve() if config else roots.managed_projects_config())
        only = frozenset(repos) if repos else None
        if only is not None and (unknown := sorted(only - {d.name for d in declared})):
            err.print(f"[red]not a managed project: {', '.join(unknown)}[/red]")
            raise typer.Exit(2)
        skipped = load_skip_repos()
        runtime = OrcaRuntime.connect(estate_dir)
        archived = _archived(declared, orca.snapshot(runtime))
        carried: list[orca.Finding] = []
        if apply:
            carried = orca.apply(
                runtime,
                declared,
                code_root=root,
                log=lambda line: err.print(f"[green]{line}[/green]"),
                archived=archived,
                skipped=skipped,
                only=only,
            )
        findings = orca.audit(declared, orca.snapshot(runtime), code_root=root, archived=archived, skipped=skipped)
        findings += [c for c in carried if c not in findings]
        if only is not None:
            findings = orca.scoped(findings, declared, only)
    except OrcaUnavailableError as e:
        err.print(f"[red]{e}[/red]")
        raise typer.Exit(ORCA_UNAVAILABLE_EXIT) from e
    except (OSError, ValueError) as e:
        err.print(f"[red]{e}[/red]")
        raise typer.Exit(2) from e
    if not repos and work_dir is not None and work_dir.resolve() != estate_dir.resolve():
        findings += _work_profile(work_dir, apply=apply)

    if as_json:
        typer.echo(_FINDINGS.dump_json(findings, indent=2).decode())
    else:
        _print(findings)
