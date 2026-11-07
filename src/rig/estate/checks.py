"""Read-only inspection of one checkout's local git hygiene.

Split from the actuator on purpose: everything here reads, and
`cleanup.py` is the only place that deletes.

Two facts drive the design.

**Merged-ness cannot be answered locally.** Under a squash-merge policy a
branch's commits never become ancestors of `main`, so `git branch --merged`
reports nothing — `repo_governance.branch_cleanup` measured zero on a repo with
36 merged PRs. So the cheap checks here never claim a branch is merged. They
report the signals git *can* prove: an upstream marked `gone`, and true ancestry.
Asking GitHub is opt-in, and lives in the CLI.

**A worktree is not a directory.** The convention puts them at
`<repo>/temp/<name>`, which is also where scratch files live, so temp cleanup and
worktree cleanup overlap on the same paths. Every temp entry is therefore
classified as a registered worktree or not, and a worktree is only ever removed
through `git worktree remove`.
"""

from __future__ import annotations

import datetime as dt
import os
import pathlib
from collections.abc import Mapping
from dataclasses import dataclass, field
from enum import StrEnum

from rig import proc, tracing

DEFAULT_STALE_AFTER = dt.timedelta(days=14)


class Severity(StrEnum):
    """How a finding should be treated."""

    ERROR = "error"
    WARN = "warn"
    INFO = "info"


@dataclass(frozen=True)
class Finding:
    """One piece of local git hygiene worth a human's attention."""

    check: str
    severity: Severity
    subject: str
    message: str
    #: The command that resolves it, when there is a single safe one.
    fix: str = ""
    #: True when `cleanup.py` is willing to act on it under `--apply`.
    actionable: bool = False


# ---------------------------------------------------------------------------
# worktrees
# ---------------------------------------------------------------------------


@dataclass(frozen=True)
class Worktree:
    """One entry from `git worktree list --porcelain`."""

    path: pathlib.Path
    head: str = ""
    branch: str = ""
    detached: bool = False
    bare: bool = False
    locked: bool = False
    lock_reason: str = ""
    prunable: bool = False
    prune_reason: str = ""
    is_main: bool = False

    @property
    def name(self) -> str:
        return self.path.name


@dataclass(slots=True)
class _PorcelainRecord:
    """One `git worktree list --porcelain` stanza as it accumulates line by line."""

    worktree: str | None = None
    head: str = ""
    branch: str = ""
    detached: bool = False
    bare: bool = False
    locked: str | None = None
    prunable: str | None = None

    def take(self, key: str, value: str) -> None:
        match key:
            case "worktree":
                self.worktree = value
            case "HEAD":
                self.head = value
            case "branch":
                self.branch = value
            case "detached":
                self.detached = True
            case "bare":
                self.bare = True
            case "locked":
                self.locked = value
            case "prunable":
                self.prunable = value
            case _:
                pass


def parse_worktrees(porcelain: str) -> list[Worktree]:
    """Parse `git worktree list --porcelain`. Pure, so it is testable offline.

    The first record is always the main worktree — the checkout itself — which
    matters because several checks apply only to the others.
    """
    out: list[Worktree] = []
    current = _PorcelainRecord()

    def flush() -> None:
        if current.worktree is not None:
            out.append(
                Worktree(
                    path=pathlib.Path(current.worktree),
                    head=current.head,
                    branch=current.branch.removeprefix("refs/heads/"),
                    detached=current.detached,
                    bare=current.bare,
                    locked=current.locked is not None,
                    lock_reason=current.locked or "",
                    prunable=current.prunable is not None,
                    prune_reason=current.prunable or "",
                    is_main=not out,
                )
            )

    for line in porcelain.splitlines():
        if not line.strip():
            flush()
            current = _PorcelainRecord()
            continue
        key, _, value = line.partition(" ")
        current.take(key, value)
    flush()
    return out


def list_worktrees(repo: pathlib.Path) -> list[Worktree]:
    """Return the worktrees registered against *repo*."""
    return parse_worktrees(proc.git_out("worktree", "list", "--porcelain", cwd=repo))


