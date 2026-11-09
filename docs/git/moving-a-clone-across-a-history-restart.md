# Moving a clone across a history restart

`rtkelly13/rig` was restarted without history on 2026-11-07. The new root commit
`3ef1c67` ("rig: a fresh start from rtkelly13/rig-archive@f715cb88") has the same
tree as the archive's last `main`, but no ancestry in common with it. The old
history stays read-only in `rtkelly13/rig-archive`.

The restart exists to keep work content out of a public repository, and that
content is still in the old history. A clone from before the restart therefore
holds history that must **never** reach the new `origin`. Its tip trees are
clean; the leak is in pushing old commits. Every step below protects against
that.

This is the procedure used to move the Windows machine's clone: 45 worktrees and
84 branches, several of them in live agent sessions.

## 1. Fence off the old history

Keep the archive reachable for comparison, but make it impossible to push to:

```bash
git remote add archive git@github.com:rtkelly13/rig-archive.git
git remote set-url --push archive DISABLED-archived-history
git fetch archive
git fetch archive "refs/pull/<n>/head:refs/remotes/archive/pr/<n>"   # each PR you may port
```

`archive/pr/<n>` refs show what each archive PR held, which matters once you
start working out what was squash-merged.

Then refresh `origin` itself. Fetching `archive` does not touch it, so a clone
that has not fetched since the restart still has the old history under
`origin/main`, and every later step would compare against, and port onto, the
wrong base:

```bash
git fetch --prune origin
git rev-list --max-parents=0 origin/main   # must print the new root, 3ef1c67…
```

Stop if it prints anything else.

## 2. Install a pre-push guard

Put a `pre-push` hook in the clone's common dir (`git rev-parse --git-common-dir`
→ `hooks/`), so that it covers every worktree. For pushes to `rtkelly13/rig` it
refuses:

- a tip that does not descend from the new root `3ef1c67`,
- any new commit that is an ancestor of `archive/main`,
- an author or committer email that is not a personal or GitHub noreply address,
- a message or added line that matches the work-content term pattern.

The term pattern lives **only in the local hook**, never in this repository: the
repository is public, and naming the terms here would publish them.

Test it before relying on it: a push of `archive/main` and of an unported branch
must be refused, and a push of `origin/main` allowed.

`estate shelve` refuses any worktree that shares no history with what origin
serves ([shelving](../estate/shelving.md)), so it cannot be used to park
old-history work. Do not shelve until the worktree is ported.

## 3. Audit before touching anything

For every worktree and branch, record:

- uncommitted and untracked changes, and whether the worktree is locked;
- commits on no remote at all (`git rev-list <branch> --not --remotes`), which
  are the only copy of that work;
- the archive PR it belongs to and that PR's state, and whether the work landed
  in the new repository (compare trees with `git merge-tree`; a squash merge
  leaves no shared commits);
- whether an agent session is working in it (`ListAgents`, or Orca's session
  list).

Sort each into one of four buckets:

| Bucket | What | Action |
|:-------|:-----|:-------|
| **Port** | Open PRs, local-only work | Rebase its own commits onto `origin/main` |
| **Reset** | Long-lived worktrees with nothing unique | `git reset --keep origin/main`, unset upstream |
| **Remove** | Landed, merged or abandoned | Remove the worktree, delete the branch |
| **Keep** | Work that will not rebase cleanly | Leave it, port by hand |

## 4. Port, reset and remove: dry run first

Script the buckets, and make the script print what it would do by default and
act only with `--apply`. For every branch or detached HEAD it changes, it must
first write a backup ref (`refs/backup/rig-archive/<branch>`, or
`.../detached/<dir>`), and it never pushes.

**Find the commits to replay with `merge-base <branch> archive/main`, not with
`--not --remotes=archive`.** A PR branch's commits are on its archive PR branch,
so reachability from archive refs says it has nothing unique, and resetting it
would discard the PR. Use the reachability test only for a branch built on top of
other PR branches, where it picks out the commits that are this branch's alone.

Then, per bucket:

- **Port.** Scan the replayed range for the term pattern and identities, and
  refuse on any hit. Commit uncommitted work as a `wip:` commit first, but only
  when something is actually staged: on Windows, line-ending noise makes a
  worktree look dirty and then leaves nothing to commit.
  `git rebase --onto origin/main <start> <branch>`. On a conflict, abort,
  leave the branch as it was, and report the command to finish by hand.
- **Reset.** Refuse dirty worktrees, except the root, whose harmless local
  edits `reset --keep` carries across. Point the root's `main` at `origin/main`.
- **Remove.** Refuse dirty or locked worktrees, and skip anything already on
  the new history, so that running the script again never touches new work.

Read the dry run in full, pause the agent sessions it touches, then apply. The
reset pulls a branch out from under a running session, and a removed worktree
takes away a session's working directory.

## 5. Push and reopen

Push each ported branch normally; the guard checks it on the way out. Reopen its
PR on `rtkelly13/rig` against `main`, and link the archive PR in the body rather
than repeating its history.

## 6. Finish the machine

The rest of the handover (machine-local config in `~/.workspace/config.json`, the
desktop-manager runtime and its checks) is in the
[handoff comment on rig-archive#647](https://github.com/rtkelly13/rig-archive/pull/647#issuecomment-6714357821).

## 7. Tear down

Once every worktree is on the new history:

- remove the pre-push hook and the `archive` remote;
- after a grace period, delete `refs/backup/rig-archive/*`
  (`git for-each-ref --format='delete %(refname)' refs/backup/rig-archive | git update-ref --stdin`);
- remove that machine's pending-setup bullet from `AGENTS.md`.
