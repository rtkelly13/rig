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
So is a worktree whose git is not its registered project's, and one whose PRs GitHub could not
list. Removal goes through Orca (`worktree.rm`), never `rm -rf`, so Orca stops the terminals and
drops the sidebar entry. Orca deletes the branch with `git branch -d`, so an unmerged branch is
kept. Only a leftover folder, which git has already let go of, is deleted here, and it is
compared with its branch again just before.
"""

from __future__ import annotations

import os
import pathlib
import sys
import tempfile
from collections.abc import Callable, Mapping
from dataclasses import dataclass, replace
from enum import StrEnum
from typing import Annotated

from pydantic import Field

from rig import proc, worktree_state
from rig.estate.checks import is_dirty, orphaned_count
from rig.estate.orca_runtime import Activity, OrcaRuntime, OrcaUnavailableError, Worktree
from rig.estate.prs import PullRequest, origin_slug
from rig.estate.trim import remove_tree
from rig.typed_json import try_decode_json

#: Agent states that mean someone is still working in the worktree.
BUSY_STATES = frozenset({"working", "blocked", "waiting"})
DEFAULT_IDLE_DAYS = 2.0

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
    FOREIGN = "foreign"
    PR_UNKNOWN = "pr-unknown"


#: Verdicts `--apply` acts on. Every other verdict keeps the worktree.
REMOVABLE = frozenset({Verdict.MERGED, Verdict.LANDED, Verdict.MISSING, Verdict.LEFTOVER})


@dataclass(frozen=True, slots=True)
class Facts:
    """Everything `decide` reads about one worktree."""

    exists: bool
    is_worktree: bool
    is_current: bool = False
    busy_agents: int = 0
    #: The worktree's git is not its registered project's, or the project is not registered.
    foreign: bool = False
    dirty: bool = False
    #: The newest PR for the branch, or None when there is none (or HEAD is detached).
    pr: PullRequest | None = None
    #: False when GitHub could not list the branch's PRs, so "no PR" cannot be trusted.
    pr_known: bool = True
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
    if facts.foreign:
        return Judgement(Verdict.FOREIGN, "its git is not the registered project's")
    if facts.dirty:
        return Judgement(Verdict.DIRTY, "uncommitted or untracked files")
    if not facts.pr_known:
        return Judgement(Verdict.PR_UNKNOWN, "GitHub could not list the branch's PRs")
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
    #: Orca's id for the worktree reviewed, so `apply` removes that one and no other at the same path.
    worktree_id: str = ""

    @property
    def removable(self) -> bool:
        return self.verdict in REMOVABLE

    @property
    def removed(self) -> bool:
        return self.outcome.startswith("removed")

    @property
    def failed(self) -> bool:
        return self.outcome.startswith("failed")


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


def _common_dir(path: pathlib.Path) -> str:
    """The git common dir *path* belongs to, comparable across spellings; empty when git cannot say."""
    out = proc.git_out("rev-parse", "--path-format=absolute", "--git-common-dir", cwd=path)
    return _comparable(out) if out else ""


def _belongs_to(path: pathlib.Path, repo: pathlib.Path | None) -> bool:
    """True when the worktree at *path* shares *repo*'s git, so removing it touches only that project."""
    if repo is None:
        return False
    mine = _common_dir(path)
    return bool(mine) and mine == _common_dir(repo)


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
    scratch_root = worktree_state.state_dir(repo)
    scratch_root.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="orca-leftover-", dir=scratch_root) as scratch:
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
class PrAnswer:
    """The newest PR for one branch, or `known=False` when GitHub could not say."""

    known: bool
    pr: PullRequest | None = None


type PrLookup = Callable[[pathlib.Path, str], PrAnswer]
"""The newest same-repository PR for a branch of a clone."""

type GhCli = Callable[[list[str]], proc.Ran]


def _run_gh(args: list[str]) -> proc.Ran:
    return proc.gh(*args)


@dataclass(frozen=True, slots=True)
class _BranchPr:
    number: int
    state: str = ""
    head_oid: Annotated[str, Field(alias="headRefOid")] = ""
    cross_repository: Annotated[bool, Field(alias="isCrossRepository")] = False