def is_dirty(path: pathlib.Path) -> bool:
    """Return True if *path* has uncommitted changes, staged or not."""
    return bool(proc.git_out("status", "--porcelain", cwd=path))


def unpushed_count(path: pathlib.Path) -> int:
    """Return commits on HEAD not present on its upstream. 0 when no upstream.

    Kept for callers that want "ahead of upstream", but **not the right measure
    of risk** — see `orphaned_count`.
    """
    out = proc.git_out("rev-list", "--count", "@{upstream}..HEAD", cwd=path)
    return int(out) if out.isdigit() else 0


def orphaned_count(path: pathlib.Path) -> int:
    """Return commits reachable from HEAD and present on no remote.

    This is the measure that means "would be lost". `unpushed_count` is not:
    a worktree tracking `origin/main` after its own branch was squash-merged
    reports its original commits as "ahead" *forever*, even though every one of
    them still exists on a remote ref. Measured on `design-system`,
    `temp/feat-headless-table` reported 4 ahead and 0 orphaned — a refusal for
    nothing.
    """
    out = proc.git_out("rev-list", "--count", "HEAD", "--not", "--remotes", cwd=path)
    return int(out) if out.isdigit() else 0


def check_worktrees(
    repo: pathlib.Path,
    *,
    temp_dir: pathlib.Path | None = None,
    merged_heads: Mapping[str, int] | None = None,
) -> list[Finding]:
    """Report worktree problems: finished, prunable, dirty, detached, locked, off-convention.

    *merged_heads* is what makes "finished" answerable. Age is a poor proxy:
    measured on `design-system`, 3 of 6 worktrees held branches that had
    already merged, and none was old enough or broken enough for any other
    check to fire. A worktree exists to carry a branch, so the branch landing
    is what finishes it.
    """
    temp = temp_dir or (repo / "temp")
    findings: list[Finding] = []

    for tree in list_worktrees(repo):
        subject = str(tree.path)

        if tree.prunable:
            reason = tree.prune_reason or "its directory is gone"
            findings.append(
                Finding(
                    check="worktree-prunable",
                    severity=Severity.WARN,
                    subject=subject,
                    message=f"Registered but {reason}.",
                    fix="git worktree prune",
                    actionable=True,
                )
            )
            continue  # nothing else can be read from a path that is not there

        if tree.locked:
            findings.append(
                Finding(
                    check="worktree-locked",
                    severity=Severity.INFO,
                    subject=subject,
                    message=f"Locked{f': {tree.lock_reason}' if tree.lock_reason else ''}.",
                    fix=f"git worktree unlock {tree.path}",
                )
            )

        if is_dirty(tree.path):
            findings.append(
                Finding(
                    check="worktree-dirty",
                    severity=Severity.ERROR,
                    subject=subject,
                    message="Uncommitted changes. Nothing will remove this until they are dealt with.",
                    fix=f"cd {tree.path} && git status",
                )
            )

        orphaned = orphaned_count(tree.path)
        if orphaned:
            findings.append(
                Finding(
                    check="worktree-unpushed",
                    severity=Severity.ERROR,
                    subject=subject,
                    message=f"{orphaned} commit(s) on no remote — they exist only here.",
                    fix=f"cd {tree.path} && git push",
                )
            )

        if tree.detached and not tree.bare:
            findings.append(
                Finding(
                    check="worktree-detached",
                    severity=Severity.WARN,
                    subject=subject,
                    message=f"Detached at {tree.head[:8]} — commits here are reachable from nothing.",
                )
            )

        if not tree.is_main and tree.branch and (merged_heads or {}).get(tree.branch) is not None:
            pr = (merged_heads or {})[tree.branch]
            findings.append(
                Finding(
                    check="worktree-merged",
                    severity=Severity.INFO,
                    subject=subject,
                    message=f"Holds `{tree.branch}`, merged in #{pr}. The worktree is finished.",
                    fix=f"cd {repo} && git worktree remove {tree.path}",
                    actionable=True,
                )
            )

        if not tree.is_main and temp not in tree.path.parents:
            findings.append(
                Finding(
                    check="worktree-off-convention",
                    severity=Severity.INFO,
                    subject=subject,
                    message=f"Outside `{temp.name}/`, so it is not gitignored and not swept.",
                )
            )

    return findings


