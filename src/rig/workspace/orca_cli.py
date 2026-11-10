"""`workspace orca`: audit and sync the Orca install against `config/orca.json`."""

from __future__ import annotations

import json
import os
import pathlib
from dataclasses import dataclass

import typer
from rich.markup import escape
from rich.table import Table

from rig.devenv.ui import make_console, table_box
from rig.estate.orca_runtime import ORCA_UNAVAILABLE_EXIT, OrcaRuntime, OrcaUnavailableError
from rig.typed_json import decode_json
from rig.workspace.engine import config_file_path, estate_root_path, retired_project_names
from rig.workspace.orca import (
    CONFIG_RELATIVE_PATH,
    OrcaPolicy,
    OrcaSnapshot,
    SyncAction,
    diagnose,
    live_settings,
    load_policy,
    orca_profile_dir,
    orca_runner,
    settings_manual_actions,
    sync_repos,
    take_snapshot,
)
from rig.workspace.orca_agent_args import sync_agent_args
from rig.workspace.orca_rpc import USER_DATA_ENV, orca_runtime_metadata_path
from rig.workspace.packages import Status

orca_cli = typer.Typer(
    name="orca",
    help="Manage Orca workspace configuration (agent flags, repos, hooks, base refs, colors).",
    no_args_is_help=True,
    add_completion=False,
)
console = make_console()

_STYLE = {
    Status.OK: "green",
    Status.WARN: "yellow",
    Status.FAIL: "bold red",
    Status.MANUAL: "cyan",
    Status.SKIP: "dim",
}
_SYNC_STATUS = {"error": "error", "manual": "manual"}
_SYNC_STYLE = {"applied": "green", "planned": "yellow", "manual": "cyan", "error": "bold red"}


@dataclass(slots=True, frozen=True)
class _Project:
    name: str
    enabled: bool = True


@dataclass(slots=True, frozen=True)
class _ManagedProjects:
    projects: tuple[_Project, ...] = ()


_ORCA_DATA_HELP = (
    "Orca's user-data folder, e.g. %APPDATA%/orca-personal "
    "(default: the Orca that launched this terminal, else the platform's)"
)


def _user_data(orca_data: str | None) -> pathlib.Path | None:
    """The instance to target: ``--orca-data``, else the Orca whose terminal this is, else the default."""
    chosen = orca_data or os.environ.get(USER_DATA_ENV)
    return pathlib.Path(chosen).expanduser() if chosen else None


def _snapshot(user_data: pathlib.Path | None) -> OrcaSnapshot:
    return take_snapshot(orca_profile_dir(user_data), orca_runner(user_data), live=lambda: live_settings(user_data))


def _sync_agents(policy: OrcaPolicy, user_data: pathlib.Path | None, *, apply: bool) -> list[SyncAction]:
    """Raises `OrcaUnavailableError` when Orca cannot be read."""
    runtime = OrcaRuntime.from_metadata(orca_runtime_metadata_path(user_data))
    return sync_agent_args(policy, runtime, apply=apply)


def _managed_project_names(registry: pathlib.Path) -> tuple[str, ...]:
    managed = decode_json(_ManagedProjects, registry.read_text(encoding="utf-8"))
    return tuple(project.name for project in managed.projects if project.enabled)


