"""Repos restarted without history, and the clones that still carry the old one.

A repo can be restarted: a new repo under the old name whose `main` begins at a single
root commit with the same tree, while the old repo is renamed and archived. rig itself
was, on 2026-11-07, so work content never ships with it.

Nothing records a restart: the new root commit says it. Its message names the archive
and the old main it was cut from:

    rig: a fresh start from rtkelly13/rig-archive@f715cb88

so any clone that has fetched the new `main` can tell it was restarted, from where, and
which commit every new branch descends from.

A clone made before the restart has unrelated history. This module works out what such
a clone needs: remotes (`origin` the new repo, `archive` the old), the git config the
pre-push guard reads (`rig.git_hooks`), and for each local branch whether it is already
on the new history, retired (merged or closed in the archive), or still to port.

The decisions are pure functions over facts; `detect_restart`, `clone_facts` and
`branch_facts` gather those facts through git and gh. `history_cli` renders and acts on
them.
"""

from __future__ import annotations

import pathlib
import re
from collections.abc import Mapping
from dataclasses import dataclass, field
from enum import StrEnum
from typing import Annotated

from pydantic import Field

from rig import git_hooks, proc
from rig.estate.checks import list_worktrees
from rig.repo_slug import RepoSlug

#: The new repo's remote name, and the archive's.
ORIGIN = "origin"
ARCHIVE = "archive"


# ---------------------------------------------------------------- the restart

#: How a restarted repo's root commit names where it came from.
_FRESH_START = re.compile(r"\bfresh start from (?P<archive>[\w.-]+/[\w.-]+)@(?P<old_main>[0-9a-f]{7,40})\b")


@dataclass(frozen=True, slots=True)
class Restart:
    """A repo restarted without history, as its new root commit describes it."""

    url: str
    slug: RepoSlug
    archive_url: str
    archive_slug: RepoSlug
    #: The new repo's root commit; every branch pushed there descends from it.
    root: str
    #: The old repo's main when the restart was cut, as the root commit names it.
    old_main: str
    date: str = ""


def parse_restart(*, url: str, root: str, message: str, date: str = "") -> Restart | None:
    """The restart *root* records, or None when its message does not name an archive.

    The archive's URL is *url* with the archive's slug in place of the repo's, so it
    keeps whichever transport (ssh or https) the clone already uses.
    """
    slug, found = RepoSlug.from_remote(url), _FRESH_START.search(message)
    archive_slug = RepoSlug.from_path(found["archive"]) if found else None
    if slug is None or found is None or archive_slug is None or archive_slug.key == slug.key:
        return None
    archive_url = re.sub(re.escape(slug.full_name), archive_slug.full_name, url, count=1, flags=re.IGNORECASE)
    return Restart(url, slug, archive_url, archive_slug, root, found["old_main"], date)


def detect_restart(clone: pathlib.Path, remote: str = ORIGIN) -> Restart | None:
    """The restart *remote*'s main begins with, from its root commit. Fetch *remote* first."""
    url = proc.git_out("remote", "get-url", remote, cwd=clone)
    roots = proc.git_lines("rev-list", "--max-parents=0", f"refs/remotes/{remote}/main", cwd=clone) or []
    if not url or len(roots) != 1:
        return None
    message = proc.git_out("log", "-1", "--format=%B", roots[0], cwd=clone)
    date = proc.git_out("log", "-1", "--format=%cs", roots[0], cwd=clone)
    return parse_restart(url=url, root=roots[0], message=message, date=date)


# ---------------------------------------------------------------- the clone


@dataclass(frozen=True, slots=True)
class CloneFacts:
    """What `estate history setup` needs to know about a clone, beyond the restart itself."""

    remotes: Mapping[str, str]
    guard_root: str = ""
    guard_slug: str = ""
    hook_current: bool = False
    #: Whether the clone has the old main (it may never have fetched the archive).
    has_old_main: bool = False
    #: None when there is no local main.
    main_on_new_history: bool | None = None
    main_upstream: str = ""


@dataclass(frozen=True, slots=True)
class Step:
    """One change to a clone. *git* is the argv after `git`; empty for installing the hook."""

    description: str
    git: tuple[str, ...] = ()


@dataclass(frozen=True, slots=True)
class SetupPlan:
    steps: tuple[Step, ...] = ()
    #: Things a person has to do: they would discard work, so they are never applied.
    advice: tuple[str, ...] = ()


def _remote_for(remotes: Mapping[str, str], slug: RepoSlug) -> str | None:
    return next((name for name, url in remotes.items() if (s := RepoSlug.from_remote(url)) and s.key == slug.key), None)


