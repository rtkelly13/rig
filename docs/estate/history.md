# Restarted repos: `estate history`

A repo can be restarted without history: a new repo under the old name, whose `main`
begins at one root commit with the same tree, while the old repo is renamed and archived.
`rtkelly13/rig` was restarted on 2026-11-07 so that work content never ships with it; the
old history lives in `rtkelly13/rig-archive`.

Nothing records a restart in config. The new root commit's message names its source:

```
rig: a fresh start from rtkelly13/rig-archive@f715cb88
```

That gives the archive's slug and the old `main` the restart was cut from. Every new branch
descends from the root commit. So `estate history` reads all it needs from a clone that has
fetched the new `origin/main`, and it works for any repo restarted with the same message.

## The risk it handles

A clone from before the restart still has the old history: its `main` and every old branch.
Pushing any of them to the new repo would publish exactly what the restart was meant to keep
out. GitHub does not stop it, because the new repo accepts unrelated history on a new branch.

## The commands

| | |
|---|---|
| `estate history status [path]` | Read-only. Shows the restart, what `setup` would change, and each local branch's standing. `--all` lists every branch, not just the ones to port; `--offline` skips GitHub |
| `estate history setup [path] --apply` | Adds the `archive` remote, records the guard config, installs the hook and sets main's upstream. Without `--apply` it only shows the changes |
| `estate history port <branch> [--from <ref>] --apply` | Rebases a branch's own commits onto the new `origin/main`. Prints the push; never pushes |

Run `setup` from the main checkout. The hook goes in the clone's shared hooks folder, so it
covers every worktree, but `repo-governance enforce` skips worktrees.

### The push guard

`setup` writes `rig.history.root` and `rig.history.slug` into the clone's git config. The
pre-push hook that rig installs everywhere (`rig.git_hooks`) reads them. A push to that
slug whose commit does not descend from the root is refused with `OLD HISTORY PUSH BLOCKED`.
Pushes to the archive, and pushes from clones without the keys, are unaffected. Deleting a
remote branch is always allowed.

Adding the guard made every older hook outdated, so `repo-governance enforce` refreshes
them across the estate. The guard stays inactive until `setup` writes the keys.

### A branch's standing

| standing | meaning |
|---|---|
| current | descends from the new root; nothing to do |
| retired | nothing left to carry over. It has no commits beyond the old main, or merging it into the old main changes nothing (it was squash-merged), or its archive PR is merged or closed |
| to-port | old history with work the old main lacks; `port` it to keep it |

The squash check runs `git merge-tree --write-tree` against the old main. It writes no refs and
no files. Without it, every squash-merged branch would look unported.

### Porting

`port` runs `git rebase --onto origin/main <old main> <branch>`. The trees of the old and new
`main` match at the restart, so a branch cut from the old `main` usually rebases cleanly. A
branch stacked on another old branch needs `--from <parent>`; otherwise it carries the parent's
commits as well.

`port` refuses in three cases:

- the branch is checked out in another worktree, which may belong to a live session. Run it
  from that worktree instead;
- the current worktree has uncommitted changes;
- the branch is not to-port.

After the rebase, `port` checks that the branch descends from the root before it suggests
`git push -u origin <branch>`.

## What stays manual

- **Resetting an old `main`.** `setup` only advises `git reset --hard origin/main`: the
  worktree that has `main` checked out may hold work.
- **Deleting old branches.** Retired branches stay until `estate tidy` or a person removes
  them. They remain in the archive either way.
- **Carrying over an archive PR's discussion.** `port` prints the archive PR's link so the
  new PR can cite it.
