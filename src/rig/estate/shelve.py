"""Resolve a worktree that `tidy` refuses: push what only exists here, then remove it.

`tidy` refuses a worktree holding uncommitted changes or commits on no remote,
and it is right to. Measured on this estate: 59 of 299 registered worktrees were
in that state, and nothing would ever clear them, because the only way to make
one safe was by hand. Shelving does that by hand-work's rules, mechanically:

1. **Snapshot without touching the branch.** The working tree, untracked files
   included, is staged into a *temporary* index and written with `commit-tree`
   on top of HEAD. No hook runs, the real index is untouched and the PR branch
   does not move, so an open PR never sees the snapshot.
2. **Mark it.** Every shelf tip is one commit whose subject starts with
   `TEMP-SHELVED:` and whose trailers record the branch, base, path and PR.
   A clean worktree with local-only commits gets the same marker with an
   unchanged tree, so "what was shelved, from where" is answered by the ref
   alone, and `unshelve` always drops exactly one commit.
3. **Push to a side ref.** `refs/heads/shelved/<user>/<name>`, never the PR
   branch (unless `--to-upstream`, and then only the real commits).
4. **Prove it before deleting.** The remote must report the pushed SHA and the
   commit must be on a remote-tracking ref. Only then is the worktree removed,
   through `git worktree remove`, and its branch deleted if every commit on it
   is on a remote.

A worktree that needs a person's judgement is not shelved; its plan carries
`decision_needed` instead. That covers what a snapshot would silently lose or
leak: ignored files that do not regenerate (an `.env`), anything too big to
push, a shelf already on the remote holding something else, and a tree touched
recently enough to be a live session.
"""

from __future__ import annotations

import datetime as dt
import json
import os
import pathlib
import re
import tempfile
from collections.abc import Mapping, Sequence
from dataclasses import asdict, dataclass, field

from rig import proc
from rig.estate.checks import Worktree, list_worktrees
from rig.estate.reclaim import CATEGORIES
from rig.estate.shelf_store import PREFIX, REF_ROOT, REMOTE, Done, git_out, run_git
from rig.estate.trim import long_path, remove_tree
from rig.typed_json import JsonValue

LEDGER = pathlib.Path.home() / ".estate-shelved.jsonl"

#: GitHub rejects files over 100 MB; well before that, a snapshot is the wrong home.
BIG_FILE = 50 * 1024 * 1024
DEFAULT_IDLE = dt.timedelta(hours=2)

#: Ignored paths that come back from a command (or were never worth keeping), so
#: losing them costs nothing. Beyond the reclaim catalogue, these are what a
#: first estate run found blocking 158 of 302 plans: husky's generated hooks,
#: wireit's cache, NuGet/paket restores, SpecFlow's `*.feature.cs`, and logs.
#: `temp` is excluded: inside a worktree it mixes run outputs with hand-written
#: scripts and analyses, so it stays a person's call.
REGENERABLE = frozenset(g for c in CATEGORIES if c.name != "temp" for g in c.globs) | {
    ".DS_Store",
    "Thumbs.db",
    ".wireit",
    "packages",
    "paket-files",
    "TestResults",
    "logs",
}
#: Directories that only ever hold generated content, so anything below them
#: regenerates. `packages`, `build`, `bin` and the like are not here: they are
#: also source folders (a pnpm monorepo's `packages/<pkg>/.env`).
CACHE_DIRS = frozenset(
    {
        "node_modules",
        ".venv",
        "venv",
        "__pycache__",
        ".pytest_cache",
        ".ruff_cache",
        ".mypy_cache",
        ".next",
        ".turbo",
        ".wireit",
        "paket-files",
        "TestResults",
    }
)
REGENERABLE_FILES = frozenset({".DS_Store", "Thumbs.db"})
REGENERABLE_SUFFIXES = (".pyc", ".egg-info", ".tsbuildinfo", ".log", ".feature.cs")
REGENERABLE_PATHS = (".husky/_",)

REMOVE = "remove"
PUSH = "push+remove"
SNAPSHOT = "snapshot+remove"

