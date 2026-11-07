"""`estate` — manage the code assets cloned on this machine.

Commands, ordered by the decision each one supports:

``audit``    the dashboard. One row per clone: what is at risk, what is
             reclaimable, how much sprawl.
``risk``     only what exists here and nowhere else. The precondition for
             deleting anything.
``reclaim``  the disk that regenerates from a command.
``trim``     the same, inside worktrees nobody has used for a week.
``tidy``     stale worktrees and finished branches.
``doctor``   one clone, every finding, each with its fix.
``sync``     `fetch --prune` across the estate, so `tidy` tells the truth about
             upstreams that are gone.
``prune-caches``  the tool caches that refill themselves (uv, pnpm, NuGet http,
             Claude temp, Docker build cache).
``decisions``     what only a human can decide: dubious ownership, unpushed or
             PR-less idle worktrees, directories a delete left behind.
``volume``   (Windows) where a whole drive's space went, from a WizTree MFT scan.
``shelve``   resolve a worktree `tidy` refuses: push a `TEMP-SHELVED:` snapshot to
             `shelved/<user>/<name>`, verify it on origin, remove the worktree.
             ``unshelve`` restores one; ``shelved`` lists and prunes them.

Read-only by default throughout. `reclaim`, `trim`, `tidy`, `prune-caches` and `shelve` take `--apply`;
`reclaim` and `tidy` refuse a clone holding uncommitted or unpushed work unless
`--force`, and `trim` refuses any directory git tracks or does not ignore.

Per § *Agent DX* in `docs/agents/agent-consistency.md`, `--json` writes the payload to
stdout with no Rich formatting; progress narration goes to stderr.
"""

from __future__ import annotations

import json
import pathlib
import sys
from dataclasses import dataclass

import typer
from rich.console import Console
from rich.table import Table

from rig import proc, roots, tracing
from rig.estate import apps as apps_mod
from rig.estate import apps_windows, discovery, locate, orca_cli, shelve_cli, tiers_cli, volume_report, wrapup_cli
from rig.estate import downloads as downloads_mod
from rig.estate import reclaim as reclaim_mod
from rig.estate import risk as risk_mod
from rig.estate import trim as trim_mod
from rig.estate.checks import inspect_repo
from rig.estate.cleanup import TIDY_CHECKS, Busy, Outcome, clean_repo
from rig.estate.reclaim import human

app = typer.Typer(add_completion=False, help="Manage the code assets cloned on this machine.")
console = Console()
err = Console(stderr=True)

_SEVERITY_STYLE = {"error": "bold red", "warn": "yellow", "info": "dim"}
_OUTCOME_STYLE = {
    "cleaned": "bold green",
    "would-clean": "cyan",
    "refused": "yellow",
    "failed": "bold red",
}


# ---------------------------------------------------------------------------


