"""Can any estate command lose work? A scenario x entry-point matrix with a recoverability oracle.

Each scenario builds a real worktree in a throwaway clone and plants canaries: files with
content that exists nowhere else. Each entry point then runs on that worktree at its most
aggressive ordinary flags (`tidy --apply --force --merged`, `shelve --apply --idle 0` against a
merged PR, `reclaim --apply --force`, `trim --apply --idle-days 0`). Afterwards the oracle demands
that no canary became less recoverable: on disk, or its blob reachable from a ref in the clone or its
origin. A canary that only a reflog names is already at risk (`gc` expires it, and a worktree's
reflog goes with its admin dir), so the oracle ranks each one SAFE, REFLOG or GONE before and after,
and a command may never lower the rank.

The oracle also records which layer kept the work:

- `tool`: the command refused;
- `git`: git declined an unforced `worktree remove`, which the command reports as `failed`;
- `kept`: the command removed what it was meant to and the canaries survived anyway (shelved,
  pushed, or untouched).

Git's refusal is a backstop, not a guard: a cell whose expectation is `GUARDED` fails if only git
stood in the way.

Known gaps are rows marked `xfail(strict=True)` that accept only `CanaryLostError` or `NotGuardedError`.
A broken scenario raises something else and fails, so it cannot pass as a known gap. Fixing a
gap turns its row into an XPASS, which strict mode reports as a failure until the marker is
removed. The matrix works as a scoreboard. The gaps are written up in
`docs/estate/worktrees.md` and the 2026-11-07 estate gap report.

Orca's own `worktree rm` is not a row: it runs in Orca, not in rig. rig's Orca apply only
unregisters projects.

Part of #30 F7 (estate behaviour suites). Entry points are driven through their library surface,
not `CliRunner`. `reclaim` has no public per-entry seam, so its row calls `cli._reclaim_one`, as
`test_estate_cli` already does. Giving it one is F7 work.
"""

from __future__ import annotations

import datetime as dt
import enum
import hashlib
import pathlib
import shutil
import sys
from collections.abc import Callable
from dataclasses import dataclass, field

import pytest

from rig import proc
from rig.estate import cli, shelf_store, shelve, trim
from rig.estate import reclaim as reclaim_mod
from rig.estate.checks import Worktree, inspect_repo, list_worktrees
from rig.estate.cleanup import Busy, Outcome, clean_repo
from rig.estate.prs import PullRequest

pytestmark = pytest.mark.ci

BRANCH = "spike"


class CanaryLostError(AssertionError):
    """A canary became less recoverable than it was before the command ran."""


class NotGuardedError(AssertionError):
    """The work survived, but the command itself did not refuse: git declined, or the command crashed first."""


class Layer(enum.StrEnum):
    TOOL = "tool"  # the command refused
    GIT = "git"  # git declined `worktree remove`; the command reported a failure
    CRASH = "crash"  # the command failed on something else before it reached a removal
    UNTOUCHED = "untouched"  # the command never offered the worktree at all
    KEPT = "kept"  # removed, shelved or trimmed, and the canaries survived


class Expect(enum.StrEnum):
    SAFE = "safe"  # canaries survive, whichever layer saved them
    GUARDED = "guarded"  # canaries survive, and the command itself refused
    REMOVES = "removes"  # the control: the worktree is actually removed, and nothing undeclared is lost
    TRIMS = "trims"  # trim's control: every canary it may lose is gone, and the worktree stays


# ---------------------------------------------------------------------------
# a scenario: a clone, one worktree in it, and what must survive
# ---------------------------------------------------------------------------


def _git(args: list[str], cwd: pathlib.Path) -> str:
    return proc.git(*args, cwd=cwd, check=True).out.strip()


def _blob(content: str) -> str:
    raw = content.encode()
    return hashlib.sha1(b"blob %d\0" % len(raw) + raw, usedforsecurity=False).hexdigest()