@orca_cli.command(name="doctor")
def doctor_command(
    config: str | None = typer.Option(None, "--config", "-c", help="Path to orca.json"),
    estate_root: str | None = typer.Option(None, "--root", help="Estate root holding the managed projects"),
    orca_data: str | None = typer.Option(None, "--orca-data", help=_ORCA_DATA_HELP),
    json_output: bool = typer.Option(False, "--json", help="Output findings as JSON"),
) -> None:
    """Compare Orca's saved settings and live repos with config/orca.json. Read-only; exits 1 on any failure."""
    policy_path = pathlib.Path(config).resolve() if config else config_file_path().parent.parent / CONFIG_RELATIVE_PATH
    findings = diagnose(
        load_policy(policy_path),
        _snapshot(_user_data(orca_data)),
        estate_root=estate_root_path(estate_root),
        managed_projects=_managed_project_names(config_file_path()),
    )

    if json_output:
        payload = [
            {"subject": f.subject, "check": f.check, "status": f.status.value, "detail": f.detail, "fix": f.fix}
            for f in findings
        ]
        typer.echo(json.dumps(payload, indent=2))
    else:
        table = Table(title=f"Orca vs {policy_path.name}", expand=True, box=table_box())
        table.add_column("Subject", style="bold", no_wrap=True)
        table.add_column("Check", no_wrap=True)
        table.add_column("Status", no_wrap=True)
        table.add_column("Detail")
        last = ""
        for f in findings:
            name = f.subject if f.subject != last else ""
            last = f.subject
            detail = escape(f.detail) + (f"\n[dim]fix: {escape(f.fix)}[/dim]" if f.fix else "")
            style = _STYLE[f.status]
            table.add_row(name, f.check, f"[{style}]{f.status.value}[/{style}]", detail)
        console.print(table)

    if any(f.status is Status.FAIL for f in findings):
        raise typer.Exit(code=1)


@orca_cli.command(name="sync")
def sync_command(
    apply: bool = typer.Option(False, "--apply", help="Apply planned changes (default is dry-run)"),
    config: str | None = typer.Option(None, "--config", "-c", help="Path to orca.json"),
    repo: str | None = typer.Option(None, "--repo", "-r", help="Sync only the named repo"),
    estate_root: str | None = typer.Option(None, "--root", help="Estate root holding the managed projects"),
    orca_data: str | None = typer.Option(None, "--orca-data", help=_ORCA_DATA_HELP),
    json_output: bool = typer.Option(False, "--json", help="Output actions as JSON"),
) -> None:
    """Sync Orca to config/orca.json: agent default args, then repos (registration, colors, icons, scripts, refs).

    Exits 3 when Orca cannot be reached, 1 when any change failed.
    """
    policy_path = pathlib.Path(config).resolve() if config else config_file_path().parent.parent / CONFIG_RELATIVE_PATH
    policy = load_policy(policy_path)
    user_data = _user_data(orca_data)
    snapshot = _snapshot(user_data)
    root = estate_root_path(estate_root)

    try:
        actions = [] if repo is not None else _sync_agents(policy, user_data, apply=apply)
    except OrcaUnavailableError as e:
        typer.echo(str(e), err=True)
        raise typer.Exit(ORCA_UNAVAILABLE_EXIT) from e
    if repo is None:
        actions += settings_manual_actions(policy, snapshot)
    actions += sync_repos(
        policy,
        snapshot,
        estate_root=root,
        apply=apply,
        target_repo=repo,
        user_data=user_data,
        retired=retired_project_names(),
    )

    if json_output:
        payload = [
            {
                "repo": a.repo,
                "action": a.action,
                "field": a.field,
                "old_value": a.old_value,
                "new_value": a.new_value,
                "applied": a.applied,
                "detail": a.detail,
            }
            for a in actions
        ]
        typer.echo(json.dumps(payload, indent=2))
    else:
        mode_str = "[green]APPLIED[/green]" if apply else "[yellow]DRY-RUN (pass --apply to execute)[/yellow]"
        table = Table(title=f"Orca Sync vs {policy_path.name} ({mode_str})", expand=True, box=table_box())
        table.add_column("Repo", style="bold", no_wrap=True)
        table.add_column("Action", no_wrap=True)
        table.add_column("Field", no_wrap=True)
        table.add_column("Current")
        table.add_column("Target")
        table.add_column("Status", no_wrap=True)

        if not actions:
            console.print("[green]All Orca repositories are in sync with policy.[/green]")
            return

        for a in actions:
            status_text = "applied" if a.applied else _SYNC_STATUS.get(a.action, "planned")
            status_style = _SYNC_STYLE[status_text]
            detail = f"\n[dim]{escape(a.detail)}[/dim]" if a.detail and not a.applied and a.detail != "planned" else ""
            table.add_row(
                a.repo,
                a.action,
                a.field,
                escape(a.old_value or "(none)"),
                escape(a.new_value or "(none)"),
                f"[{status_style}]{status_text}[/{status_style}]{detail}",
            )
        console.print(table)

    if any(a.action == "error" for a in actions):
        raise typer.Exit(code=1)
