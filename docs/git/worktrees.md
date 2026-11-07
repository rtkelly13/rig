# Worktrees

Several branches are usually live at once — a stack being restacked, a spike, a
migration being trialled. Worktrees keep each in its own directory instead of
switching one checkout back and forth.

```bash
git worktree add -b feat/thing temp/thing origin/main
git worktree list
git worktree remove --force temp/thing   # or `git worktree prune` after deleting
```

Convention: `temp/<short-name>` inside the repo, ignored by git.

## The three ways this bites

### A branch can only be checked out once

```
fatal: 'feat/thing' is already used by worktree at '…/temp/thing'
```

Harmless on its own. **Dangerous when scripted**, because a failed `checkout`
leaves you on whatever branch was already there — and the next command runs
against *that*:

```bash
git checkout -q -B feat/thing origin/feat/thing   # fails, still on other-branch
git rebase --onto some-base origin/main           # rebases OTHER-BRANCH
```

That has happened: a rebase intended for one PR landed on an unrelated branch
and rewrote it. Nothing was pushed, so it was recoverable from `origin`, but the
failure is silent by construction.

Chain the intent so a failed checkout stops the sequence:

```bash
git checkout -q -B "$B" "origin/$B" && git rebase --onto "$BASE" "$OLD"
```

and print `git rev-parse --abbrev-ref HEAD` before anything destructive.

### The stash stack is shared

Every worktree shares one stash stack, and other sessions may push or pop it
concurrently. A bare `git stash` / `git stash pop` can restore someone else's
work into your tree.

Prefer a throwaway WIP commit. If you must stash, tag it and apply by SHA:

```bash
git stash push -u -m "my-unique-tag"
SHA=$(git stash list --format='%H %gs' | grep my-unique-tag | head -1 | cut -d' ' -f1)
git stash apply "$SHA"      # apply, not pop
git stash drop "stash@{N}"  # re-find by tag first — indices shift
```

### `node_modules` is per-worktree

A new worktree has none, so `pnpm install` is the first step. That is also the
*point* when trialling a dependency upgrade — the trial cannot contaminate the
main checkout.

Watch for the reverse, though: a dev server started in one worktree serves *its*
build. Probing `localhost:3000` after rebuilding elsewhere reads the wrong
output, and the port collision is easy to miss:

```
⨯ Another next dev server is already running.
```

Check the port is actually yours before trusting what it serves.

## Cleaning up

`git worktree remove` fails on a dirty tree — deliberately. Confirm the contents
are disposable before forcing:

```bash
cd temp/thing && git status --short
git worktree remove --force temp/thing
```

Removing the directory by hand leaves the registration behind; `git worktree
prune` clears the stale entry.