@app.command()
def audit(
    root: str | None = typer.Option(None, "--root", help="Code root to scan (default: the personal repos dir)"),
    refresh: bool = typer.Option(False, "--refresh", help="Rescan rather than using the cached clone list"),
    json_output: bool = typer.Option(False, "--json", help="Emit the inventory for a machine"),
    per_branch: bool = typer.Option(False, "--branches", help="Also name the branches carrying unpushed work"),
) -> None:
    """The dashboard: every clone, what is at risk, and what is reclaimable.

    Sorted by risk, because that is the column you act on first. Reclaimable
    disk is worth nothing next to work that exists nowhere else, and the two
    live in the same directory tree.
    """
    scan_root = locate.code_root(root)
    clones = locate.clones(scan_root, refresh=refresh, quiet=json_output, err=err)

    exposures = [risk_mod.inspect_exposure(c, per_branch=per_branch) for c in clones]
    reclaims = [reclaim_mod.scan_repo(c) for c in clones]
    by_repo = {r.repo: r for r in reclaims}
    duplicates = risk_mod.find_duplicates(exposures)

    if json_output:
        typer.echo(
            json.dumps(
                {
                    "root": str(scan_root),
                    "clones": [
                        {
                            "repo": str(e.repo),
                            "local_only_commits": e.local_only_commits,
                            "unpushed_branches": list(e.unpushed_branches),
                            "dirty_files": e.dirty_files,
                            "stashes": e.stashes,
                            "has_remote": e.has_remote,
                            "reclaimable_bytes": by_repo[e.repo].total,
                            "reclaimable_by_category": by_repo[e.repo].by_category(),
                        }
                        for e in exposures
                    ],
                    "duplicate_clones": [
                        {"origin": d.origin, "repos": [str(p) for p in d.repos], "exposure": d.exposure}
                        for d in duplicates
                    ],
                },
                indent=2,
            )
        )
        return

    table = Table(title=f"Estate — {len(clones)} clones under {scan_root}", title_style="bold")
    table.add_column("clone")
    table.add_column("local-only", justify="right")
    table.add_column("dirty", justify="right")
    table.add_column("stash", justify="right")
    table.add_column("reclaimable", justify="right")
    table.add_column("largest", style="dim")

    for exposure in sorted(exposures, key=lambda e: -e.score):
        entry = by_repo[exposure.repo]
        largest = entry.entries[0] if entry.entries else None
        table.add_row(
            exposure.repo.name,
            f"[bold red]{exposure.local_only_commits}[/]" if exposure.local_only_commits else "—",
            f"[yellow]{exposure.dirty_files}[/]" if exposure.dirty_files else "—",
            f"[yellow]{exposure.stashes}[/]" if exposure.stashes else "—",
            human(entry.total) if entry.total else "—",
            f"{largest.category} {human(largest.bytes_used)}" if largest else "",
        )
    console.print(table)

    at_risk = sum(e.local_only_commits for e in exposures)
    console.print(
        f"\n[bold]{at_risk}[/bold] commits exist on no remote · "
        f"[bold]{human(sum(r.total for r in reclaims))}[/bold] regenerates from a command"
    )
    if duplicates:
        console.print("\n[yellow]Duplicate clones — estate totals double-count these:[/yellow]")
        for dup in duplicates:
            names = ", ".join(f"{k} ({v} local-only)" for k, v in dup.exposure.items())
            console.print(f"  {dup.origin} → {names}")
    console.print("\n[dim]`estate risk` before deleting anything · `estate reclaim` for the disk[/dim]")


@app.command()
def risk(
    root: str | None = typer.Option(None, "--root", help="Code root to scan"),
    refresh: bool = typer.Option(False, "--refresh", help="Rescan rather than using the cached list"),
    json_output: bool = typer.Option(False, "--json", help="Emit for a machine"),
) -> None:
    """Only what exists on this machine and nowhere else.

    Uncommitted changes, stash entries, and commits reachable from no remote.
    Run this before `reclaim --apply` or `tidy --apply`: everything else those
    commands touch comes back from a command, and nothing here does.
    """
    scan_root = locate.code_root(root)
    exposures = [
        risk_mod.inspect_exposure(c, per_branch=True)
        for c in locate.clones(scan_root, refresh=refresh, quiet=json_output, err=err)
    ]
    exposed = [e for e in exposures if e.at_risk]

    if json_output:
        typer.echo(
            json.dumps(
                [
                    {
                        "repo": str(e.repo),
                        "local_only_commits": e.local_only_commits,
                        "unpushed_branches": list(e.unpushed_branches),
                        "dirty_files": e.dirty_files,
                        "stashes": e.stashes,
                        "has_remote": e.has_remote,
                    }
                    for e in sorted(exposed, key=lambda e: -e.score)
                ],
                indent=2,
            )
        )
        return

    if not exposed:
        console.print("[bold green]✓ Nothing at risk[/bold green] — every commit is on a remote, every tree clean.")
        return

    for exposure in sorted(exposed, key=lambda e: -e.score):
        console.print(f"\n[bold]{exposure.repo.name}[/bold]")
        if not exposure.has_remote:
            console.print("  [bold red]no remote at all[/bold red] — every commit here is local-only")
        if exposure.local_only_commits:
            console.print(f"  [bold red]{exposure.local_only_commits}[/bold red] commit(s) on no remote")
            shown = exposure.unpushed_branches[:8]
            for branch in shown:
                console.print(f"      [dim]cd {exposure.repo} && git push -u origin {branch}[/dim]")
            if len(exposure.unpushed_branches) > len(shown):
                console.print(f"      [dim]… and {len(exposure.unpushed_branches) - len(shown)} more branches[/dim]")
        if exposure.dirty_files:
            console.print(f"  [yellow]{exposure.dirty_files}[/yellow] uncommitted file(s)")
        if exposure.stashes:
            console.print(
                f"  [yellow]{exposure.stashes}[/yellow] stash entr{'y' if exposure.stashes == 1 else 'ies'}"
                " [dim](one stack shared by every worktree of this repo)[/dim]"
            )

    total = sum(e.local_only_commits for e in exposed)
    console.print(f"\n[bold red]{total}[/bold red] commits across {len(exposed)} clones exist only here.")