# ---------------------------------------------------------------------------
# local branches
# ---------------------------------------------------------------------------


@dataclass(frozen=True)
class LocalBranch:
    """One local branch and what git knows about it."""

    name: str
    upstream: str = ""
    upstream_gone: bool = False
    is_current: bool = False
    checked_out_elsewhere: bool = False


def parse_branches(for_each_ref: str, *, current: str, worktree_branches: set[str]) -> list[LocalBranch]:
    """Parse `git for-each-ref` output. Pure.

    Expects the format string used by `list_branches`: refname, upstream, and
    upstream track state, tab-separated.
    """
    out: list[LocalBranch] = []
    for line in for_each_ref.splitlines():
        if not line.strip():
            continue
        parts = line.split("\t")
        name = parts[0].removeprefix("refs/heads/")
        upstream = parts[1] if len(parts) > 1 else ""
        track = parts[2] if len(parts) > 2 else ""
        out.append(
            LocalBranch(
                name=name,
                upstream=upstream.removeprefix("refs/remotes/"),
                upstream_gone="gone" in track,
                is_current=name == current,
                checked_out_elsewhere=name in worktree_branches and name != current,
            )
        )
    return out


def list_branches(repo: pathlib.Path) -> list[LocalBranch]:
    """Return the local branches of *repo*."""
    fmt = "%(refname)\t%(upstream)\t%(upstream:track)"
    raw = proc.git_out("for-each-ref", f"--format={fmt}", "refs/heads", cwd=repo)
    current = proc.git_out("rev-parse", "--abbrev-ref", "HEAD", cwd=repo)
    elsewhere = {t.branch for t in list_worktrees(repo) if t.branch and not t.is_main}
    return parse_branches(raw, current=current, worktree_branches=elsewhere)


def merged_by_ancestry(repo: pathlib.Path, branch: str, *, base: str = "origin/main") -> bool:
    """Return True if *branch* is an ancestor of *base*.

    True only for merge-commit and fast-forward landings. A squash merge leaves
    no ancestry, which is why a False here means "unknown", not "unmerged".
    """
    return proc.git("merge-base", "--is-ancestor", branch, base, cwd=repo).ok


def check_branches(
    repo: pathlib.Path,
    *,
    protected: frozenset[str] = frozenset({"main", "master"}),
    base: str = "origin/main",
) -> list[Finding]:
    """Report local branches that look finished, using only free git signals."""
    findings: list[Finding] = []

    for branch in list_branches(repo):
        if branch.name in protected or branch.is_current:
            continue

        if branch.upstream_gone:
            findings.append(
                Finding(
                    check="branch-upstream-gone",
                    severity=Severity.WARN,
                    subject=branch.name,
                    message=f"Tracked `{branch.upstream}`, which no longer exists — the remote branch was deleted.",
                    fix=f"cd {repo} && git branch -D {branch.name}",
                    actionable=not branch.checked_out_elsewhere,
                )
            )
            continue

        if merged_by_ancestry(repo, branch.name, base=base):
            findings.append(
                Finding(
                    check="branch-merged",
                    severity=Severity.WARN,
                    subject=branch.name,
                    message=f"An ancestor of `{base}`, so its work has landed.",
                    fix=f"cd {repo} && git branch -d {branch.name}",
                    actionable=not branch.checked_out_elsewhere,
                )
            )
        elif not branch.upstream:
            findings.append(
                Finding(
                    check="branch-local-only",
                    severity=Severity.INFO,
                    subject=branch.name,
                    message="No upstream, and no ancestry in the base. Never removed automatically.",
                )
            )

    return findings


# ---------------------------------------------------------------------------
# the temp directory
# ---------------------------------------------------------------------------


@dataclass(frozen=True)
class TempEntry:
    """One thing inside the repo's scratch directory."""

    path: pathlib.Path
    bytes_used: int
    modified: dt.datetime
    is_worktree: bool = False

    def age(self, *, now: dt.datetime | None = None) -> dt.timedelta:
        return (now or dt.datetime.now(dt.UTC)) - self.modified