def branch_pr(repo: pathlib.Path, branch: str, run: GhCli = _run_gh) -> PrAnswer:
    """The newest PR whose head is *branch* in *repo*'s own GitHub repository.

    Asked per branch, so a PR outside any recent window is still found, and a fork's PR that
    happens to share the branch name is ignored. Any failure answers `known=False`.
    """
    slug = origin_slug(repo)
    if slug is None:
        return PrAnswer(known=False)
    ran = run(
        [
            "pr",
            "list",
            "--repo",
            slug.full_name,
            "--head",
            branch,
            "--state",
            "all",
            "--limit",
            "100",
            "--json",
            "number,state,headRefOid,isCrossRepository",
        ]
    )
    pulls = try_decode_json(list[_BranchPr], ran.out) if ran.ok else None
    if pulls is None:
        return PrAnswer(known=False)
    own = [p for p in pulls if not p.cross_repository]
    if not own:
        return PrAnswer(known=True)
    newest = max(own, key=lambda p: p.number)
    pr = PullRequest(number=newest.number, state=newest.state, head_ref=branch, head_oid=newest.head_oid)
    return PrAnswer(known=True, pr=pr)


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
    if repo is None or not _belongs_to(path, repo):
        return replace(base, is_worktree=True, foreign=True)
    dirty = is_dirty(path)
    answer = context.prs(repo, branch) if (branch and not dirty) else PrAnswer(known=True)
    pr = answer.pr
    return replace(
        base,
        is_worktree=True,
        dirty=dirty,
        pr=pr,
        pr_known=answer.known,
        beyond_merge=_beyond_merge(path, pr.head_oid) if pr is not None and pr.merged else 0,
        in_default=False if dirty or pr is not None or not answer.known else _in_default(path),
    )


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
        prs=prs or branch_pr,
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
            Row(
                profile,
                project,
                worktree.path,
                short_branch(worktree.branch),
                judgement.verdict,
                judgement.reason,
                worktree_id=worktree.id,
            )
        )
    return rows


def select(runtime: OrcaRuntime, wanted: str) -> frozenset[str]:
    """The ids of the projects *wanted* names in this profile.

    *wanted* is a path to a project's checkout or to one of its worktrees (or anything inside
    them), or a name: the checkout's folder name or Orca's display name, ignoring case.
    """
    repos = runtime.repos()
    candidate = pathlib.Path(wanted).expanduser()
    roots = [(r.id, pathlib.Path(r.path)) for r in repos]
    roots += [(w.repo_id, pathlib.Path(w.path)) for w in runtime.worktrees()]
    if candidate.exists():
        here = candidate.resolve()
        return frozenset(repo_id for repo_id, root in roots if _is_within(here, root))
    if candidate.is_absolute():
        # A vanished worktree is still registered: match its path as Orca spells it.
        return frozenset(repo_id for repo_id, root in roots if _comparable(root) == _comparable(candidate))
    key = wanted.strip().casefold()
    return frozenset(
        r.id for r in repos if key in {pathlib.Path(r.path).name.casefold(), (r.display_name or "").casefold()}
    )


def apply(runtime: OrcaRuntime, rows: list[Row]) -> list[Row]:
    """Remove each removable worktree; returns the rows with what happened to each."""
    repo_paths = {r.id: pathlib.Path(r.path) for r in runtime.repos()}
    by_id = {w.id: w for w in runtime.worktrees()}
    done: list[Row] = []
    for row in rows:
        if not row.removable:
            done.append(row)
            continue
        worktree = by_id.get(row.worktree_id)
        if worktree is None or _comparable(worktree.path) != _comparable(row.path):
            done.append(replace(row, outcome="skipped: Orca no longer lists the worktree reviewed"))
            continue
        outcome = _remove(runtime, worktree, row, repo_paths.get(worktree.repo_id))
        done.append(replace(row, outcome=outcome))
    return done


def _still_safe(path: pathlib.Path, row: Row, repo: pathlib.Path | None) -> str:
    """Why the worktree must now be kept, re-checked just before removal; empty when it may go."""
    if row.verdict is Verdict.MISSING:
        return "its folder is back" if path.exists() else ""
    if repo is None:
        return "its project is no longer registered"
    if row.verdict is Verdict.LEFTOVER:
        compared = leftover_matches(path, repo, row.branch)
        return "" if compared.matches else f"changed since review: {compared.detail}"
    if not _belongs_to(path, repo):
        return "its git is not the registered project's"
    return "changed since review: uncommitted or untracked files" if is_dirty(path) else ""


def _remove(runtime: OrcaRuntime, worktree: Worktree, row: Row, repo: pathlib.Path | None) -> str:
    path = pathlib.Path(worktree.path)
    if keep := _still_safe(path, row, repo):
        return f"skipped: {keep}"
    if row.verdict is Verdict.LEFTOVER:
        try:
            remove_tree(path)
        except OSError as e:
            return f"failed: could not delete the folder: {e}"
    if row.verdict in {Verdict.LEFTOVER, Verdict.MISSING} and repo is not None:
        _ = proc.git("worktree", "prune", cwd=repo)
    try:
        confirmed = runtime.remove_worktree(worktree.id)
    except OrcaUnavailableError as e:
        if _gone(runtime, worktree.id):
            return "removed (git had already dropped it from Orca)"
        return f"failed: {e}"
    if confirmed or _gone(runtime, worktree.id):
        return "removed"
    return "failed: Orca accepted the removal but still lists the worktree"


def _gone(runtime: OrcaRuntime, worktree_id: str) -> bool:
    """True when Orca no longer lists the worktree; a failed listing proves nothing."""
    try:
        return all(w.id != worktree_id for w in runtime.worktrees())
    except OrcaUnavailableError:
        return False
