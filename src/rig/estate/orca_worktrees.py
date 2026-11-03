"""`estate orca-worktrees`: which of Orca's worktrees are finished, and clearing them out.

Orca lists every worktree of every registered project, and nothing removes one when its work
lands, so the sidebar fills with finished branches. This module decides, one worktree at a time,
whether anything would be lost by removing it. `decide` is pure: it reads `Facts`, which
`gather` collects from git, Orca's `worktree.ps` and GitHub, so every verdict is reproducible
from what the report prints.

A worktree is finished when any of these holds:

- **merged**: the newest PR for its branch merged, and HEAD has no commit that is neither on a
  remote nor in what merged. Ancestry cannot say this under squash merges; GitHub can.
- **landed**: no PR, HEAD is already in the default branch, and nobody has touched it for
  `idle_days`. The idle rule spares a worktree just made from `main` for new work.
- **missing**: Orca still lists it, but its folder is gone.
- **leftover**: the folder is still there, but git no longer knows it as a worktree, which is what
  an interrupted delete leaves on Windows when a locked file stops it halfway. It goes only when
  every file git does not ignore matches its branch's last commit, so the branch keeps it all.

Everything else is kept, and a dirty tree or a running agent is kept whatever else is true.
Removal goes through Orca (`worktree.rm`), never `rm -rf`, so Orca stops the terminals and
drops the sidebar entry. Orca deletes the branch with `git branch -d`, so an unmerged branch is
kept. Only a leftover folder, which git has already let go of, is deleted here.
"""

from __future__ import annotations

import os
import pathlib
import sys
import tempfile
from collections.abc import Callable, Mapping
from dataclasses import dataclass, replace
from enum import StrEnum

from rig import proc
from rig.estate.checks import is_dirty, orphaned_count
from rig.estate.orca_runtime import Activity, OrcaRuntime, OrcaUnavailableError, Worktree
from rig.estate.prs import PullRequest, pr_heads
from rig.estate.trim import remove_tree

#: Agent states that mean someone is still working in the worktree.
BUSY_STATES = frozenset({"working", "blocked", "waiting"})
DEFAULT_IDLE_DAYS = 2.0

type PrLookup = Callable[[pathlib.Path], Mapping[str, PullRequest]]
"""The newest PR per head ref for a clone."""
_MS_PER_DAY = 86_400_000


class Verdict(StrEnum):
    MERGED = "merged"
    LANDED = "landed"
    MISSING = "missing"
    LEFTOVER = "leftover"
    CURRENT = "current"
    BUSY = "busy"
    DIRTY = "dirty"
    AFTER_MERGE = "after-merge"
    OPEN_PR = "open-pr"
    CLOSED_PR = "closed-pr"
    UNMERGED = "unmerged"
    RECENT = "recent"
    LEFTOVER_DIFFERS = "leftover-differs"


#: Verdicts `--apply` acts on. Every other verdict keeps the worktree.
REMOVABLE = frozenset({Verdict.MERGED, Verdict.LANDED, Verdict.MISSING, Verdict.LEFTOVER})


@dataclass(frozen=True, slots=True)
class Facts:
    """Everything `decide` reads about one worktree."""

    exists: bool
    is_worktree: bool
    is_current: bool = False
    busy_agents: int = 0
    dirty: bool = False
    #: The newest PR for the branch, or None when there is none (or HEAD is detached).
    pr: PullRequest | None = None
    #: Commits on HEAD that are on no remote and not in the PR's merged head.
    beyond_merge: int = 0
    #: HEAD is an ancestor of the remote default branch.
    in_default: bool = False
    idle_days: float = 0.0
    #: For a leftover folder: None when it could not be checked, else whether it matches its branch.
    leftover_matches: bool | None = None
    leftover_detail: str = ""


@dataclass(frozen=True, slots=True)
class Judgement:
    verdict: Verdict
    reason: str


