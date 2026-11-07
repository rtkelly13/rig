"""`estate shelve`, `estate unshelve` and `estate shelved`.

Merged into the `estate` app by `cli.py`. The work is in `shelve` (planning and
acting) and `shelf_store` (what is on origin); this module parses options and
renders tables.
"""

from __future__ import annotations

import datetime as dt
import json
import pathlib
from dataclasses import dataclass, field

import typer
from rich.console import Console
from rich.table import Table

from rig import proc
from rig.estate import locate, shelf_store
from rig.estate import shelve as shelve_mod
from rig.estate.checks import Worktree, list_worktrees
from rig.estate.prs import pr_heads
from rig.typed_json import JsonValue

app = typer.Typer(add_completion=False)
console = Console()
err = Console(stderr=True)

_SHELVE_STYLE = {
    shelve_mod.REMOVE: "green",
    shelve_mod.PUSH: "cyan",
    shelve_mod.SNAPSHOT: "magenta",
    "shelved": "bold green",
    "removed": "bold green",
    "needs-decision": "yellow",
    "failed": "bold red",
}


def _parse_duration(text: str) -> dt.timedelta:
    """`2h`, `30m`, `1d`, or `0` to disable the live-session guard."""
    text = text.strip().lower()
    if text in {"", "0"}:
        return dt.timedelta(0)
    units = {"m": "minutes", "h": "hours", "d": "days"}
    if text[-1] not in units or not text[:-1].replace(".", "", 1).isdigit():
        raise typer.BadParameter(f"expected e.g. 30m, 2h, 1d — got {text!r}")
    return dt.timedelta(**{units[text[-1]]: float(text[:-1])})


def _clone_of(path: pathlib.Path) -> pathlib.Path | None:
    """The clone a worktree belongs to, via its common git dir."""
    out = proc.git_out("rev-parse", "--path-format=absolute", "--git-common-dir", cwd=path)
    if not out:
        return None
    common = pathlib.Path(out)
    return common.parent if common.name == ".git" else common


def _shelvable(tree: Worktree) -> bool:
    return not tree.is_main and not tree.bare and not tree.prunable and tree.path.exists()


@dataclass(slots=True, frozen=True)
class _Target:
    """One clone and the worktrees in it to consider."""

    clone: pathlib.Path
    trees: list[Worktree] = field(default_factory=list[Worktree])


@dataclass(slots=True, frozen=True)
class _Scope:
    """Which worktrees `estate shelve` was pointed at."""

    paths: list[str]
    repo: str | None
    everything: bool
    root: str | None
    refresh: bool


def _shelve_targets(scope: _Scope) -> list[_Target]:
    """Group the worktrees to consider by clone. A main checkout is never a target."""
    if scope.everything or scope.repo:
        clones = (
            locate.clones(locate.code_root(scope.root), refresh=scope.refresh, quiet=False, err=err)
            if scope.everything
            else [pathlib.Path(scope.repo or ".").expanduser().resolve()]
        )
        targets = [_Target(c, [t for t in list_worktrees(c) if _shelvable(t)]) for c in clones]
        return [t for t in targets if t.trees]

    wanted = [pathlib.Path(p).expanduser().resolve() for p in scope.paths] or [pathlib.Path.cwd().resolve()]
    grouped: dict[pathlib.Path, list[Worktree]] = {}
    for want in wanted:
        clone = _clone_of(want)
        if clone is None:
            raise typer.BadParameter(f"{want} is not inside a git worktree")
        match = sorted(
            (t for t in list_worktrees(clone) if want.is_relative_to(t.path.resolve())),
            key=lambda t: len(t.path.parts),
            reverse=True,
        )
        if not match or not _shelvable(match[0]):
            raise typer.BadParameter(f"{want} is not a linked worktree (a clone's main checkout is never shelved)")
        grouped.setdefault(clone, []).append(match[0])
    return [_Target(clone, trees) for clone, trees in grouped.items()]


def _pr_map(clone: pathlib.Path) -> dict[str, shelve_mod.PrRef]:
    # Wider than tidy's window: a shelf's trailer should name its PR however old it is.
    return {
        head: shelve_mod.PrRef(number=pull.number, state=pull.state)
        for head, pull in pr_heads(clone, limit=1000).items()
    }