@app.command()
def reclaim(
    root: str | None = typer.Option(None, "--root", help="Code root to scan"),
    apply: bool = typer.Option(False, "--apply", help="Actually delete. Off by default."),
    force: bool = typer.Option(False, "--force", help="Proceed on clones holding uncommitted or unpushed work"),
    refresh: bool = typer.Option(False, "--refresh", help="Rescan rather than using the cached list"),
    min_size: str = typer.Option("100MB", "--min", help="Ignore anything smaller (e.g. 500MB, 2GB)"),
) -> None:
    """Disk that regenerates from a command.

    Safety is per entry, not per clone, and that distinction was worth getting
    right: a first cut refused any clone holding local-only commits, which on
    this estate meant refusing 100% of the 14 GB — every clone with build output
    also has unpushed work. But a *commit* is not endangered by deleting
    `node_modules`. Only live state inside the path being removed is, so the
    refusals are:

    - a `temp/` worktree that is dirty, or holds commits on no remote
    - anything resolving outside the clone

    Uncommitted files elsewhere in the clone are reported, not blocking. `--force`
    is therefore only needed for a dirty worktree you are certain about.
    """
    scan_root = locate.code_root(root)
    floor = _parse_size(min_size)
    clones = locate.clones(scan_root, refresh=refresh, quiet=False, err=err)

    table = Table(title="Reclaimable" + ("" if apply else " (dry run — pass --apply)"), title_style="bold")
    table.add_column("clone")
    table.add_column("path", style="dim")
    table.add_column("size", justify="right")
    table.add_column("comes back from", style="dim")
    table.add_column("", justify="right")

    freed = 0
    pending = 0
    noted: list[str] = []
    for clone in clones:
        entries = [e for e in reclaim_mod.scan_repo(clone, floor=floor).entries if e.bytes_used >= floor]
        if not entries:
            continue
        exposure = risk_mod.inspect_exposure(clone, per_branch=False)
        if exposure.at_risk:
            noted.append(
                f"{clone.name} — {exposure.local_only_commits} local-only commit(s), "
                f"{exposure.dirty_files} uncommitted file(s)"
            )

        for entry in entries:
            outcome = _reclaim_one(clone, entry, apply=apply, force=force)
            status, style = outcome.status, outcome.style
            if status == "removed":
                freed += entry.bytes_used
            elif status == "would remove":
                pending += entry.bytes_used
            table.add_row(
                clone.name,
                locate.short(entry.path, clone),
                human(entry.bytes_used),
                entry.regenerate,
                f"[{style}]{status}[/]",
            )

    console.print(table)
    if apply:
        console.print(f"\n[bold]Freed {human(freed)}[/bold]")
    else:
        console.print(f"\n[bold]Would free {human(pending)}[/bold] — pass [bold]--apply[/bold] to do it")
    if noted:
        console.print(
            "\n[dim]These clones hold work that exists only here. It is not endangered by the above —"
            " a commit survives a deleted node_modules — but `estate risk` is worth a look:[/dim]"
        )
        for line in noted:
            console.print(f"  [dim]{line}[/dim]")