def decide(facts: Facts, *, idle_days: float = DEFAULT_IDLE_DAYS) -> Judgement:
    """The verdict for one worktree and the reason, in the order the safety rules need."""
    if facts.is_current:
        return Judgement(Verdict.CURRENT, "this command is running inside it")
    if facts.busy_agents:
        return Judgement(Verdict.BUSY, f"{facts.busy_agents} agent(s) still working or waiting")
    if not facts.exists:
        return Judgement(Verdict.MISSING, "the folder is gone")
    if not facts.is_worktree:
        if facts.leftover_matches:
            return Judgement(Verdict.LEFTOVER, "not a git worktree any more; its files match the branch")
        return Judgement(Verdict.LEFTOVER_DIFFERS, facts.leftover_detail or "not a git worktree; could not compare it")
    if facts.dirty:
        return Judgement(Verdict.DIRTY, "uncommitted or untracked files")
    pr = facts.pr
    if pr is not None and pr.merged:
        if facts.beyond_merge:
            return Judgement(
                Verdict.AFTER_MERGE, f"PR #{pr.number} merged, but {facts.beyond_merge} commit(s) since are local"
            )
        return Judgement(Verdict.MERGED, f"PR #{pr.number} merged")
    if pr is not None and pr.open:
        return Judgement(Verdict.OPEN_PR, f"PR #{pr.number} open")
    if pr is not None:
        return Judgement(Verdict.CLOSED_PR, f"PR #{pr.number} closed without merging: decide by hand")
    if not facts.in_default:
        return Judgement(Verdict.UNMERGED, "commits not in the default branch, and no PR")
    if facts.idle_days < idle_days:
        return Judgement(Verdict.RECENT, f"already in the default branch, but active {facts.idle_days:.1f} day(s) ago")
    return Judgement(Verdict.LANDED, f"already in the default branch; idle {facts.idle_days:.0f} day(s)")


# ---------------------------------------------------------------- gathering facts


@dataclass(frozen=True, slots=True)
class Row:
    """One linked worktree, its verdict, and what `--apply` did."""

    profile: str
    project: str
    path: str
    branch: str
    verdict: Verdict
    reason: str
    outcome: str = ""

    @property
    def removable(self) -> bool:
        return self.verdict in REMOVABLE


def short_branch(ref: str | None) -> str:
    return (ref or "").removeprefix("refs/heads/")


def _comparable(path: pathlib.Path | str) -> str:
    text = os.path.normpath(str(path))
    return text.casefold() if sys.platform == "win32" else text


def _is_within(path: pathlib.Path, root: pathlib.Path) -> bool:
    inner, outer = _comparable(path), _comparable(root)
    return inner == outer or inner.startswith(outer.rstrip("\\/") + os.sep)


def _is_worktree(path: pathlib.Path) -> bool:
    """True when git recognises *path* itself as the top of a working tree."""
    top = proc.git_out("rev-parse", "--show-toplevel", cwd=path)
    return bool(top) and _comparable(top) == _comparable(path)


def _beyond_merge(path: pathlib.Path, merged_oid: str) -> int:
    """Commits on HEAD found on no remote and not in *merged_oid*; falls back when the oid is unknown here."""
    if merged_oid:
        out = proc.git_out("rev-list", "--count", "HEAD", "--not", "--remotes", merged_oid, cwd=path)
        if out.isdigit():
            return int(out)
        if proc.git_out("rev-parse", "HEAD", cwd=path) == merged_oid:
            return 0
    return orphaned_count(path)


def _in_default(path: pathlib.Path) -> bool:
    default = proc.git_out("rev-parse", "--abbrev-ref", "origin/HEAD", cwd=path) or "origin/main"
    return proc.git("merge-base", "--is-ancestor", "HEAD", default, cwd=path).ok


@dataclass(frozen=True, slots=True)
class Comparison:
    """Whether a leftover folder matches its branch, and if not, why."""

    matches: bool
    detail: str = ""