@dataclass(slots=True)
class World:
    """The clone the entry point runs against, the worktree it targets, and the canaries planted there."""

    root: pathlib.Path
    origin: pathlib.Path
    clone: pathlib.Path
    tree: pathlib.Path
    canaries: dict[pathlib.Path, str] = field(default_factory=dict[pathlib.Path, str])
    may_lose: set[pathlib.Path] = field(default_factory=set[pathlib.Path])
    keep_tree: bool = False  # the worktree itself must survive (in use, locked, another clone's)
    busy: frozenset[pathlib.Path] = frozenset()
    repos: list[pathlib.Path] = field(default_factory=list[pathlib.Path])
    pr_head: str = ""  # the head commit the merged PR recorded, as `gh` reports headRefOid

    def plant(self, rel: str, *, lose_ok: bool = False) -> pathlib.Path:
        path = self.tree / rel
        path.parent.mkdir(parents=True, exist_ok=True)
        content = f"canary {rel} {self.tree.name} {len(self.canaries)} {id(self)}\n"
        path.write_text(content, encoding="utf-8", newline="\n")  # CRLF would change the blob id
        self.canaries[path] = content
        if lose_ok:
            self.may_lose.add(path)
        return path


def _origin(root: pathlib.Path) -> pathlib.Path:
    bare = root / "origin.git"
    _git(["init", "-q", "--bare", "-b", "main", str(bare)], root)
    return bare


def _clone(root: pathlib.Path, name: str, origin: pathlib.Path) -> pathlib.Path:
    path = root / name
    _git(["clone", "-q", str(origin), str(path)], root)
    _git(["config", "user.email", "t.user@example.com"], path)
    _git(["config", "user.name", "T"], path)
    _git(["config", "core.autocrlf", "false"], path)
    return path


def _world(root: pathlib.Path, *, relative: bool = False, squashed: bool = True) -> World:
    """A clone and a finished worktree: `spike` was pushed, squash-merged into `main`, and its remote branch deleted.

    That is the state `--merged` exists for: the branch's own commits are on no remote, and only
    the merged PR says the work landed. With *squashed* off, the branch stays on origin as it is,
    as after a merge commit or with branch auto-delete off.
    """
    origin = _origin(root)
    seed = _clone(root, "seed", origin)
    _git(["symbolic-ref", "HEAD", "refs/heads/main"], seed)
    (seed / ".gitignore").write_text("node_modules/\nTODO.md\n", encoding="utf-8")
    (seed / "README.md").write_text("hi\n", encoding="utf-8")
    _git(["add", "."], seed)
    _git(["commit", "-qm", "first"], seed)
    _git(["push", "-q", "origin", "main"], seed)

    clone = _clone(root, "clone", origin)
    add = ["worktree", "add", "-q", *(["--relative-paths"] if relative else []), "-b", BRANCH, "temp/spike"]
    _git(add, clone)
    tree = clone / "temp" / "spike"
    world = World(root=root, origin=origin, clone=clone, tree=tree, repos=[clone, origin])
    work = world.plant("work.md")
    _git(["add", "."], tree)
    _git(["commit", "-qm", "spike work"], tree)
    _git(["push", "-q", "-u", "origin", BRANCH], tree)
    world.pr_head = proc.git_out("rev-parse", "HEAD", cwd=tree)
    if not squashed:
        return world

    (seed / "work.md").write_text(world.canaries[work], encoding="utf-8", newline="\n")
    _git(["add", "."], seed)
    _git(["commit", "-qm", "spike work (#1)"], seed)
    _git(["push", "-q", "origin", "main", f":{BRANCH}"], seed)
    _git(["fetch", "-q", "--prune", "origin"], clone)
    return world


def finished(root: pathlib.Path) -> World:
    """Control: every commit is on origin and the PR merged. Every remover must take it."""
    return _world(root)


def dirty(root: pathlib.Path) -> World:
    world = _world(root)
    world.plant("scratch.md")
    (world.tree / "work.md").write_text("edited, never committed\n", encoding="utf-8", newline="\n")
    world.canaries[world.tree / "work.md"] = "edited, never committed\n"
    return world


def ignored_build(root: pathlib.Path) -> World:
    """Only regenerable output is ignored. Losing it is the point of every remover."""
    world = _world(root)
    world.plant("node_modules/pkg/index.js", lose_ok=True)
    return world


def ignored_notes(root: pathlib.Path) -> World:
    """The estate convention: a gitignored `TODO.md` holding notes that exist nowhere else."""
    world = _world(root)
    world.plant("TODO.md")
    return world


def unborn(root: pathlib.Path) -> World:
    """The branch ref is gone while the worktree has it checked out, as when a clone is replaced."""
    world = _world(root)
    world.plant("local.md")
    _git(["add", "local.md"], world.tree)
    _git(["commit", "-qm", "local only"], world.tree)
    _git(["update-ref", "-d", f"refs/heads/{BRANCH}"], world.clone)
    return world


