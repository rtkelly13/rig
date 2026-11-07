"""`desktop-manager jobs`: the one place logon, recurring and daily machine jobs are run from."""

from __future__ import annotations

import sys
from dataclasses import dataclass
from datetime import datetime
from pathlib import Path

import typer
from rich.console import Console
from rich.markup import escape
from rich.table import Table

from desktop_manager.jobs import install as installer
from desktop_manager.jobs.registry import Job, Trigger, current_environment, default_jobs
from desktop_manager.jobs.runner import log_path, plan, run
from desktop_manager.jobs.session_windows import boot_time
from desktop_manager.jobs.state import (
    STATE_NAME,
    Outcome,
    RunRecord,
    jobs_dir,
    last_run_path,
    logs_dir,
    read_run,
    read_state,
)
from rig.platforms import Platform, requires

jobs_app = typer.Typer(
    name="jobs",
    no_args_is_help=True,
    help="Logon, every-15-minute and daily jobs that keep this machine aligned, run from one registry.",
)
console = Console()


def _jobs() -> tuple[Job, ...]:
    return default_jobs(current_environment())


@jobs_app.command(name="status")
def status(
    trigger: Trigger = typer.Option(Trigger.TICK, "--trigger", help="Show what a run on this trigger would do."),
) -> None:
    """Each job: when it runs, its last outcome, and whether the next run takes it."""
    state = read_state(jobs_dir() / STATE_NAME)
    table = Table(show_header=True, box=None, padding=(0, 2, 0, 0))
    for column in ("job", "runs on", "last run", "result", f"next {trigger}"):
        table.add_column(column)
    failed: dict[str, Outcome] = {}
    for item in plan(_jobs(), trigger, state, now=datetime.now().astimezone(), booted=boot_time()):
        job = item.job
        last = state.get(job.name)
        triggers = ", ".join(sorted(job.triggers))
        if last is not None and not last.ok:
            failed[job.name] = last
        table.add_row(
            job.name,
            f"{triggers} ({job.cadence})",
            "-" if last is None else f"{last.started_at:%a %H:%M} ({last.seconds:.0f}s)",
            "-" if last is None else escape(_result(last)),
            "run" if item.run else f"[dim]{escape(item.reason)}[/dim]",
        )
    console.print(table)
    for name, outcome in failed.items():
        _print_detail(name, outcome)
    tasks = [installer.LOGON_TASK, installer.TICK_TASK, installer.GUARD_TASK]
    missing = [name for name in tasks if sys.platform == "win32" and not installer.exists(name)]
    if missing:
        console.print(f"[yellow]Not registered:[/yellow] {', '.join(missing)}. Run `desktop-manager jobs install`.")
    console.print(f"[dim]Logs: {logs_dir()}[/dim]")


@jobs_app.command(name="last")
def last(
    trigger: Trigger = typer.Option(Trigger.LOGON, "--trigger", help="Show the latest run on this trigger."),
) -> None:
    """The latest run on a trigger: every job it ran or skipped, why, and how each went."""
    record = read_run(last_run_path(jobs_dir(), trigger))
    if record is None:
        console.print(f"[dim]No {trigger} run recorded yet.[/dim]")
        return
    _print_run(record)


def _print_run(record: RunRecord) -> None:
    finished = record.finished or "[yellow]did not finish[/yellow]"
    console.print(f"[bold]{record.run_id}[/bold]  {record.started} -> {finished}")
    table = Table(show_header=True, box=None, padding=(0, 2, 0, 0))
    for column in ("job", "", "took", "result"):
        table.add_column(column)
    for item in record.decisions:
        if item.outcome is None:
            table.add_row(item.job, "[dim]skip[/dim]", "", f"[dim]{escape(item.reason)}[/dim]")
            continue
        colour = "green" if item.outcome.ok else "red"
        table.add_row(
            item.job, f"[{colour}]ran[/{colour}]", f"{item.outcome.seconds:.0f}s", escape(_result(item.outcome))
        )
    console.print(table)
    for item in record.failed:
        if item.outcome is not None:
            _print_detail(item.job, item.outcome)
    console.print(f"[dim]Job logs: {logs_dir()} (sections headed [{escape(record.run_id)}])[/dim]")


def _result(outcome: Outcome) -> str:
    status = "ok" if outcome.ok else (outcome.error or f"exit {outcome.returncode}")
    return f"{status}: {outcome.summary}" if outcome.summary else status


def _print_detail(name: str, outcome: Outcome) -> None:
    if not outcome.detail:
        return
    console.print(f"\n[red]{escape(name)}[/red] failed; the end of its output:")
    for line in outcome.detail:
        console.print(f"  [dim]|[/dim] {escape(line)}")


