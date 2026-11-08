"""`estate history status|setup|port`: a clone of a repo restarted without history.

Mounted on the `estate` app by `cli.py`. The decisions are in `history`; this module
gathers, renders and, with `--apply`, acts. Nothing here pushes, opens a PR or resets
main: those are printed for a person to run.
"""

from __future__ import annotations

import pathlib

import typer
from rich.console import Console
from rich.table import Table

from rig import git_hooks, proc
from rig.estate import history
from rig.estate.checks import is_dirty

app = typer.Typer(add_completion=False, help="A clone of a restarted repo: the push guard and the old branches.")
console = Console()
err = Console(stderr=True)

_KIND_STYLE = {history.Kind.CURRENT: "green", history.Kind.RETIRED: "dim", history.Kind.TO_PORT: "yellow"}


def _restart_or_exit(here: pathlib.Path) -> history.Restart:
    restart = history.detect_restart(here)
    if restart is None:
        err.print(
            f"[yellow]{here}: origin/main does not begin at a restart.[/yellow] "
            "If origin was restarted since the last fetch, run `git fetch origin` (or `setup --apply`) first."
        )
        raise typer.Exit(1)
    return restart


def _describe(restart: history.Restart) -> None:
    console.print(
        f"[bold]{restart.slug}[/bold] restarted {restart.date or '(undated)'} at {restart.root[:12]}, "
        f"from {restart.archive_slug}@{restart.old_main}"
    )


def _show_setup(plan: history.SetupPlan, *, applied: bool = False) -> None:
    if not plan.steps and not plan.advice:
        console.print("[green]The clone is set up: archive remote, push guard and main all current.[/green]")
    for step in plan.steps:
        console.print(f"  {'done' if applied else 'to do'}: {step.description}")
    for line in plan.advice:
        console.print(f"  [yellow]by hand[/yellow]: {line}")


@app.command()
def status(
    path: pathlib.Path | None = typer.Argument(None, help="Any path in the clone; the current directory by default"),
    show_all: bool = typer.Option(False, "--all", help="List retired and current branches too, not just their count"),
    offline: bool = typer.Option(False, "--offline", help="Skip asking GitHub for the archive's PRs"),
) -> None:
    """Read-only: the restart, what setup would change, and every local branch's standing."""
    here = (path or pathlib.Path.cwd()).resolve()
    restart = _restart_or_exit(here)
    _describe(restart)
    _show_setup(history.plan_setup(restart, history.clone_facts(here, restart)))

    prs = history.ArchivePrs() if offline else history.archive_prs(restart)
    branches = history.branch_facts(here, restart, prs)
    counts = {kind: 0 for kind in history.Kind}
    table = Table(show_header=True, header_style="bold")
    for column in ("branch", "standing", "ahead of old main", "archive PR", "checked out in"):
        table.add_column(column)
    for branch in sorted(branches, key=lambda b: b.name):
        kind = history.classify(branch)
        counts[kind] += 1
        if kind is history.Kind.TO_PORT or show_all:
            pr = branch.archive_pr
            table.add_row(
                branch.name,
                f"[{_KIND_STYLE[kind]}]{kind}[/{_KIND_STYLE[kind]}]",
                str(branch.ahead_of_old_main) if not branch.on_new_history else "",
                f"#{pr.number} {pr.state.lower()}" if pr else "",
                str(branch.worktree or ""),
            )
    if table.row_count:
        console.print(table)
    console.print(", ".join(f"{counts[kind]} {kind}" for kind in history.Kind))
    if not offline and not prs.known:
        err.print("[yellow]Could not list the archive's PRs; branches were judged on git alone.[/yellow]")


@app.command()
def setup(
    path: pathlib.Path | None = typer.Argument(None, help="Any path in the clone; the current directory by default"),
    apply: bool = typer.Option(False, "--apply", help="Make the changes; without it, only show them"),
) -> None:
    """Ready a clone: archive remote, push guard config and hook, main's upstream. Dry run without --apply.

    Run it from the main checkout: the hook goes in the clone's shared hooks folder, so
    it covers every worktree. Resetting an old main is only ever advised, never done.
    """
    here = (path or pathlib.Path.cwd()).resolve()
    if apply and history.detect_restart(here) is None:
        console.print("fetching origin")
        _ = proc.git("fetch", "origin", cwd=here)
    restart = _restart_or_exit(here)
    _describe(restart)
    plan = history.plan_setup(restart, history.clone_facts(here, restart))
    if not apply:
        _show_setup(plan)
        if plan.steps:
            console.print("Dry run: rerun with --apply to make these changes.")
        return
    for step in plan.steps:
        if step.git:
            ran = proc.git(*step.git, cwd=here, timeout=None)
            if not ran.ok:
                err.print(f"[red]{step.description} failed:[/red] {ran.err.strip()}")
                raise typer.Exit(1)
        else:
            git_dir = history.common_dir(here)
            if git_dir is None:
                err.print(f"[red]{here} is not in a git clone[/red]")
                raise typer.Exit(1)
            _ = git_hooks.install_pre_push_hook(git_dir / "hooks")
    _show_setup(plan, applied=True)


@app.command()
def port(
    branch: str = typer.Argument(..., help="The local branch to port"),
    base: str | None = typer.Option(
        None, "--from", help="Where its own commits start: the parent branch when stacked (old main by default)"
    ),
    apply: bool = typer.Option(False, "--apply", help="Run the rebase; without it, only show it"),
    offline: bool = typer.Option(False, "--offline", help="Skip asking GitHub for the archive's PRs"),
) -> None:
    """Rebase an old branch's own commits onto the new main. Never pushes or opens a PR."""
    here = pathlib.Path.cwd().resolve()
    restart = _restart_or_exit(here)
    prs = history.ArchivePrs() if offline else history.archive_prs(restart)
    facts = next((b for b in history.branch_facts(here, restart, prs) if b.name == branch), None)
    if facts is None:
        err.print(f"[red]No local branch {branch}[/red]")
        raise typer.Exit(1)
    plan = history.plan_port(restart, facts, here=here, here_dirty=is_dirty(here), base=base)
    if plan.refused:
        err.print(f"[yellow]{plan.refused}[/yellow]")
        raise typer.Exit(1)
    console.print(f"git {' '.join(plan.rebase)}")
    if plan.switches_cwd:
        console.print(f"  (this checks {branch} out in {here})")
    if not apply:
        console.print("Dry run: rerun with --apply to rebase.")
        return
    ran = proc.git(*plan.rebase, cwd=here, timeout=None)
    if not ran.ok:
        err.print(f"[red]The rebase stopped:[/red] {ran.err.strip()}")
        err.print("Resolve and `git rebase --continue`, or `git rebase --abort` to leave the branch as it was.")
        raise typer.Exit(1)
    if not proc.git("merge-base", "--is-ancestor", restart.root, branch, cwd=here).ok:
        err.print(f"[red]{branch} still does not descend from {restart.root[:12]}; check it before pushing.[/red]")
        raise typer.Exit(1)
    console.print(f"[green]{branch} is on the new history.[/green] Push it when ready:")
    console.print(f"  git push -u origin {branch}")
    if facts.archive_pr:
        console.print(f"  and carry over anything from {facts.archive_pr.url}")