def foreign(root: pathlib.Path) -> World:
    """A stale clone carrying a copy of the real clone's registry entry; the commands run in the stale one."""
    world = _world(root)
    world.plant("scratch.md")
    stale = _clone(root, "stale", world.origin)
    shutil.copytree(world.clone / ".git" / "worktrees" / "spike", stale / ".git" / "worktrees" / "spike")
    world.repos.append(stale)
    world.clone = stale
    world.keep_tree = True
    return world


def busy(root: pathlib.Path) -> World:
    """A shell is working below the worktree. Only `tidy` can be told; the others have no seam for it."""
    world = _world(root)
    (world.tree / "src").mkdir()
    world.busy = frozenset({(world.tree / "src").resolve()})
    world.keep_tree = True
    return world


def locked(root: pathlib.Path) -> World:
    world = _world(root)
    _git(["worktree", "lock", "--reason", "keep me", str(world.tree)], world.clone)
    world.keep_tree = True
    return world


def _reset_away(world: World) -> World:
    """Commit a canary, then reset it away: it stays in the reflogs, on no ref and not on disk."""
    lost = world.plant("dropped.md")
    _git(["add", "dropped.md"], world.tree)
    _git(["commit", "-qm", "about to be reset away"], world.tree)
    _git(["reset", "-q", "--hard", "HEAD~1"], world.tree)
    assert not lost.exists()
    return world


def reflog_only(root: pathlib.Path) -> World:
    """A commit dropped by a reset on a squash-merged branch."""
    return _reset_away(_world(root))


def reflog_only_pushed(root: pathlib.Path) -> World:
    """The same on a branch still on origin, so every remover sees nothing local and deletes the branch."""
    return _reset_away(_world(root, squashed=False))


def relative(root: pathlib.Path) -> World:
    """A finished worktree written with `--relative-paths` (git >= 2.48). It must still be removed."""
    version = proc.git("version", cwd=root).out.split()[2]
    major, minor = (int(p) for p in version.split(".")[:2])
    if (major, minor) < (2, 48):
        pytest.skip(f"git {version} has no --relative-paths")
    return _world(root, relative=True)


SCENARIOS: dict[str, Callable[[pathlib.Path], World]] = {
    f.__name__: f
    for f in (
        finished,
        dirty,
        ignored_build,
        ignored_notes,
        unborn,
        foreign,
        busy,
        locked,
        reflog_only,
        reflog_only_pushed,
        relative,
    )
}


def _registered(world: World) -> Worktree:
    target = world.tree.resolve()
    return next(t for t in list_worktrees(world.clone) if t.path.resolve() == target)


def _ignored_and_clean(world: World, rel: str) -> None:
    assert proc.git("check-ignore", "-q", rel, cwd=world.tree).ok, f"{rel} is not ignored"
    assert not _git(["status", "--porcelain"], world.tree), "an ignored-only tree must read clean"


def _gitdir(world: World) -> str:
    return (world.tree / ".git").read_text(encoding="utf-8").split("gitdir:", 1)[1].strip()


def assert_shape(scenario: str, world: World, before: dict[pathlib.Path, Tier]) -> None:
    """What *scenario* must look like before the command runs.

    A mis-built scenario then fails here, loudly, instead of passing as a known gap.
    """
    assert world.tree.is_dir(), "scenario did not build its worktree"
    assert all(t > Tier.GONE for t in before.values()), "scenario planted an unrecoverable canary"
    match scenario:
        case "dirty":
            assert _git(["status", "--porcelain"], world.tree), "not dirty"
        case "ignored_build":
            _ignored_and_clean(world, "node_modules")
        case "ignored_notes":
            _ignored_and_clean(world, "TODO.md")
        case "unborn":
            assert _registered(world).unborn, "HEAD is not unborn"
        case "foreign":
            assert world.clone.name not in _gitdir(world), "the stale clone owns the worktree"
            assert _registered(world), "the stale clone does not list the worktree"
        case "locked":
            assert _registered(world).locked, "not locked"
        case "reflog_only" | "reflog_only_pushed":
            assert Tier.REFLOG in before.values(), "nothing is reflog-only"
        case "relative":
            assert not pathlib.Path(_gitdir(world)).is_absolute(), "gitdir is absolute"
        case _:
            pass