@dataclass(frozen=True, slots=True)
class _ReclaimOutcome:
    """What happened to one reclaimable entry, and the rich style to show it in."""

    status: str
    style: str


def _reclaim_one(
    clone: pathlib.Path, entry: reclaim_mod.Reclaimable, *, apply: bool, force: bool = False
) -> _ReclaimOutcome:
    """Remove one entry, or say what would happen. Returns the status and its rich style.

    A worktree is the only entry kind that can hold live state, so it is the only
    one with refusals — and `force` overrides them, because occasionally you do
    know that the dirty worktree is scrap.
    """
    if entry.is_worktree:
        from rig.estate.checks import is_dirty, orphaned_count

        if not force and is_dirty(entry.path):
            return _ReclaimOutcome("dirty worktree", "yellow")
        # commits on no remote, not "ahead of upstream" — see `orphaned_count`
        if not force and orphaned_count(entry.path):
            return _ReclaimOutcome("only copy", "yellow")
        if not apply:
            return _ReclaimOutcome("would remove", "cyan")
        done = proc.git(*trim_mod.LONG_PATHS, "worktree", "remove", str(entry.path), cwd=clone)
        return _ReclaimOutcome("removed", "bold green") if done.ok else _ReclaimOutcome("failed", "bold red")

    if clone.resolve() not in entry.path.resolve().parents:
        return _ReclaimOutcome("outside clone", "bold red")
    if not apply:
        return _ReclaimOutcome("would remove", "cyan")
    try:
        trim_mod.remove_tree(entry.path)
    except OSError:
        return _ReclaimOutcome("failed", "bold red")
    return _ReclaimOutcome("removed", "bold green")


def _parse_size(text: str) -> int:
    """Parse `500MB` / `2GB` / `1024`. Falls back to 0 on nonsense."""
    raw = text.strip().upper()
    for suffix, factor in (("TB", 1024**4), ("GB", 1024**3), ("MB", 1024**2), ("KB", 1024), ("B", 1)):
        if raw.endswith(suffix):
            head = raw[: -len(suffix)].strip()
            try:
                return int(float(head) * factor)
            except ValueError:
                return 0
    return int(raw) if raw.isdigit() else 0


@app.command()
def trim(
    root: str | None = typer.Option(None, "--root", help="Code root to scan"),
    apply: bool = typer.Option(False, "--apply", help="Actually delete. Off by default."),
    refresh: bool = typer.Option(False, "--refresh", help="Rescan rather than using the cached list"),
    idle_days: int = typer.Option(trim_mod.DEFAULT_IDLE_DAYS, "--idle-days", help="Days since the worktree was used"),
    min_size: str = typer.Option("10MB", "--min", help="Ignore anything freeing less (e.g. 100MB)"),
) -> None:
    """Regenerable output inside worktrees idle `--idle-days` or more.

    The worktree, its branch and its `temp/` stay; `node_modules`, venvs and
    build output go. Locked worktrees are skipped. Sizes are freeable bytes:
    a pnpm or uv tree hardlinked from its store frees far less than it shows.
    """
    clones = locate.clones(locate.code_root(root), refresh=refresh, quiet=False, err=err)
    floor = _parse_size(min_size)

    table = Table(title="Idle worktrees" + ("" if apply else " (dry run — pass --apply)"), title_style="bold")
    table.add_column("worktree")
    table.add_column("path", style="dim")
    table.add_column("frees", justify="right")
    table.add_column("", justify="right")

    freed = 0
    for clone in clones:
        for item in trim_mod.scan(clone, idle_days=idle_days, floor=floor):
            if item.refused:
                status, style = f"refused: {item.refused}", "yellow"
            elif not apply:
                status, style = "would remove", "cyan"
            else:
                status = trim_mod.remove(item)
                style = "bold green" if status == "removed" else "bold red"
            if status in ("removed", "would remove"):
                freed += item.size.freeable
            table.add_row(
                item.worktree.name,
                locate.short(item.path, item.worktree),
                human(item.size.freeable),
                f"[{style}]{status}[/]",
            )

    console.print(table)
    console.print(f"\n[bold]{'Freed' if apply else 'Would free'} {human(freed)}[/bold]")