@jobs_app.command(name="run")
def run_cmd(
    trigger: Trigger = typer.Option(Trigger.TICK, "--trigger", help="Run the jobs due on this trigger."),
    only: list[str] = typer.Option([], "--only", help="Run just this job, due or not. Repeatable."),
    force: bool = typer.Option(False, "--force", help="Run every job on the trigger, due or not."),
    dry_run: bool = typer.Option(False, "--dry-run", help="Say what would run, and run nothing."),
) -> None:
    """Run the due jobs now, in the foreground, as the scheduled tasks do."""
    jobs = _jobs()
    unknown = sorted(set(only) - {job.name for job in jobs})
    if unknown:
        console.print(f"[red]Unknown job:[/red] {', '.join(unknown)}")
        raise typer.Exit(code=2)
    if dry_run:
        state = read_state(jobs_dir() / STATE_NAME)
        for item in plan(
            jobs, trigger, state, now=datetime.now().astimezone(), booted=boot_time(), only=frozenset(only), force=force
        ):
            console.print(f"{'run ' if item.run else 'skip'} {item.job.name}: {escape(item.reason)}")
        return
    code = run(
        jobs,
        trigger,
        jobs_dir(),
        booted=boot_time(),
        only=frozenset(only),
        force=force,
        report=lambda line: console.print(escape(line)),
    )
    raise typer.Exit(code=code)


@jobs_app.command(name="log")
def log(job: str = typer.Argument(..., help="Job name."), lines: int = typer.Option(40, "--lines", "-n")) -> None:
    """Show the end of a job's log."""
    match = next((item for item in _jobs() if item.name == job), None)
    if match is None:
        console.print(f"[red]Unknown job:[/red] {escape(job)}")
        raise typer.Exit(code=2)
    path = log_path(logs_dir(), match)
    if not path.exists():
        console.print(f"[dim]No log yet: {path}[/dim]")
        return
    tail = path.read_text(encoding="utf-8", errors="replace").splitlines()[-lines:]
    console.print(escape("\n".join(tail)))


@dataclass(frozen=True, slots=True)
class _InstallRoot:
    """The environment root the tasks run from and the plan the guard watches."""

    root: Path
    plan: Path


def _install_root() -> _InstallRoot:
    from desktop_manager import runtime

    if runtime.running_copy(__file__):
        root = runtime.current_link()
        return _InstallRoot(root, root / runtime.PLAN_RELATIVE)
    from desktop_manager.cli_machine import main_checkout_root, same_path
    from desktop_manager.plan import default_plan_path

    root = Path(__file__).resolve().parents[3]
    main_checkout = main_checkout_root(root)
    if main_checkout is not None and not same_path(main_checkout, root):
        console.print("[red]jobs install must run from the main checkout or a runtime copy, not a worktree.[/red]")
        console.print(f"[dim]Main checkout:[/dim] {main_checkout}")
        raise typer.Exit(code=1)
    return _InstallRoot(root, default_plan_path())


@jobs_app.command(name="install")
@requires(Platform.WINDOWS)
def install(
    start: bool = typer.Option(True, "--start/--no-start", help="Start the display guard now as well as at logon."),
) -> None:
    """Register the Logon, Tick and Display Guard tasks, and retire the separate tasks they replace.

    Run it from `desktop-manager runtime install`'s copy so the tasks follow
    `current` through later installs and rollbacks.
    """
    from desktop_manager.cli_machine import current_user_id
    from desktop_manager.display_guard import render_guard_task_xml

    target = _install_root()
    root, plan_path = target.root, target.plan
    pythonw = root / ".venv" / "Scripts" / "pythonw.exe"
    user_id = current_user_id()
    now = datetime.now().astimezone()
    try:
        for spec in (installer.LOGON, installer.TICK):
            installer.register(spec.name, installer.render_task_xml(spec, pythonw, user_id=user_id, start=now))
            console.print(f"[green]Registered[/green] {spec.name}")
        guard_xml = render_guard_task_xml(root, plan_path, user_id=user_id, task_name=installer.GUARD_TASK)
        installer.register(installer.GUARD_TASK, guard_xml)
        console.print(f"[green]Registered[/green] {installer.GUARD_TASK}")
        retired = [name for name in installer.RETIRED_TASKS if installer.delete(name)]
        if start:
            installer.start(installer.GUARD_TASK)
    except RuntimeError as exc:
        console.print(f"[red]{escape(str(exc))}[/red]")
        raise typer.Exit(code=1) from exc
    for name in retired:
        console.print(f"[dim]Retired[/dim] {name}")
    console.print(f"[dim]Runs from:[/dim] {pythonw}")
    console.print(f"[dim]Plan:[/dim] {plan_path}")


@jobs_app.command(name="uninstall")
@requires(Platform.WINDOWS)
def uninstall() -> None:
    """Remove the coordinator's scheduled tasks."""
    for name in (installer.LOGON_TASK, installer.TICK_TASK, installer.GUARD_TASK):
        console.print(f"{'[green]Removed[/green]' if installer.delete(name) else '[dim]Not registered[/dim]'} {name}")