#: Convention dirs that hold worktrees; stripped from the shelf name.
_CONTAINERS = (".worktrees", "worktrees")


# ---------------------------------------------------------------------------
# naming and the commit message
# ---------------------------------------------------------------------------


def _ref_component(part: str) -> str:
    """Make one path segment a legal ref component."""
    cleaned = re.sub(r"[\s~^:?*\[\\\x00-\x1f\x7f]+", "-", part)
    cleaned = cleaned.replace("..", "-").replace("@{", "-").lstrip(".").rstrip(".")
    cleaned = cleaned.removesuffix(".lock")
    return cleaned or "-"


def shelf_user(repo: pathlib.Path) -> str:
    """The namespace segment: the local part of the committer email."""
    email = git_out(["config", "user.email"], repo)
    return _ref_component(email.split("@", 1)[0] or os.environ.get("USERNAME", "") or "unknown")


def shelf_name(clone: pathlib.Path, path: pathlib.Path) -> str:
    """Name a shelf after the worktree's path inside the clone.

    `.worktrees/` is the convention, so it is dropped; anything else (`temp/x`)
    stays, so two worktrees with the same leaf in different places do not
    collide. A worktree outside the clone is named by its leaf.
    """
    try:
        parts = list(path.resolve().relative_to(clone.resolve()).parts)
    except ValueError:
        parts = [path.name]
    if parts and parts[0] in _CONTAINERS and len(parts) > 1:
        parts = parts[1:]
    return "/".join(_ref_component(p) for p in parts)


def shelf_ref(user: str, name: str) -> str:
    return f"{REF_ROOT}/{user}/{name}"


def message(*, repo: str, name: str, branch: str, base: str, path: str, pr: int | None, at: dt.datetime) -> str:
    """The marker commit's message. The subject prefix is the contract."""
    where = branch or f"detached@{base[:7]}"
    lines = [
        f"{PREFIX} {repo}/{name} ({where})",
        "",
        "Set aside by `estate shelve` so the worktree could be removed. Never merge this.",
        f"Restore with: estate unshelve {name} --repo <clone of {repo}>",
        "",
    ]
    if branch:
        lines.append(f"Shelved-Branch: {branch}")
    lines += [f"Shelved-Base: {base}", f"Shelved-Path: {path}"]
    if pr is not None:
        lines.append(f"Shelved-PR: #{pr}")
    lines += [f"Shelved-At: {at.isoformat(timespec='seconds')}", "Shelved-By: estate shelve"]
    return "\n".join(lines) + "\n"


# ---------------------------------------------------------------------------
# inspection
# ---------------------------------------------------------------------------


@dataclass(slots=True, frozen=True)
class Porcelain:
    """`git status --porcelain -z`, split into tracked changes and untracked paths."""

    changed: list[str]
    untracked: list[str]


@dataclass(slots=True, frozen=True)
class SizedFile:
    path: str
    size: int


@dataclass(slots=True, frozen=True)
class PrRef:
    """The PR a branch heads: its number and GitHub state (`OPEN`, `MERGED`, `CLOSED`)."""

    number: int
    state: str


@dataclass(slots=True, frozen=True)
class Status:
    """What `git status` and `ls-files` say about one worktree."""

    changed: list[str]
    untracked: list[str]
    ignored: list[str]


def parse_status(porcelain_z: str) -> Porcelain:
    """Split `status --porcelain -z` into changed and untracked paths. Pure.

    A rename carries its source as an extra NUL-separated field, which a
    line-based parse would read as a separate entry.
    """
    changed: list[str] = []
    untracked: list[str] = []
    fields = porcelain_z.split("\0")
    i = 0
    while i < len(fields):
        entry = fields[i]
        i += 1
        if len(entry) < 4:
            continue
        code, path = entry[:2], entry[3:]
        if code == "??":
            untracked.append(path)
        else:
            changed.append(path)
            if code[0] in "RC":
                i += 1
    return Porcelain(changed=changed, untracked=untracked)