@app.command()
def tidy(
    root: str | None = typer.Option(None, "--root", help="Code root to scan"),
    repos: list[str] | None = typer.Option(
        None,
        "--repo",
        help="Only this clone: a path to a checkout, or a clone's folder name under --root. Repeatable.",
    ),
    every: bool = typer.Option(False, "--all", help="Every clone under --root. Required when --repo is not given."),
    apply: bool = typer.Option(False, "--apply", help="Actually delete. Off by default."),
    force: bool = typer.Option(
        False, "--force", help="Proceed on uncommitted or unpushed work, or when lsof cannot say what is in use"
    ),
    refresh: bool = typer.Option(False, "--refresh", help="Rescan rather than using the cached list"),
    merged: bool = typer.Option(
        False, "--merged", help="Ask GitHub which head refs have a merged PR (one call per clone)"
    ),
    only_checks: list[str] | None = typer.Option(
        None,
        "--check",
        help=f"Act only on this finding check. Repeatable. One of: {', '.join(TIDY_CHECKS)}.",
    ),
) -> None:
    """Stale worktrees and finished branches, across the estate.

    Squash merges are why `--merged` exists. Under a squash policy a branch's
    commits never become ancestors of `main`, and deleting the remote branch
    afterwards strands the originals on no remote *forever* — so the local
    "nothing is lost" check refuses every branch that actually landed. Measured
    on `design-system`: 12 of 12 such branches refused, all 12 merged.

    `--merged` asks GitHub for the merged head refs, one call per clone, and a
    merged PR is what authorises deletion in that case. Without it this stays
    entirely local and says so in the refusal.

    The scope is always explicit: `--repo` names the clones (tidying after one
    merge costs one clone's checks), `--all` sweeps every clone under `--root`.
    Neither, or both, is an error, so a bare `tidy` never starts an estate-wide
    run nobody asked for. `--check` narrows it further, to one kind of finding.

    A directory some process is working in (a shell, an agent, an Orca terminal)
    is never removed, whatever the flags: one `lsof` snapshot per run says which.
    Where that cannot be read, removals are refused unless `--force`.
    """
    if bool(repos) == every:
        err.print("[bold red]Choose a scope: --repo <path|name> (repeatable), or --all for every clone.[/bold red]")
        raise typer.Exit(code=2)
    unknown = sorted(set(only_checks or ()) - set(TIDY_CHECKS))
    if unknown:
        err.print(f"[bold red]Unknown --check {', '.join(unknown)}. Choose from: {', '.join(TIDY_CHECKS)}.[/bold red]")
        raise typer.Exit(code=2)
    scan_root = locate.code_root(root)
    if repos:
        clones = locate.selected_clones(scan_root, repos, refresh=refresh, quiet=False, err=err)
    else:
        clones = locate.clones(scan_root, refresh=refresh, quiet=False, err=err)

    table = Table(title="Tidy" + ("" if apply else " (dry run — pass --apply)"), title_style="bold")
    table.add_column("clone")
    table.add_column("check", style="cyan")
    table.add_column("subject")
    table.add_column("outcome", justify="right")
    table.add_column("detail", style="dim")

    in_use = Busy.snapshot()
    if in_use.dirs is None:
        err.print("[yellow]Cannot read process working directories (lsof); removals need --force.[/yellow]")
    counts: dict[str, int] = {}
    for clone in clones:
        with tracing.span("estate.tidy.clone", scope="rig.estate", **{"estate.clone": str(clone)}) as clone_span:
            heads: dict[str, int] | None = None
            if merged:
                from rig.estate.prs import merged_heads

                heads = merged_heads(clone)
            report = inspect_repo(clone, merged_heads=heads)
            clone_span.set(**{"estate.actionable": len(report.actionable)})
            actions = (
                clean_repo(
                    report, apply=apply, force=force, merged_heads=heads, in_use=in_use, checks=only_checks or None
                )
                if report.actionable
                else []
            )
            for outcome in {a.outcome for a in actions}:
                clone_span.set(**{f"estate.outcome.{outcome}": sum(a.outcome == outcome for a in actions)})
        for action in actions:
            counts[action.outcome] = counts.get(action.outcome, 0) + 1
            subject = locate.short(action.subject, clone)
            table.add_row(
                clone.name,
                action.check,
                subject,
                f"[{_OUTCOME_STYLE.get(action.outcome, 'white')}]{action.outcome}[/]",
                action.detail,
            )

    console.print(table)
    if counts:
        console.print("\n" + " · ".join(f"[bold]{v}[/bold] {k}" for k, v in sorted(counts.items())))
    else:
        console.print("\n[bold green]✓ Nothing to tidy.[/bold green]")
    if counts.get(Outcome.REFUSED):
        console.print("[dim]Refusals are deliberate — see `estate risk`, or pass --force.[/dim]")


