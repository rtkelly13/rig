"""The shelves on origin: what `estate shelve` pushed, read back and restored.

A shelf is a ref under `refs/heads/shelved/<user>/<name>` whose tip is one
`TEMP-SHELVED:` commit. Its `Shelved-*` trailers say where the work came from,
so listing, pruning and restoring need nothing but the ref.
"""

from __future__ import annotations

import pathlib
from collections.abc import Mapping, Sequence
from dataclasses import dataclass

from rig import proc
from rig.estate.trim import LONG_PATHS
from rig.typed_json import JsonValue, decode

PREFIX = "TEMP-SHELVED:"
REF_ROOT = "refs/heads/shelved"
REMOTE = "origin"


@dataclass(slots=True, frozen=True)
class Done:
    """Whether a step worked, and its output (stdout on success, the error otherwise)."""

    ok: bool
    detail: str


def run_git(args: Sequence[str], cwd: pathlib.Path, *, env: Mapping[str, str] | None = None) -> Done:
    """Run git with long paths on, so a write never stops part-way through a tree. Never raises."""
    ran = proc.git(*LONG_PATHS, *args, cwd=cwd, timeout=300, env=env)
    if not ran.ok:
        return Done(ok=False, detail=(ran.err or ran.out).strip())
    return Done(ok=True, detail=ran.out.strip())


def git_out(args: Sequence[str], cwd: pathlib.Path) -> str:
    """git's stdout, or "" when it failed."""
    run = run_git(args, cwd)
    return run.detail if run.ok else ""


@dataclass(slots=True, frozen=True)
class Trailers:
    """The `Shelved-*` trailers of a marker commit; "" where one is absent."""

    branch: str = ""
    base: str = ""
    path: str = ""
    pr: str = ""
    at: str = ""
    by: str = ""


def parse_trailers(text: str) -> Trailers:
    """Read `Shelved-*` trailers from a commit message. Pure."""
    found: dict[str, str] = {}
    for line in text.splitlines():
        key, sep, value = line.partition(":")
        if sep and key.startswith("Shelved-") and " " not in key:
            found[key.removeprefix("Shelved-").lower()] = value.strip()
    return decode(Trailers, found)


def remote_shelves(clone: pathlib.Path) -> dict[str, str]:
    """`{ref: sha}` for every shelf on origin. One network call per clone."""
    out = git_out(["ls-remote", REMOTE, f"{REF_ROOT}/*"], clone)
    shelves: dict[str, str] = {}
    for line in out.splitlines():
        sha, _, ref = line.partition("\t")
        if ref:
            shelves[ref] = sha
    return shelves


@dataclass(slots=True, frozen=True)
class Shelf:
    clone: pathlib.Path
    ref: str
    sha: str
    subject: str
    trailers: Trailers
    #: False when the marker carries no file changes (a clean worktree's commits).
    snapshot: bool

    @property
    def name(self) -> str:
        """The shelf name — the ref with `shelved/<user>/` stripped."""
        return self.ref.removeprefix(REF_ROOT + "/").split("/", 1)[-1]

    def to_json(self) -> dict[str, JsonValue]:
        t = self.trailers
        every = {"branch": t.branch, "base": t.base, "path": t.path, "pr": t.pr, "at": t.at, "by": t.by}
        trailers: dict[str, JsonValue] = {key: value for key, value in every.items() if value}
        return {
            "clone": str(self.clone),
            "ref": self.ref,
            "sha": self.sha,
            "name": self.name,
            "subject": self.subject,
            "snapshot": self.snapshot,
            **trailers,
        }


