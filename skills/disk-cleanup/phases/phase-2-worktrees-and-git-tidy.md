# Phase 2: Estate Worktrees & Squash-Merge Tidy (Sustainable SWE Relief)

**Rule: Worktrees are the #1 source of code estate bloat. Prune verified-merged branches via GitHub and discover external worktrees.**

---

## 1. The Squash-Merge Stranding Problem

In an estate with a **Squash-and-Merge** policy:
1. Feature branch commits are squashed into a single new commit on `main`.
2. The remote branch is deleted on GitHub.
3. The local branch's original commits **never become ancestors of `main`**.
4. Git commands like `git branch --merged` report 0 merged branches, and `rev-list <branch> --not --remotes` reports those commits as "local-only" forever.

To prune safely without deleting unmerged work, `estate tidy --merged` queries the GitHub API (`gh pr list --state merged`) to confirm that each branch's head ref actually landed in a merged PR:

```bash
# 1. Fetch and prune dead remote tracking refs across all clones
rig estate sync

# 2. Dry run: inspect which branches/worktrees have landed PRs
rig estate tidy --merged --all

# 3. Apply: delete confirmed merged branches and clean worktrees
rig estate tidy --merged --all --apply
```

---

## 2. External Worktree Pitfall (Estate Root Worktrees)

A registered git worktree can live anywhere on disk.
- **The Convention**: Worktrees belong at `<repo>/.worktrees/<name>`.
- **The Pitfall**: An agent or script checks out a worktree at the virtual monorepo root: `<estate-root>/temp/<name>` (e.g. `personal/temp/obsidian-drop-claude-md`).
- **The Consequence**: If the parent repository contains large binary files (e.g. an Obsidian vault or video assets), this loose worktree duplicates the entire 15 GB footprint outside normal per-repo scans!

### Discovery & Removal:
`estate reclaim` traverses `git worktree list` from each clone to find external worktrees:
```bash
rig estate reclaim
```

To remove a worktree cleanly:
```bash
git -C <parent-repo> worktree remove <worktree-path>
```

> [!CAUTION]
> **Never `rm -rf` a registered worktree**:
> Deleting the directory directly leaves git's administrative files in `.git/worktrees/`, creating prunable or corrupt entries. Always use `git worktree remove`.

---

## 3. Start From the Daily Jobs

The scheduled jobs already do the safe part every day. `estate-trim` drops regenerable output inside worktrees idle for a week, `estate-prune-caches` empties the tool caches, and `worktree-reclaim` removes merged worktrees. What is left for a human is in the decision feed, `%LOCALAPPDATA%\shared-utilities\jobs\decisions.json`, refreshed daily by `estate-decisions`:

```bash
rig estate decisions          # or read the feed; --json for the raw entries
```

Each entry carries one command. `unpushed-worktree` points at the `estate shelve <path>` preview (section 4). A `locked-path` with no files left says `rmdir /s /q`; one that still holds files lists them instead, because some may be hand-written. If the feed is older than a day, check the `estate-decisions` job before acting on it.

---

## 4. Worktrees `tidy` Refuses: Shelve Them

`tidy` never removes a worktree with uncommitted changes or local-only commits. On a busy estate those refusals pile up into most of the reclaimable space: 59 of 299 worktrees, over 120 GB, the first time this was measured. `estate shelve` pushes what exists only locally to `shelved/<user>/<name>` as one `TEMP-SHELVED:` commit. It checks origin holds that commit, then removes the worktree. Design: [`docs/estate/shelving.md`](../../../docs/estate/shelving.md).

```bash
# 1. Dry run across the estate: what resolves, what needs a decision
rig estate shelve --all --root <code-root> --json > shelve-plan.json

# 2. Prove it on ONE worktree first, then check the directory is really gone
rig estate shelve <worktree-path> --apply

# 3. Only with the user's approval: the resolvable batch
rig estate shelve --all --root <code-root> --apply

# Bring one back (the snapshot returns as unstaged changes)
rig estate unshelve <name>
rig estate shelved            # list shelves; --prune drops merged markers
```

Exit code 2 means some worktrees need a decision. Do not clear those decisions in bulk:

| Decision | What to do |
|:---|:---|
| Non-regenerable ignored files (`temp/`, `.env`) | Triage them (below). Use `--discard-ignored` only after the hand-written files are archived. Use `--include-ignored` only when no secret is in the set. |
| Touched within `--idle` (2h) | Treat it as a live agent session. Leave it alone. |
| Holds another repository | Move the nested worktree or clone out first. No flag clears this, because removal would delete it. |
| Untracked file over 50 MB, locked worktree, shelf clash | Ask the user. |

### Triage ignored files before discarding

`temp/` mixes regenerable report or test output with hand-written notes and scripts. Classify each kept ignored file:
1. **Output**: it sits under a dependency, cache or run dir (`node_modules`, `.venv`, `pytest-*`, `temp/pt*`, `run-*` and similar). Check this first.
2. **Secret**: its name matches `.env*`, `*.pem`, `*.key`, `credentials*` and similar.
3. **Hand-written**: a small source-like file (`.py`, `.md`, `.sql`, `.ipynb`). Anything else is output.

Put the per-worktree list in front of the user for a discard, archive or keep choice. Copy the archived files out under `C:\code\workspace\temp\shelve-archive\<repo>\<worktree>`, then shelve with `--discard-ignored`.

> [!CAUTION]
> **Windows leaves a junction skeleton.** `git worktree remove` does not delete pnpm's `node_modules` junctions. The directory survives as thousands of links in empty dirs. `shelve` sweeps it with `sweep_link_skeleton`, which never follows a link and stops if any regular file remains. Plain `git worktree remove` and `tidy` do not sweep it yet.

Lessons and open gaps from the first real run: [`docs/estate/lessons.md`](../../../docs/estate/lessons.md).