def read_status(path: pathlib.Path) -> Status:
    raw = proc.git("status", "--porcelain", "-z", "--untracked-files=all", cwd=path).out
    porcelain = parse_status(raw)
    ignored = [
        p
        for p in proc.git(
            "ls-files", "-z", "--others", "--ignored", "--exclude-standard", "--directory", cwd=path
        ).out.split("\0")
        if p
    ]
    return Status(changed=porcelain.changed, untracked=porcelain.untracked, ignored=ignored)


def is_regenerable(path: str) -> bool:
    """True when an ignored path is a build output or cache by name.

    A wholly ignored directory (listed with a trailing `/`) counts by its own
    name. A file counts by its name or suffix, or when it sits under a
    `CACHE_DIRS` directory: `packages/api/.env` is not output.
    """
    normal = path.replace("\\", "/")
    parts = [p for p in normal.split("/") if p]
    if not parts:
        return False
    joined = "/".join(parts)
    *parents, last = parts
    return (
        (normal.endswith("/") and last in REGENERABLE)
        or last in REGENERABLE_FILES
        or any(p.endswith(REGENERABLE_SUFFIXES) for p in parts)
        or any(p in CACHE_DIRS for p in parents)
        or any(joined == r or joined.startswith(r + "/") or f"/{r}/" in f"/{joined}/" for r in REGENERABLE_PATHS)
    )


def _files(root: pathlib.Path) -> list[SizedFile]:
    """Every file under *root* (or *root* itself) with its size. scandir, no junctions."""
    if root.is_file():
        return [SizedFile(path=str(root), size=root.stat().st_size)]
    found: list[SizedFile] = []
    stack = [str(root)]
    while stack:
        try:
            entries = list(os.scandir(stack.pop()))
        except OSError:
            continue
        for entry in entries:
            try:
                if entry.is_dir(follow_symlinks=False) and not entry.is_junction():
                    stack.append(entry.path)
                elif entry.is_file(follow_symlinks=False):
                    found.append(SizedFile(path=entry.path, size=entry.stat(follow_symlinks=False).st_size))
            except OSError:
                continue
    return found


def nested_repos(clone: pathlib.Path, path: pathlib.Path, tops: Sequence[str]) -> list[str]:
    """Other repositories inside this worktree, which removing it would destroy.

    Registered worktrees of the same clone are checked directly; a walk of the
    ignored and untracked paths finds any other `.git` (a nested clone, or a
    worktree of another clone). `git worktree remove` checks for neither.
    """
    own = path.resolve()
    found = [
        str(t.path) for t in list_worktrees(clone) if t.path.resolve() != own and t.path.resolve().is_relative_to(own)
    ]
    for top in tops:
        stack = [str(path / top.rstrip("/"))]
        while stack:
            current = stack.pop()
            try:
                entries = list(os.scandir(current))
            except OSError:
                continue
            for entry in entries:
                if entry.name == ".git":
                    found.append(str(pathlib.Path(current)))
                    break
                try:
                    if entry.is_dir(follow_symlinks=False) and not entry.is_junction():
                        stack.append(entry.path)
                except OSError:
                    continue
    return sorted({str(pathlib.Path(f).resolve()) for f in found})


def last_activity(worktree: pathlib.Path, status: Status) -> dt.datetime | None:
    """The newest mtime among the signals a live session would touch.

    Not a full walk: the worktree's index, HEAD and reflog move on every git
    operation, and every changed or untracked file is listed by status. That is
    enough to see an agent mid-session without stat-ing a `node_modules`.
    """
    gitdir = git_out(["rev-parse", "--absolute-git-dir"], worktree)
    candidates = [worktree]
    if gitdir:
        g = pathlib.Path(gitdir)
        candidates += [g / "index", g / "HEAD", g / "logs" / "HEAD"]
    candidates += [worktree / p for p in (status.changed + status.untracked)[:2000]]
    newest = 0.0
    for c in candidates:
        try:
            newest = max(newest, c.stat().st_mtime)
        except OSError:
            continue
    return dt.datetime.fromtimestamp(newest, tz=dt.UTC) if newest else None


