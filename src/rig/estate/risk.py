"""What exists on this machine and nowhere else.

This is the only module about loss. Everything else `estate` reports is
friction — branch sprawl, stale worktrees, disk — and all of it regenerates from
a remote or a package manager. The state here does not.

Four kinds, in order of how easily they vanish:

1. **Uncommitted changes.** One `git checkout .` from gone.
2. **Stashes.** Every worktree of a repo shares one stash stack, so a bare
   `git stash pop` in another session can consume an entry from this one.
3. **Local-only commits.** Committed, so safe from a careless checkout, but on
   no remote — a dead disk takes them.
4. **A clone with no remote at all**, where every commit is local-only.

The count that matters is `git rev-list --all --not --remotes`, which is the
number of *distinct* commits reachable from any local ref and present on no
remote. Summing per-branch counts overcounts badly — measured across this estate
a naive per-branch sum reported 126 for a repo whose real figure is 118, because
branches share history.
"""

from __future__ import annotations

import pathlib
from collections import defaultdict
from dataclasses import dataclass, field

from rig import proc


@dataclass(frozen=True)
class Exposure:
    """One clone's irreplaceable state."""

    repo: pathlib.Path
    local_only_commits: int = 0
    #: Branches carrying at least one commit that is on no remote.
    unpushed_branches: tuple[str, ...] = ()
    dirty_files: int = 0
    stashes: int = 0
    has_remote: bool = True
    origin: str = ""

    @property
    def at_risk(self) -> bool:
        """True when something here would be lost with the disk."""
        return bool(self.local_only_commits or self.dirty_files or self.stashes)

    @property
    def score(self) -> int:
        """Rough ordering weight. Commits outrank dirt because there is more of it.

        Not a unit of anything — it exists so a dashboard can sort the list
        without the caller inventing its own weighting.
        """
        return self.local_only_commits * 10 + self.dirty_files * 3 + self.stashes * 5


def local_only_commits(repo: pathlib.Path) -> int:
    """Return distinct commits reachable locally and present on no remote."""
    out = proc.git_out("rev-list", "--count", "--all", "--not", "--remotes", cwd=repo)
    return int(out) if out.isdigit() else 0


def unpushed_branches(repo: pathlib.Path) -> tuple[str, ...]:
    """Return local branches holding at least one commit that is on no remote.

    Answers the actionable question — *what do I push* — which the total alone
    does not. One `rev-list` per branch, so this is the expensive half of the
    check and callers may skip it.
    """
    names = [
        n for n in proc.git_out("for-each-ref", "--format=%(refname:short)", "refs/heads", cwd=repo).splitlines() if n
    ]
    carrying: list[str] = []
    for name in names:
        count = proc.git_out("rev-list", "--count", name, "--not", "--remotes", cwd=repo)
        if count.isdigit() and int(count) > 0:
            carrying.append(name)
    return tuple(carrying)


def inspect_exposure(repo: pathlib.Path, *, per_branch: bool = True) -> Exposure:
    """Measure one clone's irreplaceable state."""
    remotes = proc.git_out("remote", cwd=repo)
    origin = proc.git_out("remote", "get-url", "origin", cwd=repo)
    dirty = len([line for line in proc.git_out("status", "--porcelain", cwd=repo).splitlines() if line.strip()])
    stashes = len([line for line in proc.git_out("stash", "list", cwd=repo).splitlines() if line.strip()])
    return Exposure(
        repo=repo,
        local_only_commits=local_only_commits(repo),
        unpushed_branches=unpushed_branches(repo) if per_branch else (),
        dirty_files=dirty,
        stashes=stashes,
        has_remote=bool(remotes),
        origin=origin,
    )


def _no_paths() -> list[pathlib.Path]:
    """Typed factory — see `estate.checks`."""
    return []


def _no_counts() -> dict[str, int]:
    """Typed factory — see `estate.checks`."""
    return {}


@dataclass
class DuplicateClone:
    """Two or more checkouts of the same origin."""

    origin: str
    repos: list[pathlib.Path] = field(default_factory=_no_paths)
    #: Local-only commits per checkout, so the risk is attributed rather than summed.
    exposure: dict[str, int] = field(default_factory=_no_counts)


def find_duplicates(exposures: list[Exposure]) -> list[DuplicateClone]:
    """Group checkouts that share an origin.

    Worth surfacing separately because a duplicate inflates every estate total:
    two clones of one repo each reporting 118 local-only commits are not 236
    commits at risk, and a dashboard that adds them up is lying. It is also a
    reclaim candidate — the second checkout is usually a forgotten experiment.
    """
    by_origin: dict[str, list[Exposure]] = defaultdict(list)
    for exposure in exposures:
        if exposure.origin:
            by_origin[_normalise(exposure.origin)].append(exposure)

    out: list[DuplicateClone] = []
    for origin, group in sorted(by_origin.items()):
        if len(group) < 2:
            continue
        out.append(
            DuplicateClone(
                origin=origin,
                repos=[e.repo for e in group],
                exposure={e.repo.name: e.local_only_commits for e in group},
            )
        )
    return out


def _normalise(url: str) -> str:
    """Reduce a remote URL to `host/owner/name` so SSH and HTTPS forms match."""
    cleaned = url.strip().removesuffix(".git")
    for prefix in ("git@", "ssh://git@", "https://", "http://"):
        cleaned = cleaned.removeprefix(prefix)
    return cleaned.replace(":", "/", 1)