# ---------------------------------------------------------------------------
# entry points: each returns the layer that decided
# ---------------------------------------------------------------------------


def run_tidy(world: World) -> Layer:
    """Two passes, as documented: removing a worktree frees its branch for the second."""
    verdicts: list[Outcome] = []
    merged = {BRANCH: PullRequest(1, "MERGED", BRANCH, head_oid=world.pr_head)}
    for _ in range(2):
        report = inspect_repo(world.clone, merged_heads=merged)
        actions = clean_repo(report, apply=True, force=True, merged_heads=merged, in_use=Busy(world.busy))
        verdicts += [a.outcome for a in actions if pathlib.Path(a.subject) == world.tree]
    if Outcome.CLEANED in verdicts:
        return Layer.KEPT
    return Layer.GIT if Outcome.FAILED in verdicts else Layer.TOOL


def run_shelve(world: World) -> Layer:
    target = world.tree.resolve()
    tree = next(t for t in list_worktrees(world.clone) if t.path.resolve() == target)
    plan = shelve.plan_worktree(
        world.clone,
        tree,
        user=shelve.shelf_user(world.clone),
        shelves=shelf_store.remote_shelves(world.clone),
        prs={BRANCH: shelve.PrRef(1, "MERGED")},
        options=shelve.Options(idle=dt.timedelta(0)),
    )
    result = shelve.apply_plan(plan, ledger=None)
    if result.outcome == "needs-decision":
        return Layer.TOOL
    if result.outcome == "failed":
        return Layer.GIT if result.detail.startswith("worktree remove") else Layer.CRASH
    return Layer.KEPT


def run_reclaim(world: World) -> Layer:
    target = world.tree.resolve()
    entries = [e for e in reclaim_mod.scan_repo(world.clone, floor=0).entries if e.path.resolve() == target]
    if not entries:
        return Layer.UNTOUCHED
    status = cli._reclaim_one(world.clone, entries[0], apply=True, force=True).status  # pyright: ignore[reportPrivateUsage]
    if status == "removed":
        return Layer.KEPT
    return Layer.GIT if status == "failed" else Layer.TOOL


def run_trim(world: World) -> Layer:
    for item in trim.scan(world.clone, idle_days=0, floor=0):
        if not trim.recheck(item, idle_days=0):
            trim.remove(item)
    return Layer.KEPT


ENTRY_POINTS: dict[str, Callable[[World], Layer]] = {
    "tidy": run_tidy,
    "shelve": run_shelve,
    "reclaim": run_reclaim,
    "trim": run_trim,
}


# ---------------------------------------------------------------------------
# the oracle
# ---------------------------------------------------------------------------


class Tier(enum.IntEnum):
    """How recoverable one canary is. A command may never lower it."""

    GONE = 0
    REFLOG = 1  # only a reflog names it: `gc` expires it, and a worktree's goes with its admin dir
    SAFE = 2  # on disk as planted, or reachable from a ref in some repo


def _objects(repo: pathlib.Path, *walk: str) -> set[str]:
    done = proc.git("rev-list", "--objects", *walk, cwd=repo)
    return {line.split(" ", 1)[0] for line in done.out.splitlines() if line}


def recoverability(world: World) -> dict[pathlib.Path, Tier]:
    """Each canary's tier. `--all` walks refs only; `--reflog` adds every worktree's reflog."""
    repos = [r for r in world.repos if r.exists()]
    by_ref = set[str]().union(*(_objects(r, "--all") for r in repos))
    by_reflog = set[str]().union(*(_objects(r, "--all", "--reflog") for r in repos))
    tiers: dict[pathlib.Path, Tier] = {}
    for path, content in world.canaries.items():
        blob = _blob(content)
        on_disk = path.is_file() and path.read_text(encoding="utf-8") == content
        tiers[path] = Tier.SAFE if on_disk or blob in by_ref else Tier.REFLOG if blob in by_reflog else Tier.GONE
    return tiers