def plan_setup(restart: Restart, facts: CloneFacts) -> SetupPlan:
    """The steps that ready a clone of a restarted repo: archive remote, guard config, hook, main's upstream.

    *restart* was read from `origin`, so `origin` is already the new repo.
    """
    steps: list[Step] = []
    advice: list[str] = []
    archive = _remote_for(facts.remotes, restart.archive_slug)
    if archive is None:
        if ARCHIVE in facts.remotes:
            advice.append(f"{ARCHIVE} points at {facts.remotes[ARCHIVE]}, not {restart.archive_slug}; fix it by hand")
        else:
            add = ("remote", "add", ARCHIVE, restart.archive_url)
            steps.append(Step(f"add {ARCHIVE} for {restart.archive_slug}", add))
            archive = ARCHIVE
    if archive is not None and not facts.has_old_main:
        steps.append(Step(f"fetch {archive}, which holds the old history", ("fetch", archive)))

    if facts.guard_root != restart.root:
        root = ("config", git_hooks.HISTORY_ROOT_KEY, restart.root)
        steps.append(Step("record the new root for the push guard", root))
    if facts.guard_slug != restart.slug.full_name:
        slug = restart.slug.full_name
        steps.append(Step("record the restarted repo for the push guard", ("config", git_hooks.HISTORY_SLUG_KEY, slug)))
    if not facts.hook_current:
        steps.append(Step("install the pre-push hook with the history guard"))

    upstream = f"{ORIGIN}/main"
    if facts.main_on_new_history is False:
        advice.append(
            "main is still on the old history. Check `git status` in the worktree that has it, then: "
            f"git switch main && git reset --hard {upstream} && git branch -u {upstream} main"
        )
    elif facts.main_on_new_history and facts.main_upstream != upstream:
        steps.append(Step(f"track {upstream} from main", ("branch", "-u", upstream, "main")))
    return SetupPlan(tuple(steps), tuple(advice))


# ---------------------------------------------------------------- the branches


class Kind(StrEnum):
    #: Descends from the new root: nothing to do.
    CURRENT = "current"
    #: Old history with nothing left to carry: in the old main, or its archive PR merged or closed.
    RETIRED = "retired"
    #: Old history with work not in the old main: rebase it onto the new main to keep it.
    TO_PORT = "to-port"


@dataclass(frozen=True, slots=True)
class ArchivePr:
    number: int
    state: str
    url: str


@dataclass(frozen=True, slots=True)
class BranchFacts:
    name: str
    sha: str
    on_new_history: bool
    #: Commits on the branch that the old main lacks; 0 on the new history.
    ahead_of_old_main: int = 0
    #: Merging the branch into the old main would change nothing: its work landed, squashed or not.
    in_old_main: bool = False
    worktree: pathlib.Path | None = None
    archive_pr: ArchivePr | None = None


def classify(branch: BranchFacts) -> Kind:
    if branch.on_new_history:
        return Kind.CURRENT
    if branch.ahead_of_old_main == 0 or branch.in_old_main:
        return Kind.RETIRED
    if branch.archive_pr is not None and branch.archive_pr.state in {"MERGED", "CLOSED"}:
        return Kind.RETIRED
    return Kind.TO_PORT


@dataclass(frozen=True, slots=True)
class PortPlan:
    """How to port one branch, or why not. *cwd* is where the rebase runs."""

    branch: str
    refused: str = ""
    cwd: pathlib.Path | None = None
    rebase: tuple[str, ...] = ()
    switches_cwd: bool = False


def plan_port(
    restart: Restart,
    branch: BranchFacts,
    *,
    here: pathlib.Path,
    here_dirty: bool,
    new_remote: str = ORIGIN,
    base: str | None = None,
) -> PortPlan:
    """Rebase *branch*'s own commits onto the new main.

    The commits are those after *base*, the old main by default. A branch stacked on
    another old branch needs *base* set to its parent, or it carries the parent's
    commits too.

    It runs where the branch is checked out, and only when that is *here*: another
    worktree may belong to a live session. A branch checked out nowhere is rebased in
    *here*, which then has it checked out.
    """
    kind = classify(branch)
    if kind is not Kind.TO_PORT:
        return PortPlan(branch.name, refused=f"{branch.name} is {kind}; there is nothing to port")
    if branch.worktree is not None and branch.worktree.resolve() != here.resolve():
        return PortPlan(branch.name, refused=f"{branch.name} is checked out in {branch.worktree}; port it from there")
    if here_dirty:
        return PortPlan(branch.name, refused=f"{here} has uncommitted changes; commit or set them aside first")
    rebase = ("rebase", "--onto", f"{new_remote}/main", base or restart.old_main, branch.name)
    return PortPlan(branch.name, cwd=here, rebase=rebase, switches_cwd=branch.worktree is None)