def leftover_matches(folder: pathlib.Path, repo: pathlib.Path, branch: str) -> Comparison:
    """Whether every file git does not ignore in *folder* matches *branch*'s last commit.

    The folder has no `.git` any more, so it is compared through the main checkout's object store
    with a throwaway index: read the branch's tree into it, refresh it against the folder, then
    look for changed or untracked files. Ignored files (`.venv`, `node_modules`) regenerate and do
    not count.

    A file missing from the folder loses nothing, because the branch has it. An interrupted delete
    leaves many missing, usually `.gitignore` among them, and without it every file in `.venv`
    would look untracked. So missing files are marked skip-worktree, and git then reads their
    ignore rules from the index instead of the folder.
    """
    if not branch:
        return Comparison(False, "detached when it broke; nothing to compare it with")
    common = proc.git_out("rev-parse", "--path-format=absolute", "--git-common-dir", cwd=repo)
    if not common:
        return Comparison(False, f"could not find the git folder of {repo.as_posix()}")
    if not proc.git("rev-parse", "--verify", "--quiet", f"refs/heads/{branch}", cwd=repo).ok:
        return Comparison(False, f"branch {branch} no longer exists, so the folder may hold the only copy")
    with tempfile.TemporaryDirectory(prefix="orca-leftover-") as scratch:
        env = {
            **os.environ,
            "GIT_DIR": common,
            "GIT_WORK_TREE": str(folder),
            "GIT_INDEX_FILE": str(pathlib.Path(scratch) / "index"),
        }

        def run(*args: str) -> proc.Ran:
            return proc.git(*args, cwd=folder, env=env)

        if not run("read-tree", f"refs/heads/{branch}").ok:
            return Comparison(False, f"could not read {branch}")
        _ = run("update-index", "-q", "--refresh")
        missing = run("diff-files", "--name-only", "--diff-filter=D", "-z")
        if missing.ok and missing.out:
            _ = proc.git(
                "update-index", "-z", "--skip-worktree", "--stdin", cwd=folder, env=env, stdin_text=missing.out
            )
        changed = run("diff-files", "--name-only")
        untracked = run("ls-files", "--others", "--exclude-standard")
    if not (changed.ok and untracked.ok):
        return Comparison(False, "git could not compare the folder")
    differing = [line for line in (changed.out + untracked.out).splitlines() if line.strip()]
    if differing:
        return Comparison(False, f"{len(differing)} file(s) differ from {branch}, e.g. {differing[0]}")
    return Comparison(True)


@dataclass(frozen=True, slots=True)
class Context:
    """What is shared across one profile's worktrees."""

    repo_paths: Mapping[str, pathlib.Path]
    activity: Mapping[str, Activity]
    prs: PrLookup
    now_ms: float
    cwd: pathlib.Path


def gather(worktree: Worktree, context: Context) -> Facts:
    """Collect `Facts` for one linked worktree; reads only, changes nothing."""
    path = pathlib.Path(worktree.path)
    running = context.activity.get(worktree.id)
    busy = sum(1 for agent in (running.agents or ()) if agent.state in BUSY_STATES) if running else 0
    idle = (context.now_ms - worktree.last_activity_ms) / _MS_PER_DAY if worktree.last_activity_ms else 0.0
    base = Facts(
        exists=path.is_dir(),
        is_worktree=False,
        is_current=_is_within(context.cwd, path),
        busy_agents=busy,
        idle_days=max(idle, 0.0),
    )
    if not base.exists or base.is_current or busy:
        return base
    branch = short_branch(worktree.branch)
    repo = context.repo_paths.get(worktree.repo_id)
    if not _is_worktree(path):
        if repo is None:
            return replace(base, leftover_matches=False, leftover_detail="its project is not registered in Orca")
        compared = leftover_matches(path, repo, branch)
        return replace(base, leftover_matches=compared.matches, leftover_detail=compared.detail)
    dirty = is_dirty(path)
    pr = context.prs(repo).get(branch) if (repo is not None and branch and not dirty) else None
    return replace(
        base,
        is_worktree=True,
        dirty=dirty,
        pr=pr,
        beyond_merge=_beyond_merge(path, pr.head_oid) if pr is not None and pr.merged else 0,
        in_default=False if dirty or pr is not None else _in_default(path),
    )