@dataclass
class Plan:
    """What shelving one worktree would do, and why — or why it needs a person."""

    clone: pathlib.Path
    path: pathlib.Path
    name: str
    ref: str
    branch: str
    head: str
    action: str
    orphaned: int = 0
    changed: int = 0
    untracked: int = 0
    pr: int | None = None
    pr_state: str = ""
    reasons: list[str] = field(default_factory=list[str])
    decision_needed: list[str] = field(default_factory=list[str])
    #: Ignored paths the snapshot will carry (`--include-ignored`).
    carry_ignored: list[str] = field(default_factory=list[str])
    #: Ignored paths that do not regenerate, and their total size.
    keep_ignored: list[str] = field(default_factory=list[str])
    keep_ignored_bytes: int = 0
    #: The shelf already on the remote, if any.
    existing: str = ""
    upstream: str = ""

    def to_json(self) -> dict[str, JsonValue]:
        data = asdict(self)
        data["clone"] = str(self.clone)
        data["path"] = str(self.path)
        return data


@dataclass(frozen=True)
class Options:
    idle: dt.timedelta = DEFAULT_IDLE
    include_ignored: bool = False
    discard_ignored: bool = False
    to_upstream: bool = False


def plan_worktree(
    clone: pathlib.Path,
    tree: Worktree,
    *,
    user: str,
    shelves: Mapping[str, str],
    prs: Mapping[str, PrRef],
    options: Options,
    now: dt.datetime | None = None,
) -> Plan:
    """Decide how one worktree would be resolved. Reads only; never writes."""
    now = now or dt.datetime.now(dt.UTC)
    path = tree.path
    name = shelf_name(clone, path)
    ref = shelf_ref(user, name)
    head = tree.head or git_out(["rev-parse", "HEAD"], path)
    status = read_status(path)
    orphaned_out = git_out(["rev-list", "--count", "HEAD", "--not", "--remotes"], path)
    orphaned = int(orphaned_out) if orphaned_out.isdigit() else 0

    plan = Plan(
        clone=clone,
        path=path,
        name=name,
        ref=ref,
        branch=tree.branch,
        head=head,
        action=REMOVE,
        orphaned=orphaned,
        changed=len(status.changed),
        untracked=len(status.untracked),
        existing=shelves.get(ref, ""),
    )
    pr = prs.get(tree.branch)
    if pr is not None:
        plan.pr, plan.pr_state = pr.number, pr.state

    if status.changed or status.untracked:
        plan.action = SNAPSHOT
        plan.reasons.append(f"{len(status.changed)} changed, {len(status.untracked)} untracked")
    elif orphaned:
        plan.action = PUSH
        plan.reasons.append(f"{orphaned} commit(s) on no remote")
    else:
        plan.reasons.append("clean, every commit on a remote")

    if plan.action == PUSH and plan.pr_state == "MERGED":
        # Squash-stranded: the work landed, so there is nothing to keep. Same
        # authority `tidy --merged` uses.
        plan.action = REMOVE
        plan.reasons.append(f"squash-merged in #{plan.pr}")

    if plan.action != REMOVE and not shares_history(path):
        # No flag clears this: a clone made before its remote was restarted would push
        # the history the restart dropped. Rebase onto the remote's default branch first.
        plan.decision_needed.append(
            f"shares no history with {REMOTE}'s default branch; a shelf would push history "
            + f"{REMOTE} does not have. Rebase onto {REMOTE} first"
        )

    if options.to_upstream and plan.action != REMOVE and tree.branch:
        upstream = git_out(["rev-parse", "--abbrev-ref", f"{tree.branch}@{{upstream}}"], clone)
        if upstream.startswith(f"{REMOTE}/") and plan.pr_state != "MERGED":
            plan.upstream = upstream
        else:
            plan.reasons.append("--to-upstream: no live upstream, shelf ref only")

    keep = [p for p in status.ignored if not is_regenerable(p)]
    nested = nested_repos(clone, path, [*keep, *(u for u in status.untracked if u.endswith("/"))])
    if nested:
        # No flag clears this: removing the worktree would delete the nested one.
        plan.decision_needed.append(f"holds another repository, move it out first: {', '.join(nested[:3])}")
    kept_files = [f for p in keep for f in _files(path / p)]
    plan.keep_ignored = keep
    plan.keep_ignored_bytes = sum(f.size for f in kept_files)
    if keep:
        weight = f"{len(keep)} ignored path(s), {plan.keep_ignored_bytes / 2**20:,.1f} MB"
        if options.include_ignored:
            plan.carry_ignored = keep
            if plan.action != SNAPSHOT:
                plan.action = SNAPSHOT
            plan.reasons.append(f"{weight} carried")
        elif options.discard_ignored:
            plan.reasons.append(f"{weight} discarded")
        else:
            shown = ", ".join(keep[:5]) + (" …" if len(keep) > 5 else "")
            plan.decision_needed.append(
                f"ignored, not regenerable ({weight}): {shown} — --include-ignored or --discard-ignored"
            )

    big = [p for p in status.untracked if (path / p).is_file() and (path / p).stat().st_size > BIG_FILE]
    if options.include_ignored:
        big += [str(pathlib.Path(f.path).relative_to(path)) for f in kept_files if f.size > BIG_FILE]
    if big:
        plan.decision_needed.append(f"over {BIG_FILE // 2**20} MB: {', '.join(big[:5])}")

    if tree.locked:
        plan.decision_needed.append(f"locked{': ' + tree.lock_reason if tree.lock_reason else ''}")

    # `--idle 0` disables the guard outright, including for mtimes ahead of *now*.
    active = last_activity(path, status)
    if options.idle and active and now - active < options.idle:
        minutes = int((now - active).total_seconds() // 60)
        plan.decision_needed.append(f"touched {minutes}m ago — may be a live session (--idle)")

    if plan.existing and plan.action != REMOVE and not _matches(plan, status):
        plan.decision_needed.append(f"{ref} already on {REMOTE} at {plan.existing[:9]} with other content")

    return plan


def shares_history(path: pathlib.Path) -> bool:
    """True unless HEAD and the remote's default branch have no common ancestor.

    A remote with no default branch fetched yet cannot be judged, so it passes.
    """
    for base in (f"refs/remotes/{REMOTE}/HEAD", f"refs/remotes/{REMOTE}/main"):
        if git_out(["rev-parse", "--verify", "-q", base], path):
            return bool(git_out(["merge-base", "HEAD", base], path))
    return True


def _matches(plan: Plan, status: Status) -> bool:
    """True when the shelf already on the remote is this worktree's state.

    Makes a re-run after a failed removal idempotent rather than a conflict.
    """
    return _holds(plan, plan.existing, status)


def _holds(plan: Plan, sha: str, status: Status) -> bool:
    """True when shelf commit *sha* sits on the plan's HEAD and holds the tree as it is now."""
    parent = git_out(["rev-parse", "--verify", "-q", f"{sha}^"], plan.path)
    if parent != plan.head:
        return False
    tree = _snapshot_tree(plan.path, plan.carry_ignored) if status.changed or status.untracked else plan.head
    have = git_out(["rev-parse", f"{sha}^{{tree}}"], plan.path)
    want = git_out(["rev-parse", f"{tree}^{{tree}}"], plan.path)
    return bool(have) and have == want


def _drifted(plan: Plan, sha: str) -> str:
    """Why the worktree no longer matches the shelf, or "" when it still does.

    `--all` plans everything before applying anything, so a worktree can change
    in between. Checked just before the forced remove.
    """
    status = read_status(plan.path)
    head = git_out(["rev-parse", "HEAD"], plan.path)
    if head != plan.head:
        return f"HEAD moved to {head[:9] or '?'} since planning"
    try:
        held = _holds(plan, sha, status)
    except RuntimeError as exc:
        return f"could not re-read the worktree: {exc}"
    if not held:
        return "changed since planning"
    fresh = [p for p in status.ignored if not is_regenerable(p) and p not in plan.keep_ignored]
    if fresh:
        return f"new ignored path(s) since planning: {', '.join(fresh[:5])}"
    return ""


# ---------------------------------------------------------------------------
# acting
# ---------------------------------------------------------------------------


@dataclass(frozen=True)
class Result:
    plan: Plan
    outcome: str
    detail: str = ""
    sha: str = ""

    def to_json(self) -> dict[str, JsonValue]:
        return {**self.plan.to_json(), "outcome": self.outcome, "detail": self.detail, "sha": self.sha}


def _snapshot_tree(path: pathlib.Path, carry_ignored: Sequence[str]) -> str:
    """Write the working tree as a tree object via a throwaway index.

    The worktree's own index is never read or written, so staged-vs-unstaged
    state is left exactly as it was if anything below fails.
    """
    with tempfile.TemporaryDirectory(prefix="estate-shelve-") as scratch:
        env = {"GIT_INDEX_FILE": str(pathlib.Path(scratch) / "index")}
        steps: list[list[str]] = [["read-tree", "HEAD"], ["add", "-A", "--", "."]]
        if carry_ignored:
            steps.append(["add", "-f", "--", *carry_ignored])
        for step in steps:
            run = run_git(step, path, env=env)
            if not run.ok:
                raise RuntimeError(f"git {' '.join(step[:2])}: {run.detail}")
        tree = run_git(["write-tree"], path, env=env)
        if not tree.ok:
            raise RuntimeError(f"git write-tree: {tree.detail}")
        return tree.detail


def _marker(plan: Plan, now: dt.datetime) -> str:
    """Create the TEMP-SHELVED commit on top of HEAD; return its SHA."""
    tree = _snapshot_tree(plan.path, plan.carry_ignored) if plan.action == SNAPSHOT else f"{plan.head}^{{tree}}"
    text = message(
        repo=plan.clone.name,
        name=plan.name,
        branch=plan.branch,
        base=plan.head,
        path=plan.path.resolve().relative_to(plan.clone.resolve()).as_posix()
        if plan.clone.resolve() in plan.path.resolve().parents
        else str(plan.path),
        pr=plan.pr,
        at=now,
    )
    done = proc.git("commit-tree", tree, "-p", plan.head, "-F", "-", cwd=plan.path, timeout=60, stdin_text=text)
    if not done.ok:
        raise RuntimeError(f"git commit-tree: {done.err.strip()}")
    return done.out.strip()


def _on_remote(clone: pathlib.Path, ref: str, sha: str) -> bool:
    """The proof required before deleting anything: origin holds *sha* at *ref*."""
    listed = run_git(["ls-remote", REMOTE, ref], clone)
    if not listed.ok or listed.detail.split("\t", 1)[0] != sha:
        return False
    run_git(["fetch", "-q", REMOTE, f"+{ref}:refs/remotes/{REMOTE}/{ref.removeprefix('refs/heads/')}"], clone)
    return git_out(["rev-list", "--count", sha, "--not", "--remotes"], clone) == "0"


def sweep_link_skeleton(root: pathlib.Path) -> str:
    """Delete what `git worktree remove` leaves on Windows: pnpm's junctions.

    git deletes every file but not links, so a pnpm worktree survives as
    thousands of junctions in empty directories. Links are unlinked, never
    followed, and the sweep refuses outright if a regular file remains.
    """
    dirs: list[str] = []
    links: list[os.DirEntry[str]] = []
    stack = [long_path(root)]
    try:
        while stack:
            current = stack.pop()
            dirs.append(current)
            for entry in os.scandir(current):
                if entry.is_symlink() or entry.is_junction():
                    links.append(entry)
                elif entry.is_dir(follow_symlinks=False):
                    stack.append(entry.path)
                else:
                    return f", {root} kept: it still holds files"
        for link in links:
            try:
                pathlib.Path(link.path).unlink()
            except OSError:
                pathlib.Path(link.path).rmdir()  # A junction or directory symlink on Windows.
        for directory in reversed(dirs):
            pathlib.Path(directory).rmdir()
    except OSError as exc:
        return f", leftover links not cleared: {exc}"
    return f", {len(links)} leftover link(s) cleared"


def _abandoned_part_way(plan: Plan) -> bool:
    """Whether git unregistered the worktree and then stopped deleting it.

    The plan already settled that nothing in the tree is lost by removing it,
    so finishing the delete is the removal git was asked for.
    """
    trees = list_worktrees(plan.clone)
    if not any(t.is_main for t in trees):
        return False  # git could not list the worktrees, so nothing is known.
    target = plan.path.resolve()
    return plan.path.exists() and not any(t.path.resolve() == target for t in trees)


def _remove(plan: Plan, *, force: bool) -> Done:
    """Remove the worktree, then its branch if nothing on it is local-only."""
    if pathlib.Path.cwd().resolve().is_relative_to(plan.path.resolve()):
        os.chdir(plan.clone)  # Windows will not delete a process's cwd.
    args = ["worktree", "remove", *(["--force"] if force else []), str(plan.path)]
    removed = run_git(args, plan.clone)
    detail = "worktree removed"
    if not removed.ok:
        if not _abandoned_part_way(plan):
            return Done(ok=False, detail=f"worktree remove: {removed.detail}")
        try:
            remove_tree(plan.path)
        except OSError as exc:
            return Done(ok=False, detail=f"worktree remove: {removed.detail}; finishing the delete: {exc}")
        detail = "worktree removed (finished after git stopped part-way)"
    if plan.path.exists():
        detail += sweep_link_skeleton(plan.path)
    if plan.branch:
        local = git_out(["rev-list", "--count", plan.branch, "--not", "--remotes"], plan.clone)
        still_used = any(t.branch == plan.branch for t in list_worktrees(plan.clone))
        if local == "0" and not still_used:
            deleted = run_git(["branch", "-D", plan.branch], plan.clone).ok
            detail += f", branch {plan.branch} deleted" if deleted else f", branch {plan.branch} kept"
        else:
            detail += f", branch {plan.branch} kept"
    return Done(ok=True, detail=detail)


def _ledger(result: Result, ledger: pathlib.Path | None) -> None:
    if ledger is None:
        return
    try:
        with ledger.open("a", encoding="utf-8") as fh:
            fh.write(json.dumps({"at": dt.datetime.now(dt.UTC).isoformat(), **result.to_json()}) + "\n")
    except OSError:
        pass  # The ref and its trailers are the record; the ledger is a convenience.


def apply_plan(plan: Plan, *, now: dt.datetime | None = None, ledger: pathlib.Path | None = LEDGER) -> Result:
    """Carry out one plan. Refuses anything with `decision_needed`."""
    if plan.decision_needed:
        return Result(plan, "needs-decision", "; ".join(plan.decision_needed))
    now = now or dt.datetime.now(dt.UTC)

    if plan.action == REMOVE:
        removed = _remove(plan, force=False)
        result = Result(plan, "removed" if removed.ok else "failed", removed.detail)
        _ledger(result, ledger)
        return result

    try:
        if plan.upstream:
            branch = plan.upstream.removeprefix(f"{REMOTE}/")
            pushed = run_git(["push", "-q", REMOTE, f"{plan.head}:refs/heads/{branch}"], plan.clone)
            if not pushed.ok:
                return Result(plan, "failed", f"push to {plan.upstream} (no force): {pushed.detail}")
        sha = plan.existing or _marker(plan, now)
        if not plan.existing:
            pushed = run_git(["push", "-q", REMOTE, f"{sha}:{plan.ref}"], plan.clone)
            if not pushed.ok:
                return Result(plan, "failed", f"push {plan.ref}: {pushed.detail}", sha)
    except RuntimeError as exc:
        return Result(plan, "failed", str(exc))

    if not _on_remote(plan.clone, plan.ref, sha):
        return Result(plan, "failed", f"{plan.ref} not verified on {REMOTE}; worktree kept", sha)
    if drift := _drifted(plan, sha):
        return Result(plan, "failed", f"{drift}; worktree kept, shelf {plan.ref} pushed", sha)

    # Forced only now: the snapshot holds every change and the remote has it.
    removed = _remove(plan, force=True)
    outcome = "shelved" if removed.ok else "failed"
    result = Result(plan, outcome, f"{plan.ref.removeprefix('refs/heads/')} · {removed.detail}", sha)
    _ledger(result, ledger)
    return result