# ---------------------------------------------------------------- gathering facts


def _is_ancestor(clone: pathlib.Path, ancestor: str, commit: str) -> bool:
    return proc.git("merge-base", "--is-ancestor", ancestor, commit, cwd=clone).ok


def _merges_to_nothing(clone: pathlib.Path, base: str, branch: str, base_tree: str) -> bool:
    """Whether merging *branch* into *base* leaves *base*'s tree as it was. Writes no refs or worktree."""
    if not base_tree:
        return False
    return proc.git_out("merge-tree", "--write-tree", base, branch, cwd=clone) == base_tree


def remotes_of(clone: pathlib.Path) -> dict[str, str]:
    remotes: dict[str, str] = {}
    for name in proc.git_lines("remote", cwd=clone) or []:
        if url := proc.git_out("remote", "get-url", name, cwd=clone):
            remotes[name] = url
    return remotes


def common_dir(path: pathlib.Path) -> pathlib.Path | None:
    """The shared `.git` directory of the clone *path* is in, whichever worktree it is."""
    out = proc.git_out("rev-parse", "--path-format=absolute", "--git-common-dir", cwd=path)
    return pathlib.Path(out) if out else None


def clone_facts(clone: pathlib.Path, restart: Restart) -> CloneFacts:
    git_dir = common_dir(clone)
    hook = git_hooks.pre_push_hook(git_dir / "hooks") if git_dir else None
    main = proc.git_out("rev-parse", "--verify", "--quiet", "refs/heads/main", cwd=clone)
    return CloneFacts(
        remotes=remotes_of(clone),
        guard_root=proc.git_out("config", "--get", git_hooks.HISTORY_ROOT_KEY, cwd=clone),
        guard_slug=proc.git_out("config", "--get", git_hooks.HISTORY_SLUG_KEY, cwd=clone),
        hook_current=bool(hook and hook.exists() and git_hooks.hook_is_current(hook.read_text(encoding="utf-8"))),
        has_old_main=proc.git("cat-file", "-e", f"{restart.old_main}^{{commit}}", cwd=clone).ok,
        main_on_new_history=_is_ancestor(clone, restart.root, main) if main else None,
        main_upstream=proc.git_out("rev-parse", "--abbrev-ref", "main@{upstream}", cwd=clone) if main else "",
    )


@dataclass(frozen=True, slots=True)
class _Pr:
    number: int
    state: str
    url: str
    head: Annotated[str, Field(alias="headRefName")] = ""


@dataclass(frozen=True, slots=True)
class ArchivePrs:
    by_head: Mapping[str, ArchivePr] = field(default_factory=dict[str, ArchivePr])
    #: False when gh could not be asked; branches then classify on git alone.
    known: bool = False


def archive_prs(restart: Restart, *, limit: int = 1000) -> ArchivePrs:
    """The newest archive PR per head branch, from one `gh pr list`."""
    listed = proc.gh_json(
        list[_Pr],
        *("pr", "list", "--repo", restart.archive_slug.full_name, "--state", "all"),
        *("--limit", str(limit), "--json", "number,state,url,headRefName"),
    )
    if listed is None:
        return ArchivePrs()
    by_head: dict[str, ArchivePr] = {}
    for pr in sorted(listed, key=lambda p: p.number):
        by_head[pr.head] = ArchivePr(pr.number, pr.state, pr.url)
    return ArchivePrs(by_head, known=True)


def branch_facts(clone: pathlib.Path, restart: Restart, prs: ArchivePrs) -> list[BranchFacts]:
    """Every local branch of *clone*, with where it stands against *restart*."""
    checked_out = {w.branch.removeprefix("refs/heads/"): w.path for w in list_worktrees(clone) if w.branch}
    facts: list[BranchFacts] = []
    old_tree = proc.git_out("rev-parse", f"{restart.old_main}^{{tree}}", cwd=clone)
    refs = proc.git_lines("for-each-ref", "--format=%(refname:short) %(objectname)", "refs/heads", cwd=clone)
    for line in refs or []:
        name, _, sha = line.rpartition(" ")
        on_new = _is_ancestor(clone, restart.root, sha)
        ahead, landed = 0, False
        if not on_new:
            count = proc.git_out("rev-list", "--count", f"{restart.old_main}..{sha}", cwd=clone)
            ahead = int(count) if count.isdigit() else 0
            landed = ahead > 0 and _merges_to_nothing(clone, restart.old_main, sha, old_tree)
        worktree, pr = checked_out.get(name), prs.by_head.get(name)
        facts.append(BranchFacts(name, sha, on_new, ahead, landed, worktree, pr))
    return facts