def judge(world: World, before: dict[pathlib.Path, Tier], layer: Layer, expect: Expect) -> None:
    after = recoverability(world)
    lost = [
        f"{p.relative_to(world.tree)} ({before[p].name} -> {after[p].name})"
        for p in world.canaries
        if p not in world.may_lose and after[p] < before[p]
    ]
    if lost:
        raise CanaryLostError(f"{layer}: lost {lost}")
    if world.keep_tree and not world.tree.is_dir():
        raise CanaryLostError(f"{layer}: removed a worktree that had to stay")
    if expect is Expect.GUARDED and layer not in (Layer.TOOL, Layer.UNTOUCHED):
        raise NotGuardedError(f"survived on {layer} alone")
    if expect is Expect.TRIMS and any(p.exists() for p in world.may_lose):
        raise AssertionError(f"{layer}: trimmed nothing it may lose")
    if expect is Expect.REMOVES and world.tree.exists():
        raise AssertionError(f"{layer}: refused a worktree it exists to remove")


# ---------------------------------------------------------------------------
# the table
# ---------------------------------------------------------------------------

GAP_IGNORED = "gap A: ignored, non-regenerable files go with `git worktree remove`; only shelve checks them"
GAP_RECLAIM = "gap B: reclaim has no unborn or foreign-owner guard of its own; only git's validation stops it"
GAP_SHELVE = "gap C: shelve has no foreign-owner guard; it crashes on commit-tree with the null SHA, by luck"
GAP_BUSY = "gaps B/C: no process-based in-use check outside tidy (shelve reads mtimes, and --idle 0 drops even that)"
GAP_REFLOG = "gap D: a commit only the reflog names is not counted as local work, and deleting the branch drops it"
GAP_LOCKED = "a lock is left to git's refusal, reported as `failed`; only shelve treats it as a decision"

#: What each cell must do. Cells absent here default to SAFE.
EXPECT: dict[tuple[str, str], Expect] = {
    **{
        (s, e): Expect.REMOVES for s in ("finished", "ignored_build", "relative") for e in ("tidy", "shelve", "reclaim")
    },
    **{(s, e): Expect.GUARDED for s in ("unborn", "foreign", "locked") for e in ("tidy", "shelve", "reclaim")},
    ("busy", "tidy"): Expect.GUARDED,
    ("ignored_build", "trim"): Expect.TRIMS,
    ("ignored_notes", "shelve"): Expect.GUARDED,
}

#: Known gaps: (the exception the gap raises, why). Fixing one makes the cell XPASS, which fails.
GAPS: dict[tuple[str, str], tuple[type[AssertionError], str]] = {
    ("ignored_notes", "tidy"): (CanaryLostError, GAP_IGNORED),
    ("ignored_notes", "reclaim"): (CanaryLostError, GAP_IGNORED),
    ("unborn", "reclaim"): (NotGuardedError, GAP_RECLAIM),
    ("foreign", "shelve"): (NotGuardedError, GAP_SHELVE),
    ("foreign", "reclaim"): (NotGuardedError, GAP_RECLAIM),
    ("busy", "shelve"): (CanaryLostError, GAP_BUSY),
    ("busy", "reclaim"): (CanaryLostError, GAP_BUSY),
    ("reflog_only", "tidy"): (CanaryLostError, GAP_REFLOG),
    ("reflog_only_pushed", "shelve"): (CanaryLostError, GAP_REFLOG),
    ("locked", "tidy"): (NotGuardedError, GAP_LOCKED),
    ("locked", "reclaim"): (NotGuardedError, GAP_LOCKED),
}


def _cells() -> list[object]:
    out: list[object] = []
    for scenario in SCENARIOS:
        for entry in ENTRY_POINTS:
            marks: list[pytest.MarkDecorator] = []
            gap = GAPS.get((scenario, entry))
            if gap is not None:
                marks.append(pytest.mark.xfail(raises=gap[0], strict=True, reason=gap[1]))
            if scenario == "relative" and sys.platform == "win32":
                marks.append(pytest.mark.skip(reason="relative gitdirs are exercised on POSIX runners"))
            out.append(pytest.param(scenario, entry, marks=marks, id=f"{scenario}-{entry}"))
    return out


@pytest.mark.parametrize(("scenario", "entry"), _cells())
def test_no_command_loses_work(scenario: str, entry: str, tmp_path: pathlib.Path) -> None:
    world = SCENARIOS[scenario](tmp_path)
    before = recoverability(world)
    assert_shape(scenario, world, before)

    layer = ENTRY_POINTS[entry](world)

    judge(world, before, layer, EXPECT.get((scenario, entry), Expect.SAFE))
