# Shelving

**Up:** [`estate`](README.md) · [Worktrees](./worktrees.md) · [Caches](./caches.md) · [Decision feed](./decisions.md) · [Orca](./orca.md) · [Internals](./internals.md) · [Lessons](./lessons.md)

`estate shelve`, `unshelve` and `shelved`: making the worktrees `tidy` refuses safe to remove.

`tidy` refuses a worktree with uncommitted changes or commits on no remote, and
nothing else ever clears one. Measured on this estate: 59 of 299 registered
worktrees were in that state. `estate shelve` resolves them:

1. **Snapshot without touching the branch.** The working tree, untracked files
   included, is staged into a throwaway index and written with `commit-tree` on
   top of HEAD. No hook runs, the worktree's own index is untouched, and the PR
   branch does not move.
2. **One marker commit per shelf.** Its subject starts `TEMP-SHELVED:` and its
   trailers (`Shelved-Branch`, `-Base`, `-Path`, `-PR`, `-At`, `-By`) are the
   record. A clean worktree with local commits gets a marker too, with an
   unchanged tree, so every shelf looks the same and `unshelve` always drops
   exactly one commit.
3. **Push to `refs/heads/shelved/<user>/<name>`**, never the PR branch.
   `--to-upstream` also fast-forwards the branch's own upstream with the real
   commits; the marker still goes only to the shelf.
4. **Prove it, then remove.** `ls-remote` must report the pushed SHA and the
   commit must be on a remote-tracking ref. Only then does `git worktree remove`
   run, and the branch is deleted only if nothing on it is local-only.

A plan carries `decision_needed`, and `shelve` exits 2, when a snapshot would
silently lose or leak something: ignored files that do not regenerate (an
`.env`: choose `--include-ignored` or `--discard-ignored`), an untracked file
over 50 MB, a locked worktree,
another repository nested inside it (no flag clears that: `git worktree remove`
would delete it), a shelf already on origin holding other content,
a branch that shares no history with origin's default branch (a clone made before
the remote was restarted: rebase it onto origin first, no flag clears this),
or a tree touched within `--idle` (default 2h), which is how a live agent
session looks from outside.

`estate unshelve <name>` recreates the worktree at `Shelved-Path` on
`Shelved-Branch`, and the snapshot comes back as unstaged changes. Whether a
change was staged is not preserved. `estate shelved --prune` drops a shelf whose
PR merged or whose base reached the default branch, and never drops a snapshot.

Each `--apply` that reaches removal appends its outcome to `~/.estate-shelved.jsonl`, one
JSON object per worktree. The ref and its trailers are the record; the ledger is a convenience. The planning and the steps live in
`src/Rig.Core/Estate/Shelve.cs`, the shelf refs and their trailers in `ShelfStore.cs`, and the
three commands in `src/Rig.Cli/Estate/ShelveCommand.cs`.

`TEMP-SHELVED:` is a contract, and it makes a guard easy: a PR check that fails
on any commit with that subject prefix stops a shelf being merged by accident.