@app.command()
def shelve(
    worktrees: list[str] | None = typer.Argument(None, help="Worktree paths (default: the one containing the cwd)"),
    repo: str | None = typer.Option(None, "--repo", help="Every linked worktree of this clone"),
    everything: bool = typer.Option(False, "--all", help="Every linked worktree across the estate"),
    root: str | None = typer.Option(None, "--root", help="Code root for --all"),
    refresh: bool = typer.Option(False, "--refresh", help="Rescan rather than using the cached list"),
    apply: bool = typer.Option(False, "--apply", help="Push and remove. Off by default."),
    json_output: bool = typer.Option(False, "--json", help="Emit the plans (or results) for a machine"),
    idle: str = typer.Option("2h", "--idle", help="Refuse trees touched more recently than this (0 disables)"),
    include_ignored: bool = typer.Option(
        False, "--include-ignored", help="Carry non-regenerable ignored files (an .env) in the snapshot"
    ),
    discard_ignored: bool = typer.Option(
        False, "--discard-ignored", help="Accept losing non-regenerable ignored files"
    ),
    to_upstream: bool = typer.Option(
        False, "--to-upstream", help="Also push local commits to the branch's own upstream (fast-forward only)"
    ),
) -> None:
    """Resolve worktrees `tidy` refuses: push what exists only here, then remove them.

    Uncommitted work becomes one `TEMP-SHELVED:` commit on top of HEAD, made
    from a throwaway index so the branch and its PR never move, and is pushed
    to `shelved/<user>/<name>`. The worktree is removed only after origin is
    shown to hold it. `estate unshelve <name>` brings it back.

    Exit 0 when everything resolved (or would), 2 when a plan needs a decision,
    1 on a failure.
    """
    if include_ignored and discard_ignored:
        raise typer.BadParameter("--include-ignored and --discard-ignored are opposites")
    options = shelve_mod.Options(
        idle=_parse_duration(idle),
        include_ignored=include_ignored,
        discard_ignored=discard_ignored,
        to_upstream=to_upstream,
    )
    scope = _Scope(paths=worktrees or [], repo=repo, everything=everything, root=root, refresh=refresh)

    plans: list[shelve_mod.Plan] = []
    for target in _shelve_targets(scope):
        clone = target.clone
        err.print(f"[dim]planning {clone.name}: {len(target.trees)} worktree(s)[/dim]")
        user = shelve_mod.shelf_user(clone)
        shelves = shelf_store.remote_shelves(clone)
        prs = _pr_map(clone) if any(t.branch for t in target.trees) else {}
        plans += [
            shelve_mod.plan_worktree(clone, t, user=user, shelves=shelves, prs=prs, options=options)
            for t in target.trees
        ]

    results = [shelve_mod.apply_plan(p) for p in plans] if apply else []
    needs = sum(1 for p in plans if p.decision_needed)
    failed = sum(1 for r in results if r.outcome == "failed")

    if json_output:
        payload = [r.to_json() for r in results] if apply else [p.to_json() for p in plans]
        typer.echo(json.dumps(payload, indent=2))
    else:
        _render_shelve(plans, results, apply=apply)
    raise typer.Exit(1 if failed else 2 if needs else 0)


def _render_shelve(plans: list[shelve_mod.Plan], results: list[shelve_mod.Result], *, apply: bool) -> None:
    table = Table(title="Shelve" + ("" if apply else " (dry run — pass --apply)"), title_style="bold")
    table.add_column("clone")
    table.add_column("worktree")
    table.add_column("branch / PR", style="dim")
    table.add_column("plan", justify="right")
    table.add_column("why")
    by_plan = {id(r.plan): r for r in results}
    counts: dict[str, int] = {}
    for plan in sorted(plans, key=lambda p: (bool(p.decision_needed), p.clone.name, p.name)):
        result = by_plan.get(id(plan))
        label = result.outcome if result else ("needs-decision" if plan.decision_needed else plan.action)
        counts[label] = counts.get(label, 0) + 1
        why = "; ".join(plan.decision_needed or plan.reasons)
        if result and result.detail and not plan.decision_needed:
            why = result.detail
        pr = f" #{plan.pr} {plan.pr_state.lower()}" if plan.pr else ""
        table.add_row(
            plan.clone.name,
            locate.short(plan.path, plan.clone),
            (plan.branch or f"detached@{plan.head[:7]}") + pr,
            f"[{_SHELVE_STYLE.get(label, 'white')}]{label}[/]",
            why,
        )
    console.print(table)
    if counts:
        console.print("\n" + " · ".join(f"[bold]{v}[/bold] {k}" for k, v in sorted(counts.items())))


def _find_shelf(clone: pathlib.Path, name: str, user: str | None) -> shelf_store.Shelf:
    shelves = shelf_store.list_shelves(clone)
    mine = shelve_mod.shelf_ref(user or shelve_mod.shelf_user(clone), name)
    exact = [s for s in shelves if s.ref in {name, f"refs/heads/{name}", mine}]
    if not exact:
        exact = [s for s in shelves if s.name == name]
    if len(exact) != 1:
        found = ", ".join(s.ref.removeprefix("refs/heads/") for s in exact) or "none"
        raise typer.BadParameter(f"{name!r} matches {len(exact)} shelves in {clone.name} ({found})")
    return exact[0]