@app.command()
def doctor(
    path: str | None = typer.Argument(None, help="Clone to inspect (default: the one containing the cwd)"),
    json_output: bool = typer.Option(False, "--json", help="Emit findings for a machine"),
    merged: bool = typer.Option(False, "--merged", help="Ask GitHub which head refs have a merged PR"),
) -> None:
    """One clone, every finding, each with the command that resolves it."""
    clone = locate.one_clone(path)
    if not (clone / ".git").exists():
        err.print(f"[bold red]{clone} is not a git checkout.[/bold red]")
        raise typer.Exit(code=2)

    heads = None
    if merged:
        from rig.estate.prs import merged_heads

        heads = merged_heads(clone)
    report = inspect_repo(clone, merged_heads=heads)
    exposure = risk_mod.inspect_exposure(clone, per_branch=True)
    disk = reclaim_mod.scan_repo(clone)

    if json_output:
        typer.echo(
            json.dumps(
                {
                    "repo": str(clone),
                    "findings": [
                        {
                            "check": f.check,
                            "severity": str(f.severity),
                            "subject": f.subject,
                            "message": f.message,
                            "fix": f.fix,
                            "actionable": f.actionable,
                        }
                        for f in report.findings
                    ],
                    "local_only_commits": exposure.local_only_commits,
                    "unpushed_branches": list(exposure.unpushed_branches),
                    "dirty_files": exposure.dirty_files,
                    "stashes": exposure.stashes,
                    "reclaimable_bytes": disk.total,
                    "reclaimable_by_category": disk.by_category(),
                },
                indent=2,
            )
        )
        return

    console.print(f"\n[bold]{clone.name}[/bold]  [dim]{clone}[/dim]")
    console.print(
        f"  {report.worktree_count} worktree(s) · {report.branch_count} local branch(es) · "
        f"{exposure.local_only_commits} local-only commit(s) · {human(disk.total)} reclaimable"
    )

    if not report.findings:
        console.print("\n[bold green]✓ Clean.[/bold green]")
        return

    table = Table(show_header=True, header_style="dim")
    table.add_column("check", style="cyan", no_wrap=True)
    table.add_column("subject", no_wrap=True)
    table.add_column("finding")
    for finding in sorted(report.findings, key=lambda f: ["error", "warn", "info"].index(str(f.severity))):
        subject = locate.short(finding.subject, clone)
        body = finding.message + (f"\n[dim]{finding.fix}[/dim]" if finding.fix else "")
        table.add_row(f"[{_SEVERITY_STYLE.get(str(finding.severity), 'white')}]{finding.check}[/]", subject, body)
    console.print(table)

    if report.blocked:
        console.print("\n[yellow]Errors above are live work — `tidy` will refuse this clone until they clear.[/yellow]")