def _dir_size(path: pathlib.Path) -> int:
    """Total bytes under *path*. Symlinks are counted as themselves, not followed."""
    if path.is_file() or path.is_symlink():
        try:
            return path.lstat().st_size
        except OSError:
            return 0
    # scandir, not rglob: on Windows each entry carries its size from the directory
    # listing, where rglob costs three calls per file -- 40+ minutes over a 56 GB temp/.
    total = 0
    stack = [str(path)]
    while stack:
        try:
            entries = list(os.scandir(stack.pop()))
        except OSError:
            continue
        for entry in entries:
            try:
                if entry.is_dir(follow_symlinks=False) and not entry.is_junction():
                    stack.append(entry.path)
                else:
                    total += entry.stat(follow_symlinks=False).st_size
            except OSError:
                continue
    return total


def scan_temp(repo: pathlib.Path, *, temp_dir: pathlib.Path | None = None) -> list[TempEntry]:
    """Return the immediate contents of the scratch directory, sized and dated."""
    temp = temp_dir or (repo / "temp")
    if not temp.is_dir():
        return []
    registered = {t.path.resolve() for t in list_worktrees(repo) if not t.is_main}

    entries: list[TempEntry] = []
    for child in sorted(temp.iterdir()):
        try:
            stat = child.lstat()
        except OSError:
            continue
        entries.append(
            TempEntry(
                path=child,
                bytes_used=_dir_size(child),
                modified=dt.datetime.fromtimestamp(stat.st_mtime, tz=dt.UTC),
                is_worktree=child.resolve() in registered,
            )
        )
    return entries


def check_temp(
    repo: pathlib.Path,
    *,
    temp_dir: pathlib.Path | None = None,
    stale_after: dt.timedelta = DEFAULT_STALE_AFTER,
    now: dt.datetime | None = None,
    entries: list[TempEntry] | None = None,
) -> list[Finding]:
    """Report the scratch directory: whether it is ignored, and what has gone stale.

    Pass *entries* from an earlier `scan_temp` to avoid walking the tree twice.
    """
    temp = temp_dir or (repo / "temp")
    findings: list[Finding] = []
    if not temp.is_dir():
        return findings

    if not _is_ignored(repo, temp):
        findings.append(
            Finding(
                check="temp-not-ignored",
                severity=Severity.ERROR,
                subject=str(temp),
                message="Exists but is not gitignored, so scratch files can be committed by accident.",
                fix=f"echo '{temp.name}/' >> {repo / '.gitignore'}",
            )
        )

    for entry in entries if entries is not None else scan_temp(repo, temp_dir=temp):
        if entry.age(now=now) < stale_after:
            continue
        days = entry.age(now=now).days
        if entry.is_worktree:
            findings.append(
                Finding(
                    check="temp-stale-worktree",
                    severity=Severity.INFO,
                    subject=str(entry.path),
                    message=f"A registered worktree, untouched for {days} days ({_human(entry.bytes_used)}).",
                    fix=f"cd {repo} && git worktree remove {entry.path}",
                    actionable=True,
                )
            )
        else:
            findings.append(
                Finding(
                    check="temp-stale",
                    severity=Severity.INFO,
                    subject=str(entry.path),
                    message=f"Scratch, untouched for {days} days ({_human(entry.bytes_used)}).",
                    fix=f"rm -rf {entry.path}",
                    actionable=True,
                )
            )
    return findings


def _is_ignored(repo: pathlib.Path, path: pathlib.Path) -> bool:
    """Return True if git ignores *path*."""
    ran = proc.git("check-ignore", "-q", str(path), cwd=repo, timeout=15)
    return ran.ok or ran.code in {proc.NOT_FOUND, proc.TIMED_OUT}  # cannot tell; do not cry wolf


def _human(size: int) -> str:
    """Format bytes for a human, at one decimal place."""
    value = float(size)
    for unit in ("B", "KB", "MB", "GB"):
        if value < 1024 or unit == "GB":
            return f"{value:.0f}{unit}" if unit == "B" else f"{value:.1f}{unit}"
        value /= 1024
    return f"{value:.1f}GB"


# ---------------------------------------------------------------------------
# repo-level state
# ---------------------------------------------------------------------------