@app.command()
def unshelve(
    name: str = typer.Argument(..., help="Shelf name (as `estate shelved` lists it) or full ref"),
    repo: str | None = typer.Option(None, "--repo", help="Clone the shelf belongs to (default: the cwd's)"),
    path: str | None = typer.Option(None, "--path", help="Where to recreate the worktree (default: Shelved-Path)"),
    user: str | None = typer.Option(None, "--user", help="Whose shelf (default: yours)"),
    drop: bool = typer.Option(False, "--drop", help="Delete the shelf from origin once restored"),
) -> None:
    """Recreate a shelved worktree; uncommitted work comes back unstaged."""
    clone = locate.one_clone(repo)
    shelf = _find_shelf(clone, name, user)
    restored = shelf_store.unshelve(shelf, path=pathlib.Path(path).expanduser().resolve() if path else None)
    if not restored.ok:
        console.print(f"[bold red]✗[/bold red] {restored.detail}")
        raise typer.Exit(1)
    console.print(f"[bold green]✓[/bold green] {shelf.ref.removeprefix('refs/heads/')} → {restored.detail}")
    if drop:
        dropped = shelf_store.drop(shelf)
        console.print(
            f"  shelf dropped from {shelf_store.REMOTE}"
            if dropped.ok
            else f"  [yellow]drop failed: {dropped.detail}[/]"
        )


@dataclass(slots=True)
class _ShelvedRow:
    """One shelf as `estate shelved` reports it, with the prune verdict when asked."""

    shelf: shelf_store.Shelf
    #: Why it can go ("" to keep it), or the failure; None when not pruning.
    prune: str | None = None
    #: Whether `--apply` dropped it; None when no drop was attempted.
    pruned: bool | None = None

    def to_json(self) -> dict[str, JsonValue]:
        data = self.shelf.to_json()
        if self.prune is not None:
            data["prune"] = self.prune
        if self.pruned is not None:
            data["pruned"] = self.pruned
        return data


def _default_branch(clone: pathlib.Path) -> str:
    head = proc.git_out("symbolic-ref", "--short", f"refs/remotes/{shelf_store.REMOTE}/HEAD", cwd=clone)
    return head.removeprefix(f"{shelf_store.REMOTE}/") or "main"


def _shelved_rows(clone: pathlib.Path, *, prune: bool, apply: bool) -> list[_ShelvedRow]:
    shelves = shelf_store.list_shelves(clone)
    if not shelves:
        return []
    if not prune:
        return [_ShelvedRow(shelf) for shelf in shelves]
    merged = {p.number: p.head_oid for p in pr_heads(clone).values() if p.merged}
    default = _default_branch(clone)
    rows: list[_ShelvedRow] = []
    for shelf in shelves:
        row = _ShelvedRow(shelf, prune=shelf_store.prunable(shelf, merged_prs=merged, default_branch=default))
        if row.prune and apply:
            dropped = shelf_store.drop(shelf)
            row.pruned = dropped.ok
            if not dropped.ok:
                row.prune = f"failed: {dropped.detail}"
        rows.append(row)
    return rows


@app.command()
def shelved(
    repo: str | None = typer.Option(None, "--repo", help="Clone to list (default: the cwd's)"),
    everything: bool = typer.Option(False, "--all", help="Every clone in the estate"),
    root: str | None = typer.Option(None, "--root", help="Code root for --all"),
    refresh: bool = typer.Option(False, "--refresh", help="Rescan rather than using the cached list"),
    prune: bool = typer.Option(False, "--prune", help="Drop shelves whose work landed (dry run unless --apply)"),
    apply: bool = typer.Option(False, "--apply", help="With --prune, actually delete the refs"),
    json_output: bool = typer.Option(False, "--json", help="Emit for a machine"),
) -> None:
    """What is shelved on origin, read from each `TEMP-SHELVED:` commit's trailers.

    `--prune` drops a shelf only when it carries no snapshot and its PR merged
    or its base is already in the default branch. A snapshot is never pruned.
    """
    clones = (
        locate.clones(locate.code_root(root), refresh=refresh, quiet=json_output, err=err)
        if everything
        else [locate.one_clone(repo)]
    )
    rows = [row for clone in clones for row in _shelved_rows(clone, prune=prune, apply=apply)]

    if json_output:
        typer.echo(json.dumps([row.to_json() for row in rows], indent=2))
        return
    if not rows:
        console.print("[bold green]✓ Nothing shelved.[/bold green]")
        return
    table = Table(title="Shelved" + (" (prune dry run — pass --apply)" if prune and not apply else ""))
    for column in ["clone", "shelf", "branch", "PR", "snapshot", "at", *(["prune"] if prune else [])]:
        table.add_column(column)
    for row in rows:
        shelf = row.shelf
        cells = [
            shelf.clone.name,
            shelf.ref.removeprefix("refs/heads/"),
            shelf.trailers.branch,
            shelf.trailers.pr,
            "✓" if shelf.snapshot else "",
            shelf.trailers.at,
        ]
        if prune:
            cells.append(("[green]dropped[/] " if row.pruned else "") + (row.prune or ""))
        table.add_row(*cells)
    console.print(table)