@app.command()
def sync(
    root: str | None = typer.Option(None, "--root", help="Code root to scan"),
    refresh: bool = typer.Option(False, "--refresh", help="Rescan rather than using the cached list"),
) -> None:
    """`git fetch --prune` across the estate.

    A precondition for `tidy` being truthful: an upstream is only reported
    `gone` after a fetch has pruned the ref that no longer exists.
    """
    clones = locate.clones(locate.code_root(root), refresh=refresh, quiet=False, err=err)
    failed: list[str] = []
    with console.status("fetching…") as status:
        for clone in clones:
            status.update(f"fetching {clone.name}")
            done = proc.git("fetch", "--prune", "--quiet", cwd=clone, timeout=120)
            if not done.ok:
                failed.append(f"{clone.name}: {done.err.strip().splitlines()[-1:] or ['failed']}")
    console.print(f"[bold green]✓ Fetched {len(clones) - len(failed)} of {len(clones)} clones.[/bold green]")
    for line in failed:
        console.print(f"  [yellow]{line}[/yellow]")


@app.command(name="clones")
def clones_command(
    root: str | None = typer.Option(None, "--root", help="Code root to scan"),
    refresh: bool = typer.Option(False, "--refresh", help="Rescan now and rewrite the cache"),
) -> None:
    """Show the cached clone list, and when it was last scanned."""
    scan_root = locate.code_root(root)
    discovered = discovery.repos(scan_root, refresh=refresh)
    found, cached = discovered.paths, discovered.from_cache
    age = discovery.cache_age(scan_root)
    console.print(f"[bold]{len(found)}[/bold] clones under {scan_root}")
    provenance = f"cached, scanned {locate.ago(age)}" if cached else "freshly scanned"
    console.print(f"[dim]{provenance} · {discovery.CACHE_FILE}[/dim]\n")
    for clone in found:
        console.print(f"  {clone}")


@app.command(name="apps")
def apps_command(
    min_size: str = typer.Option("100MB", "--min", help="Ignore apps smaller than this"),
    json_output: bool = typer.Option(False, "--json", help="Emit for a machine"),
) -> None:
    """Audit installed desktop applications by size, version, and last-modified date."""
    floor = _parse_size(min_size)
    if sys.platform == "win32":
        items = apps_windows.scan_applications(min_size=floor)
    else:
        items = apps_mod.scan_applications(min_size=floor)

    if json_output:
        typer.echo(
            json.dumps(
                [
                    {
                        "name": a.name,
                        "path": str(a.path),
                        "bytes": a.bytes_used,
                        "version": a.version,
                        "modified": a.modified.isoformat(),
                        "accessed": a.accessed.isoformat(),
                        "is_dormant": a.is_dormant,
                    }
                    for a in items
                ],
                indent=2,
            )
        )
        return

    table = Table(title="Installed Applications", title_style="bold")
    table.add_column("Application")
    table.add_column("Size", justify="right")
    table.add_column("Version", style="dim")
    table.add_column("Last Modified", style="dim")
    table.add_column("Status", justify="right")

    dormant_bytes = 0
    for a in items:
        status = "[yellow]dormant[/yellow]" if a.is_dormant else "[green]active[/green]"
        if a.is_dormant:
            dormant_bytes += a.bytes_used
        table.add_row(
            a.name,
            human(a.bytes_used),
            a.version,
            a.modified.strftime("%Y-%m-%d"),
            status,
        )

    console.print(table)
    total_bytes = sum(a.bytes_used for a in items)
    console.print(
        f"\n[bold]{human(total_bytes)}[/bold] across {len(items)} applications · "
        f"[yellow]{human(dormant_bytes)}[/yellow] in dormant apps (>180 days untouched)"
    )


