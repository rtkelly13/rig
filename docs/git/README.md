# Git strategy

How work is branched, stacked, reviewed and landed across the personal
repositories — and the specific mechanics that have already gone wrong once.

These are notes on *this* setup, not general git advice. The shape of it:

- **Small PRs, stacked.** A long branch gets split into a chain of reviewable
  layers rather than reviewed as one diff.
- **Worktrees, not branch switching.** Several branches are usually live at
  once, each in its own directory under `temp/`.
- **`main` is protected and strict.** A branch must be current with `main` to
  merge, so landing a queue is inherently sequential.
- **Rebase, not merge, to update a branch.** History stays linear; every
  force-push is `--force-with-lease` and preceded by a safety tag.

## Guides

| Guide | When to use |
|:------|:------------|
| [stacked-pull-requests.md](stacked-pull-requests.md) | A PR refuses `gh pr merge` or a base change because it is in a GitHub stack |
| [splitting-a-branch.md](splitting-a-branch.md) | One long branch needs to become a reviewable chain |
| [landing-a-queue.md](landing-a-queue.md) | Several PRs are ready and `main` is strict |
| [resolving-conflicts.md](resolving-conflicts.md) | Rebasing a stale branch, especially across many of them |
| [worktrees.md](worktrees.md) | Working on more than one branch at a time |
| [moving-a-clone-across-a-history-restart.md](moving-a-clone-across-a-history-restart.md) | A clone from before `rig` was restarted without history |

---

## The rules that are not obvious

**Never delete a base branch while anything is based on it.** Deleting it
auto-closes every child PR, and a closed PR can be neither reopened nor
retargeted. Recreating the branch does not undo it.

**A green check is not a green run.** Check status and run conclusion are
different things, and they disagree in practice. Verify the run:

```bash
rid=$(gh pr checks "$PR" | grep '^<required-check>' | cut -f4 | sed 's|.*/runs/||; s|/job/.*||')
gh run view "$rid" --json conclusion --jq .conclusion   # must be "success"
```

**Verify a rebase by content, not by exit status.** After restacking, the tip's
tree should differ from the old tip only by what the new base introduced:

```bash
git diff --name-only "$OLD_TIP" "$NEW_TIP" > /tmp/moved.txt
git diff --name-only "$OLD_BASE" "$NEW_BASE" > /tmp/base.txt
comm -23 <(sort /tmp/moved.txt) <(sort /tmp/base.txt)   # must be empty
```

Anything printed is work the rebase lost or altered.

**Tag before you rewrite.** `git tag -f prerebase/<branch> origin/<branch>`
before any force-push. It costs nothing and it is the difference between an
undo and an archaeology session.