def list_shelves(clone: pathlib.Path, *, fetch: bool = True) -> list[Shelf]:
    """Every shelf on origin for *clone*, with its trailers."""
    shelves = remote_shelves(clone)
    if fetch and shelves:
        run_git(["fetch", "-q", REMOTE, f"+{REF_ROOT}/*:refs/remotes/{REMOTE}/shelved/*"], clone)
    found: list[Shelf] = []
    for ref, sha in sorted(shelves.items()):
        body = git_out(["log", "-1", "--format=%B", sha], clone)
        subject = body.splitlines()[0] if body else ""
        tree = git_out(["rev-parse", f"{sha}^{{tree}}"], clone)
        parent_tree = git_out(["rev-parse", f"{sha}^^{{tree}}"], clone)
        found.append(
            Shelf(
                clone=clone,
                ref=ref,
                sha=sha,
                subject=subject,
                trailers=parse_trailers(body),
                snapshot=bool(tree) and tree != parent_tree,
            )
        )
    return found


def prunable(shelf: Shelf, *, merged_prs: Mapping[int, str], default_branch: str = "main") -> str:
    """Why a shelf can go, or "" to keep it.

    *merged_prs* maps each merged PR to the head commit it merged. A merged PR
    counts only when that head holds the shelf's base: commits made after the
    last push were never in the PR, and a squash merge leaves no other trace.

    A snapshot is never prunable: uncommitted work outlives a merged PR as
    often as not (the follow-up someone started before it landed).
    """
    if not shelf.subject.startswith(PREFIX) or shelf.snapshot:
        return ""
    pr = shelf.trailers.pr.lstrip("#")
    base = shelf.trailers.base
    merged_head = merged_prs.get(int(pr), "") if pr.isdigit() else ""
    if (
        base
        and merged_head
        and (base == merged_head or run_git(["merge-base", "--is-ancestor", base, merged_head], shelf.clone).ok)
    ):
        return f"PR #{pr} merged"
    if base and run_git(["merge-base", "--is-ancestor", base, f"{REMOTE}/{default_branch}"], shelf.clone).ok:
        return f"base is in {default_branch}"
    return ""


def drop(shelf: Shelf) -> Done:
    """Delete a shelf from the remote, only if it still points where we read it."""
    return run_git(["push", "-q", f"--force-with-lease={shelf.ref}:{shelf.sha}", REMOTE, f":{shelf.ref}"], shelf.clone)


def unshelve(shelf: Shelf, *, path: pathlib.Path | None = None) -> Done:
    """Recreate the worktree and put the snapshot back as unstaged changes; detail is its path."""
    clone = shelf.clone
    if not shelf.subject.startswith(PREFIX):
        return Done(ok=False, detail=f"{shelf.ref} tip is not a {PREFIX} commit; refusing to guess")
    target = path or clone / (shelf.trailers.path or f".worktrees/{shelf.name}")
    if target.exists():
        return Done(ok=False, detail=f"{target} already exists")
    branch = shelf.trailers.branch
    base = f"{shelf.sha}^"

    if branch and git_out(["rev-parse", "--verify", "-q", f"refs/heads/{branch}"], clone):
        # Only fast-forward an existing branch; anything else would discard commits.
        if not run_git(["merge-base", "--is-ancestor", branch, base], clone).ok:
            return Done(
                ok=False,
                detail=f"local branch {branch} has commits the shelf lacks; restore with --path and a new branch",
            )
        run_git(["branch", "-f", branch, base], clone)
        added = run_git(["worktree", "add", str(target), branch], clone)
    elif branch:
        added = run_git(["worktree", "add", "-b", branch, str(target), base], clone)
    else:
        added = run_git(["worktree", "add", "--detach", str(target), base], clone)
    if not added.ok:
        return Done(ok=False, detail=f"worktree add: {added.detail}")

    if shelf.snapshot:
        # Check out the snapshot's files over the base without moving HEAD, then
        # unstage: changes come back as working-tree edits. Deletions included.
        restored = run_git(["read-tree", "-u", "--reset", shelf.sha], target)
        if restored.ok:
            restored = run_git(["reset", "-q", "--mixed", "HEAD"], target)
        if not restored.ok:
            return Done(ok=False, detail=f"restoring snapshot into {target}: {restored.detail}")
    return Done(ok=True, detail=str(target))
