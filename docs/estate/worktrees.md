# Worktrees: trim, tidy and reclaim

**Up:** [`estate`](README.md) · [Shelving](./shelving.md) · [Caches](./caches.md) · [Decision feed](./decisions.md) · [Orca](./orca.md) · [Internals](./internals.md) · [Lessons](./lessons.md)

Worktrees hold both kinds of file this tool keeps apart: work that exists only here, and build output that regenerates. These sections cover how each command decides which is which.

## Trimming idle worktrees

A worktree stays until its branch is provably finished, which can take weeks,
but the bulk inside it, such as `node_modules`, `.venv` and `obj`, comes back from
one install. `estate trim` deletes that bulk from worktrees nobody has used for
`--idle-days` (7 by default) and leaves the worktree itself in place. The
scheduled `estate-trim` job runs it daily with `--apply`.

**Idle comes from git's bookkeeping, not file timestamps.** The worktree's
admin `index`, `HEAD` and `logs/HEAD` change on every commit, checkout, stage
and status refresh. A build rewrites `node_modules` long after anyone looked at
the branch, so tree mtimes would keep a dead worktree "active" forever.
Bookkeeping that cannot be read counts as active.

Every candidate directory has to prove it is safe on its own:

| refused when | why |
|---|---|
| git does not ignore it, or tracks a file inside it | a vendored patch or a committed `bin/` is source |
| it resolves outside the worktree | a symlink or junction points at someone else's data |
| a `.git` exists anywhere below it | a checkout is parked inside it |

`bin`/`obj` count as build output only beside a .NET project file, `packages`
only beside a `.sln`, and `target` only beside `Cargo.toml`. The main checkout,
locked worktrees and each worktree's `temp/` are never touched. Sizes are
measured in Python rather than with `du`, because `du` is not on a scheduled
task's PATH. A file hard-linked from the uv cache or pnpm store frees nothing
when it is deleted, so it does not count as freeable.

The scheduled jobs pass `--root` explicitly. Left to itself, `estate` puts the
code root beside the checkout its own code runs from. For the installed runtime
copy that directory is `%LOCALAPPDATA%\shared-utilities\runtime`, which holds
no clones.

`rig web` offers the same trim (`rig.estate.web`). `GET /api/ops/estate/trim`
returns the last finished scan at once, with `scanning: true` while a new one
runs on a background thread (the first call, `?refresh=true`, or the first
call after an apply starts one). `POST /api/ops/estate/trim/apply` with
`{"paths": [...]}` removes only paths that scan offered and did not refuse,
and re-runs every check above on each first (`trim.recheck`), so a directory
that became a link or whose worktree was used again is refused, not deleted.
`GET /api/ops/estate/disk` reports the drive holding the code root.

## Reclaiming merged work worktrees

The scheduled `worktree-reclaim` job runs the workspace repo's
`workspace worktree reclaim --all-repos --apply` daily, with the workspace
checkout's own Python. That command owns the work repos' worktree policy: it
removes worktrees whose work is merged and keeps any holding untracked files,
so hand-written notes in a worktree are never lost to it. Like
`workspace-maintain`, the job exists only when `tools.estate.workspace_repo` names
the workspace checkout, and is skipped, not failed, when that checkout or its venv
is missing.

## Why merged branches need care

Under a squash-merge policy a branch's commits never become ancestors of `main`,
so `git branch --merged` reports nothing: `repo_governance.branch_cleanup`
measured **zero on a repo with 36 merged PRs**. Asking GitHub is the only
reliable answer, and that is a network call per clone — which fights the point of
a cheap sweep.

So `estate` **detects** with what git can prove — an upstream marked `gone`, true
ancestry — and **authorises** deletion with `--merged`, which reads merged head
refs from `gh`, one call per clone.

`--merged` is not a nicety, and getting this wrong is the most instructive thing
that happened while building the tool. A squash merge replaces a branch's
commits with one new commit on `main`; deleting the remote branch afterwards
strands the originals on no remote **permanently**. So the local "nothing is
lost" test refuses every branch that actually landed. Measured on
`design-system`: 12 of 12 branches whose upstream was gone reported 1–6
local-only commits, and all 12 had a merged PR. Without `--merged`, `tidy`
refused 100% of its own purpose.

The same mistake had two more sites, both found by running it:

- **Worktrees were measured with `@{upstream}..HEAD`.** A worktree tracking
  `origin/main` after its branch squash-merged reports its originals as "ahead"
  forever — `temp/feat-headless-table` showed 4 ahead and **0** on no remote, a
  refusal for nothing. `orphaned_count` is the honest measure and
  `unpushed_count` is kept only for callers that genuinely want "ahead".
- **A finished worktree was judged by age**, so three worktrees holding
  already-merged branches went unreported because none was 14 days idle. A
  worktree exists to carry a branch; the branch landing is what finishes it.
  That is now `worktree-merged`.

`repo-governance prune-branches` remains the authority for *remote* branches.

### A dirty worktree is never removed, by any route

Two independent layers, and `--force` only relaxes the first:

1. **This tool** refuses when `git status --porcelain` is non-empty, which
   includes untracked files — the likeliest form of unsaved work, and invisible
   to any commit-based check.
2. **git itself** declines `worktree remove` on a tree with *modified or
   untracked files* unless given its own `--force`, which `estate` never passes.

So `--force` on a dirty worktree produces a loud `failed`, not a deletion. The
only thing neither layer counts is **ignored** files — a worktree holding
nothing but `node_modules` is removable, which is the intent.

## Cleanup cascades, so run it twice

Removing a worktree frees the branch it held, which until then was refused as
"checked out in another worktree". A first pass on `design-system` deleted 12
branches and 4 worktrees; a second pass then found one more branch that the
worktree removal had released. Two passes is usually enough.