def cached_prs() -> PrLookup:
    """`pr_heads` once per clone: one GraphQL round per project, not per worktree."""
    seen: dict[str, Mapping[str, PullRequest]] = {}

    def lookup(repo: pathlib.Path) -> Mapping[str, PullRequest]:
        key = _comparable(repo)
        if key not in seen:
            seen[key] = pr_heads(repo)
        return seen[key]

    return lookup


# ---------------------------------------------------------------- one profile


def review(
    runtime: OrcaRuntime,
    profile: str,
    *,
    cwd: pathlib.Path,
    now_ms: float,
    idle_days: float = DEFAULT_IDLE_DAYS,
    prs: PrLookup | None = None,
    only: frozenset[str] | None = None,
) -> list[Row]:
    """Every linked worktree in one Orca profile with its verdict, or only those of the *only* repo ids."""
    repos = [r for r in runtime.repos() if only is None or r.id in only]
    if not repos:
        return []
    context = Context(
        repo_paths={r.id: pathlib.Path(r.path) for r in repos},
        activity={a.worktree_id: a for a in runtime.activity()},
        prs=prs or cached_prs(),
        now_ms=now_ms,
        cwd=cwd,
    )
    names = {r.id: r.display_name or pathlib.Path(r.path).name for r in repos}
    rows: list[Row] = []
    for worktree in runtime.worktrees():
        if worktree.is_main or worktree.repo_id not in names:
            continue
        judgement = decide(gather(worktree, context), idle_days=idle_days)
        project = names.get(worktree.repo_id, worktree.repo_id)
        rows.append(
            Row(profile, project, worktree.path, short_branch(worktree.branch), judgement.verdict, judgement.reason)
        )
    return rows


def select(runtime: OrcaRuntime, wanted: str) -> frozenset[str]:
    """The ids of the projects *wanted* names in this profile.

    *wanted* is a path to a project's checkout or to one of its worktrees (or anything inside
    them), or a name: the checkout's folder name or Orca's display name, ignoring case.
    """
    repos = runtime.repos()
    candidate = pathlib.Path(wanted).expanduser()
    if candidate.exists():
        here = candidate.resolve()
        roots = [(r.id, pathlib.Path(r.path)) for r in repos]
        roots += [(w.repo_id, pathlib.Path(w.path)) for w in runtime.worktrees()]
        return frozenset(repo_id for repo_id, root in roots if _is_within(here, root))
    key = wanted.strip().casefold()
    return frozenset(
        r.id for r in repos if key in {pathlib.Path(r.path).name.casefold(), (r.display_name or "").casefold()}
    )


def apply(runtime: OrcaRuntime, rows: list[Row]) -> list[Row]:
    """Remove each removable worktree; returns the rows with what happened to each."""
    repo_paths = {r.id: pathlib.Path(r.path) for r in runtime.repos()}
    by_path = {_comparable(w.path): w for w in runtime.worktrees()}
    done: list[Row] = []
    for row in rows:
        worktree = by_path.get(_comparable(row.path))
        if not row.removable or worktree is None:
            done.append(row)
            continue
        outcome = _remove(runtime, worktree, row.verdict, repo_paths.get(worktree.repo_id))
        done.append(replace(row, outcome=outcome))
    return done


def _remove(runtime: OrcaRuntime, worktree: Worktree, verdict: Verdict, repo: pathlib.Path | None) -> str:
    path = pathlib.Path(worktree.path)
    if verdict is Verdict.LEFTOVER:
        try:
            remove_tree(path)
        except OSError as e:
            return f"failed: could not delete the folder: {e}"
    if verdict in {Verdict.LEFTOVER, Verdict.MISSING} and repo is not None:
        _ = proc.git("worktree", "prune", cwd=repo)
    try:
        runtime.remove_worktree(worktree.id)
    except OrcaUnavailableError as e:
        if verdict in {Verdict.LEFTOVER, Verdict.MISSING} and not path.exists():
            return "removed (git had already dropped it from Orca)"
        return f"failed: {e}"
    return "removed"