def check_repo_state(repo: pathlib.Path) -> list[Finding]:
    """Report stashes, a detached HEAD, and common gitignore drift on the checkout itself."""
    findings: list[Finding] = []

    stashes = [line for line in proc.git_out("stash", "list", cwd=repo).splitlines() if line.strip()]
    if stashes:
        findings.append(
            Finding(
                check="stash-entries",
                severity=Severity.WARN,
                subject=str(repo),
                message=(
                    f"{len(stashes)} stash entr{'y' if len(stashes) == 1 else 'ies'}. "
                    "Every worktree shares one stash stack, so a bare `git stash pop` "
                    "can restore another session's work into this tree."
                ),
                fix=f"cd {repo} && git stash list",
            )
        )

    if proc.git_out("rev-parse", "--abbrev-ref", "HEAD", cwd=repo) == "HEAD":
        findings.append(
            Finding(
                check="detached-head",
                severity=Severity.WARN,
                subject=str(repo),
                message="Detached HEAD in the main checkout.",
            )
        )

    seen_files: list[os.stat_result] = []
    for todo_name in ("TODO.md", "todo.md"):
        todo_file = repo / todo_name
        if todo_file.is_file():
            try:
                st = todo_file.stat()
                if any(os.path.samestat(st, seen) for seen in seen_files):
                    continue
                seen_files.append(st)
            except OSError:
                pass

            if not _is_ignored(repo, todo_file):
                findings.append(
                    Finding(
                        check="todo-not-ignored",
                        severity=Severity.WARN,
                        subject=todo_name,
                        message=(
                            f"Local task file `{todo_name}` exists but is not gitignored, "
                            "so it can be committed by accident."
                        ),
                        fix=f"echo '{todo_name}' >> {repo / '.gitignore'}",
                    )
                )

    return findings


# ---------------------------------------------------------------------------
# orchestration
# ---------------------------------------------------------------------------


def _no_findings() -> list[Finding]:
    """Typed factory — a bare ``list`` resolves as ``list[Unknown]``."""
    return []


@dataclass
class RepoReport:
    """Everything the read-only checks found in one repo."""

    repo: pathlib.Path
    findings: list[Finding] = field(default_factory=_no_findings)
    temp_bytes: int = 0
    worktree_count: int = 0
    branch_count: int = 0

    @property
    def actionable(self) -> list[Finding]:
        return [f for f in self.findings if f.actionable]

    @property
    def blocked(self) -> bool:
        """True when something dirty or unpushed makes cleanup unsafe."""
        return any(f.severity is Severity.ERROR for f in self.findings)


def inspect_repo(
    repo: pathlib.Path,
    *,
    merged_heads: Mapping[str, int] | None = None,
    stale_after: dt.timedelta = DEFAULT_STALE_AFTER,
    protected: frozenset[str] = frozenset({"main", "master"}),
    base: str = "origin/main",
    now: dt.datetime | None = None,
) -> RepoReport:
    """Run every read-only check over *repo*, each phase its own span."""
    report = RepoReport(repo=repo)
    attrs: dict[str, tracing.AttributeValue] = {"estate.clone": str(repo)}
    with tracing.span("estate.inspect_repo", scope="rig.estate", **attrs) as s:
        with tracing.span("estate.check_worktrees", scope="rig.estate"):
            report.findings.extend(check_worktrees(repo, merged_heads=merged_heads))
        with tracing.span("estate.check_branches", scope="rig.estate"):
            report.findings.extend(check_branches(repo, protected=protected, base=base))
        with tracing.span("estate.check_temp", scope="rig.estate"):
            temp_entries = scan_temp(repo)
            report.findings.extend(check_temp(repo, stale_after=stale_after, now=now, entries=temp_entries))
        with tracing.span("estate.check_repo_state", scope="rig.estate"):
            report.findings.extend(check_repo_state(repo))
        report.temp_bytes = sum(entry.bytes_used for entry in temp_entries)
        report.worktree_count = max(len(list_worktrees(repo)) - 1, 0)
        report.branch_count = len(list_branches(repo))
        s.set(**{"estate.findings": len(report.findings), "estate.worktrees": report.worktree_count})
    return report
