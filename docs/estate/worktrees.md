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

## Clearing finished Orca worktrees

Orca makes a worktree for each piece of work and never removes one when the work lands,
so the sidebar fills with finished branches. `estate orca-worktrees` lists every linked
worktree in both Orca profiles (on Windows, `orca-personal` and the work profile) and says
which are finished. As with `estate tidy`, the scope is explicit: `--repo <name|path>`
(repeatable; a project's folder or display name, or a path inside its checkout or a worktree)
or `--all` for every project, and neither or both exits 2. `--apply` removes the finished
ones, `--kept` lists the kept ones with the reason, and `--json` gives every row.

| verdict | means | `--apply` |
|---|---|---|
| `current` | this command is running inside it | keeps it |
| `busy` | an agent in it is `working`, `blocked` or `waiting` | keeps it |
| `missing` | Orca lists it, but the folder is gone | prunes git, removes it from Orca |
| `leftover` | git no longer knows the folder, and every file in it matches the branch | deletes the folder, prunes, removes it from Orca |
| `leftover-differs` | a folder git no longer knows, holding edits, new files, or a branch that is gone | keeps it: look by hand |
| `foreign` | its git is not the registered project's, or the project is not registered | keeps it |
| `dirty` | uncommitted or untracked files | keeps it |
| `pr-unknown` | GitHub could not list the branch's PRs, so "no PR" cannot be trusted | keeps it |
| `merged` | the newest PR for the branch merged, and HEAD has nothing local since | removes it |
| `after-merge` | the PR merged, but there are local commits since | keeps it |
| `open-pr` / `closed-pr` | the PR is open, or closed without merging | keeps it |
| `unmerged` | commits outside the default branch and no PR | keeps it |
| `recent` | already in the default branch, but touched within `--idle-days` | keeps it |
| `landed` | no PR, HEAD is already in the default branch, idle for `--idle-days` (2) | removes it |

The rules are checked in that order, top to bottom, so a running agent, a foreign or dirty
tree, or an unanswered GitHub query keeps the worktree whatever else is true. Each verdict
comes from git, Orca's `worktree.ps` and one GitHub query per branch (`gh pr list --head`),
never from guessing. A PR from a fork that happens to share the branch name is ignored.

**`--apply` checks again just before it removes.** Review and removal are separate steps, so
anything can change between them. For each worktree it removes, `--apply` first confirms that
Orca still lists the same worktree id at that path. A `missing` folder must still be gone, and
a `leftover` must still match its branch file for file. A `merged` or `landed` tree must still
belong to its project and still be clean. If any of these fails, the row says
`skipped: …` and nothing is touched. This shrinks the window for a race to the gap between
the last check and the delete; it cannot close it, because no portable way exists to lock a
folder against writes. A removal counts only when Orca confirms it, or stops listing the
worktree. Otherwise the row says `failed`, and the command exits 1, with or without `--json`.

**Removal goes through Orca, not `rm -rf`.** `worktree.rm` is Orca's own delete. It stops the
worktree's terminals, runs `git worktree remove` without `--force`, so a dirty tree is
refused, and deletes the branch with `git branch -d`, so an unmerged branch stays.

**`leftover` is what an interrupted delete leaves on Windows.** Git deletes the `.git` file and
the top-level dotfiles, then stops at a locked file in `.venv`. Orca then reports the folder
"is no longer a Git worktree" and leaves it on disk. The command compares the folder with
the branch through the main checkout's object store, using a throwaway index under the project's `temp/.rig/`. Files missing
from the folder count as safe, because the branch still has them. They are marked
skip-worktree, so git reads `.gitignore` from the branch even though the folder has lost it.
Only changed or untracked files that git does not ignore keep the folder.

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

### Three worktrees no flag removes

Each of these is a case where the usual signals read wrong rather than merely
cautious, so `--force` does not relax them. All three come from the 2026-11-06
estate triage.

- **Unborn HEAD (`worktree-unborn-head`).** The worktree's branch ref is gone, as
  happens when its clone was replaced and the refs went with the old one. Git
  then shows every file as newly staged, which an earlier `doctor` reported as
  `worktree-dirty` (56 of them across 3 clones). With no commit to count,
  `orphaned_count` reads 0 and a merged PR would have authorised removal, so a
  merged unborn worktree is never `worktree-merged`. The finding reads the
  worktree's own reflog: if the last tip is still in the clone, the fix is
  `git branch <name> <tip>`; if not, it names the tip so it can be fetched as a
  PR head, or says the files on disk are the only copy. Of those 56, 26 tips were
  in the clone, 10 were PR heads, and 20 were nowhere else.
- **Another clone's worktree (`worktree-foreign-owner`).** Copying or moving a
  clone copies its `.git/worktrees/` registry, while the worktrees themselves
  point at the new clone. The old clone then lists them as its own. A
  `git worktree remove` there deletes the new clone's live work. The check
  compares each worktree's `.git` file against this clone's `worktrees/` dir and
  reports nothing else about a worktree that fails, since every other reading
  would describe the other clone. 63 such entries across 6 `.old` clones.
- **In use.** `tidy` takes one `lsof` snapshot of process working directories per
  run and refuses any worktree or `temp/` entry that a shell, agent or Orca
  terminal is working in, or below. Where that cannot be read (no `lsof`,
  Windows), removals are refused unless `--force`. Without this,
  `tidy --merged` would have removed 62 worktrees, 8 of them with live
  terminals.

`tidy --check <check>` narrows a run to one kind of finding, for example
`--check worktree-merged`, alongside `--repo` for one clone.

## Cleanup cascades, so run it twice

Removing a worktree frees the branch it held, which until then was refused as
"checked out in another worktree". A first pass on `design-system` deleted 12
branches and 4 worktrees; a second pass then found one more branch that the
worktree removal had released. Two passes is usually enough.
