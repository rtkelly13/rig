"""`desktop-manager runtime`: install, inspect and roll back the copy the background tasks run from."""

from __future__ import annotations

from pathlib import Path

import typer
from rich.console import Console
from rich.markup import escape

from desktop_manager import rig_shim, runtime

runtime_app = typer.Typer(
    name="runtime",
    no_args_is_help=True,
    help="The pinned copy of this repo that the scheduled tasks run from.",
)
console = Console()


def _repo(explicit: Path | None) -> Path:
    if explicit is not None:
        return explicit
    from desktop_manager.chrome.install import find_checkout

    if not runtime.running_copy(__file__):
        checkout = find_checkout(Path(__file__).resolve().parent)
        if checkout is not None:
            return checkout
    recorded = runtime.read_state(runtime.runtime_root()).repo
    if recorded:
        return Path(recorded)
    console.print("[red]No repo to install from[/red]: pass --repo <shared-utilities checkout>.")
    raise typer.Exit(code=2)


@runtime_app.command(name="install")
def install(
    ref: str = typer.Option("origin/main", "--ref", help="Commit to install. origin/* refs are fetched first."),
    repo: Path | None = typer.Option(
        None, "--repo", help="Checkout to take the commit from. Default: this one, or the one last installed from."
    ),
    fetch: bool = typer.Option(True, "--fetch/--no-fetch", help="Fetch origin before resolving an origin/* ref."),
) -> None:
    """Build a copy of one commit, check it runs, then make it the one the scheduled tasks use.

    Built from the commit itself, never the working tree, so uncommitted edits and the
    checked-out branch have no effect. The copy it replaces is kept for `rollback`.
    """
    source = _repo(repo)
    try:
        result = runtime.install(source, ref, fetch=fetch, progress=lambda line: console.print(f"[dim]{line}[/dim]"))
    except runtime.InstallError as error:
        console.print(f"[red]Install failed:[/red] {escape(str(error))}")
        raise typer.Exit(code=1) from error
    link = runtime.current_link()
    if not result.changed:
        console.print(f"Already current: {result.sha[: runtime.SHORT_SHA]} ({escape(ref)}) at {escape(str(link))}")
    else:
        console.print(
            f"[green]Current:[/green] {result.sha[: runtime.SHORT_SHA]} ({escape(ref)}) at {escape(str(link))}"
        )
        if result.previous:
            console.print(f"[dim]Previous kept for rollback:[/dim] {result.previous}")
        for name in result.removed:
            console.print(f"[dim]Removed old copy:[/dim] {name}")
    _report_shim(rig_shim.install())


def _report_shim(outcome: rig_shim.Outcome) -> None:
    shim = escape(rig_shim.shim_path().as_posix())
    match outcome:
        case rig_shim.Outcome.WRITTEN:
            console.print(f"[green]rig on PATH:[/green] {shim} runs the current copy")
        case rig_shim.Outcome.UNCHANGED:
            console.print(f"[dim]rig on PATH: {shim}[/dim]")
        case rig_shim.Outcome.FOREIGN:
            console.print(f"[yellow]Left {shim} alone[/yellow]: rig did not write it, so `rig` may not be this copy.")


@runtime_app.command(name="rollback")
def rollback() -> None:
    """Point the scheduled tasks back at the previous copy (and keep the current one as the new previous)."""
    try:
        state = runtime.rollback()
    except runtime.InstallError as error:
        console.print(f"[red]Rollback failed:[/red] {escape(str(error))}")
        raise typer.Exit(code=1) from error
    console.print(f"[green]Current:[/green] {state.current} [dim](previous: {state.previous})[/dim]")


@runtime_app.command(name="status")
def status(
    ref: str = typer.Option("origin/main", "--ref", help="Ref to compare against, as of the repo's last fetch."),
) -> None:
    """The copy in use, the one kept for rollback, and how far the current one is behind `--ref`."""
    root = runtime.runtime_root()
    state = runtime.read_state(root)
    if not state.current:
        console.print(
            f"[yellow]Nothing installed[/yellow] in {escape(str(root))}: run `desktop-manager runtime install`."
        )
        raise typer.Exit(code=1)
    installed = runtime.read_installed(root / state.current)
    console.print(f"Current:  {state.current}  {escape(installed.ref or '?')}  {installed.installed_at or '?'}")
    if state.previous:
        previous = runtime.read_installed(root / state.previous)
        console.print(f"Previous: {state.previous}  {escape(previous.ref or '?')}  {previous.installed_at or '?'}")
    console.print(f"Path:     {escape(str(runtime.current_link(root)))}")
    if state.repo and installed.sha:
        behind = runtime.commits_behind(Path(state.repo), installed.sha, ref)
        if behind is not None:
            console.print(f"Behind {escape(ref)}: {behind} commit(s)")