@app.command(name="downloads")
def downloads_command(
    min_size: str = typer.Option("20MB", "--min", help="Ignore files smaller than this"),
    json_output: bool = typer.Option(False, "--json", help="Emit for a machine"),
) -> None:
    """Audit ~/Downloads directory, categorizing large files and checking for Google Drive duplicates."""
    floor = _parse_size(min_size)
    items = downloads_mod.scan_downloads(min_size=floor)

    if json_output:
        typer.echo(
            json.dumps(
                [
                    {
                        "name": item.name,
                        "path": str(item.path),
                        "bytes": item.bytes_used,
                        "category": str(item.category),
                        "is_dir": item.is_dir,
                        "cloud_duplicate": str(item.cloud_duplicate_path) if item.cloud_duplicate_path else None,
                    }
                    for item in items
                ],
                indent=2,
            )
        )
        return

    table = Table(title="Downloads Triage", title_style="bold")
    table.add_column("Item")
    table.add_column("Category", style="dim")
    table.add_column("Size", justify="right")
    table.add_column("Cloud Backup Status", justify="right")

    dupe_bytes = 0
    for item in items:
        if item.has_cloud_duplicate and item.cloud_duplicate_path:
            dupe_bytes += item.bytes_used
            status = f"[bold green]duplicate in Drive[/bold green] ({item.cloud_duplicate_path.name})"
        else:
            status = "[dim]unique local file[/dim]"
        table.add_row(
            item.name,
            str(item.category),
            human(item.bytes_used),
            status,
        )

    console.print(table)
    total_bytes = sum(item.bytes_used for item in items)
    console.print(
        f"\n[bold]{human(total_bytes)}[/bold] in large downloads · "
        f"[bold green]{human(dupe_bytes)}[/bold green] already backed up to Google Drive"
    )


_ = app.command(name="prune-caches")(tiers_cli.prune_caches)
_ = app.command(name="decisions")(tiers_cli.decisions)
_ = app.command(name="orca")(orca_cli.orca_layout)
app.add_typer(wrapup_cli.wrapup_app, name="wrapup")


@app.command()
def volume(
    drive: str = typer.Option("C", "--drive", help="Drive letter to scan"),
    csv: str | None = typer.Option(None, "--csv", help="Reuse an existing WizTree folder export instead of scanning"),
    keep_csv: bool = typer.Option(False, "--keep-csv", help="Keep the export (it is ~400 MB) for a later --csv"),
    code_root: str | None = typer.Option(None, "--code-root", help="Clone root for the dev-artefact partition"),
    min_size: str = typer.Option("5GB", "--min", help="Smallest folder shown in the drill-down"),
    depth: int = typer.Option(4, "--depth", help="Drill-down depth below the drive root"),
    json_output: bool = typer.Option(False, "--json", help="Emit for a machine"),
) -> None:
    """(Windows) Where a whole drive's space went: drill-down, dev-artefact partition, known hogs."""
    request = volume_report.VolumeRequest(
        drive=drive,
        csv=pathlib.Path(csv) if csv else None,
        keep_csv=keep_csv,
        code_root=pathlib.Path(code_root) if code_root else roots.code_root(),
        min_bytes=_parse_size(min_size),
        min_label=min_size,
        depth=depth,
    )
    volume_report.run(request, json_output=json_output, console=console, err=err)


app.add_typer(shelve_cli.app)


def main() -> None:
    """Entry point for `estate`: each run is one trace in `temp/traces/sdk/estate/`.

    A root span named after the subcommand parents every clone scan and every git and gh
    call beneath it. Spans also flush every 30s, so a long sweep that is killed still says
    where its time went.
    """
    tracing.init_tracing("estate")
    tracing.flush_periodically(30)
    command = next((arg for arg in sys.argv[1:] if not arg.startswith("-")), "")
    with tracing.span(f"estate {command}".rstrip(), scope="rig.estate.cli", **{"cli.argv": sys.argv[1:]}):
        app()


if __name__ == "__main__":
    main()
