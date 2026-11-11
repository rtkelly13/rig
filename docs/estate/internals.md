# Internals

**Up:** [`estate`](README.md) · [Worktrees](./worktrees.md) · [Shelving](./shelving.md) · [Caches](./caches.md) · [Decision feed](./decisions.md) · [Orca](./orca.md) · [Lessons](./lessons.md)

How discovery, safety checks and the Windows code paths work. Read this before changing them.

## Discovery is two levels and stops at the repo

`discovery.scan` walks at most two directories below the code root and **stops
descending the moment it finds a `.git`**. A root holding twenty clones costs
about twenty `stat` calls rather than a walk of every `node_modules` beneath
them.

Stopping at the boundary has a second effect worth knowing: a worktree carries a
`.git` *file*, and the convention puts worktrees at `<repo>/temp/<name>` — inside
a clone. Because the scan halts at the clone, worktrees are never discovered as
clones of their own. They are enumerated from their parent by
`git worktree list`, the only place that knows the relationship.

The result caches to `~/.estate-repos.json`, keyed by root, with a 12-hour TTL.
Clones appear rarely and a filesystem walk on every invocation is the kind of
cost that stops a tool being run. A cached path that has since vanished is
dropped without forcing a rescan — a deleted clone is far more common than a new
one, and reporting findings for a path that is gone is worse than a stale count.

## Safety is per item, and it took two goes

The first cut refused any clone holding a dirty file or an unpushed worktree.
Run for real, that was wrong twice over:

- **`reclaim` refused 100% of the 14 GB.** Every clone with build output also had
  local-only commits. But a *commit* is not endangered by deleting
  `node_modules` — it is already committed. Conflating the two made the command
  refuse all of its own targets.
- **`tidy` refused 9 of 17 clones**, hiding 23 provably safe deletions. An
  uncommitted file in `src/` says nothing about whether a branch whose upstream
  was deleted can go.

So each handler proves its own target instead:

| target | the proof |
|---|---|
| a branch | nothing is lost — either no commits outside a remote, or `--merged` confirms a merged PR for its head ref |
| a worktree | clean — no modified *or untracked* files — and the same two-signal test on its own HEAD |
| a branch checked out elsewhere | refused; git would refuse too, but with the reason it is obvious |
| anything resolving outside the clone | refused rather than followed |

A registered worktree always goes through `git worktree remove`, never `rmtree` —
the latter leaves the parent's administrative files behind, which is exactly how
a clone ends up with prunable entries pointing at nothing.

## Windows is a parallel implementation, not a port

`apps`, `downloads` and `reclaim` were written against macOS: they shelled out to
`du` and walked `/Applications` for `.app` bundles. On Windows there is no `du`
on the PATH a scheduled task gets, so every size read **0** and failed silently:
`reclaim` reported nothing reclaimable and `downloads` dropped every directory.
All of them now run on the C# CLI (`src/Rig.Cli/Estate/`, logic in `src/Rig.Core/Estate/`).

- **Sizing** (`DiskUsage`) keeps `du -sk` on POSIX and walks the tree on
  Windows, counting a hardlinked file only when *every* link is inside the tree.
  uv builds `.venv`s from hardlinks into its cache, so logical size overstates
  what deleting one frees by 2-3x; the walker reports what deletion would
  actually free.
- **Apps** (`InstalledApps`, `UninstallRegistry`) reads the three Uninstall registry hives.
  Windows keeps no reliable last-used time, so "dormant" means *not installed or
  updated in 180 days*. Side-by-side IDE and SDK versions are the stronger
  signal.
- **Volume** (`estate volume`, `Volume` and `WizTreeExport`) answers the question the clone-scoped commands
  cannot: where did the *drive* go. Walking is hopeless at volume scale (a
  repo-root `du` ran 40+ minutes), so it drives WizTree, which reads the MFT and
  exports allocated sizes in about a minute. It needs a UAC approval, and its
  export is ~400 MB: on a drive with less free space than that, the elevated
  child stalls silently with zero CPU. Free a little first.
