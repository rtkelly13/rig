# `estate` — managing the code cloned on this machine

**Siblings:** [`repo-governance/`](../repo-governance/README.md) (GitHub-side settings) ·
[`git/`](../git/) (the manual techniques this automates)

Twenty-odd clones under one code root accumulate two things that pull in opposite
directions, and the whole tool exists to keep them apart:

**Work that exists only here.** Uncommitted files, stash entries, commits on no
remote. A dead disk takes all of it, and nothing else on the machine is
irreplaceable.

**Disk that regenerates from a command.** `node_modules`, `.venv`, build output,
scratch in `temp/`. Deleting it costs one `pnpm install`.

They live in the same directory tree, which is why "clean up my repos" is not a
one-liner. Measured on this estate at first run: **539 commits on no remote** and
**14 GB reclaimable**, of which `temp/` was 9.2 GB.

## Personal and work scope

Which folders count as personal and which as work is configuration, never a folder-name
convention (`rig.scopes`):

- **Personal** is tracked in `config/estate-scopes.json`. `owners` are the GitHub accounts
  whose repos clone straight into `personal/`; `orgs` are the GitHub orgs whose repos sit in a
  sibling folder of `personal/` named for the org. Orca registration, the clone layout and
  policy keys all read it.
- **Work** differs between machines, so it lives in `~/.workspace/config.json`. Each entry is
  a folder name under the code root or an absolute path. With none set there is no work scope:
  `estate wrapup --work` selects nothing and `estate decisions` scans only the personal estate.

```json
{ "tools": { "estate": { "work_dirs": ["acme", "workspace"], "workspace_repo": "workspace" } } }
```

`workspace_repo` names the work workspace checkout, resolved the same way. The scheduled
`worktree-reclaim` and `workspace-maintain` jobs run its CLI, and exist only when it is set.

## The commands

| | | more |
|---|---|---|
| `estate audit` | the dashboard — one row per clone, sorted by risk. `--json` for a machine |  |
| `estate risk` | only what exists here and nowhere else, with the `push` command per branch |  |
| `estate reclaim` | the regenerable disk, biggest first. `--min 500MB` to cut the noise | [→](./internals.md#safety-is-per-item-and-it-took-two-goes) |
| `estate trim` | `node_modules`, venvs and build output inside worktrees idle 7+ days | [→](./worktrees.md#trimming-idle-worktrees) |
| `estate tidy` | stale worktrees, and branches that are provably finished. Needs `--repo <clone>` or `--all` | [→](./worktrees.md#why-merged-branches-need-care) |
| `estate apps` | installed application sizes, versions, and age; flags dormant apps (>180d) | [→](./internals.md#windows-is-a-parallel-implementation-not-a-port) |
| `estate downloads` | `~/Downloads` triage: categorizes large files & flags Google Drive duplicates |  |
| `estate doctor` | one clone, every finding, each with its fix. Defaults to the cwd's repo |  |
| `estate sync` | `fetch --prune` everywhere, so `tidy` tells the truth about gone upstreams |  |
| `estate clones` | the cached clone list and its age |  |
| `estate prune-caches` | the tool caches that refill themselves: uv, pnpm, NuGet http, Claude temp, Docker build | [→](./caches.md) |
| `estate decisions` | what only a human can decide: dubious ownership, unpushed or PR-less idle worktrees, leftovers | [→](./decisions.md) |
| `estate orca` | Orca's projects and groups against the `orca.group` each managed project declares. `--apply` registers, creates groups and moves | [→](./orca.md) |
| `estate wrapup` | asks idle Orca Claude sessions whether their change is finished, then reads back each `WRAPUP:` verdict. Needs `--personal`/`--work` and `--repo`/`--all`; `--apply` asks 5 at a time, the next as each answers | [→](./wrapup.md) |
| `estate orca-worktrees` | Orca's finished worktrees in both profiles: merged, landed, folder gone, or half-deleted. `--apply` removes them through Orca | [→](./worktrees.md#clearing-finished-orca-worktrees) |
| `estate volume` | (Windows) a whole drive from a WizTree MFT scan: drill-down, dev-artefact partition, known hogs | [→](./internals.md#windows-is-a-parallel-implementation-not-a-port) |
| `estate shelve` | resolve a worktree `tidy` refuses: push its unpushed work to `shelved/<user>/<name>`, verify, remove | [→](./shelving.md) |
| `estate unshelve` / `shelved` | restore a shelf as a worktree; list shelves and `--prune` the ones that landed | [→](./shelving.md) |

Read-only by default. `reclaim`, `trim`, `tidy`, `prune-caches`, `shelve` and `wrapup` need `--apply`.
`decisions` never changes anything; each entry carries the command that would.
`wrapup` without `--apply` still records the verdicts it reads, in each worktree's gitignored
`temp/.rig/wrapup.json`; see [Wrap-up](./wrapup.md#the-state-file).

The ordering encodes two real dependencies: **`risk` before any `--apply`**, and
**`sync` before `tidy`** — an upstream only reads `gone` after a fetch has pruned
the ref.

## Going further

Each page goes one level deeper. Read the one for the command you are using.

| page | read it when |
|---|---|
| [Worktrees](./worktrees.md) | `trim` or `tidy` keeps or refuses something, or squash-merged branches look unmerged |
| [Shelving](./shelving.md) | a worktree is dirty or unpushed and `tidy` will not remove it |
| [Tool caches](./caches.md) | changing what `prune-caches` touches, or a tool is `skipped` |
| [Decision feed](./decisions.md) | reading `decisions.json`, or a worktree shows up as a decision |
| [Orca](./orca.md) | adding a repo, or Orca's sidebar disagrees with `managed-projects.json` |
| [Wrap-up](./wrapup.md) | finding out which Orca sessions are finished and what is left for you to do |
| [Local ports and Vercel names](./url-ownership.md) | a repo gains a local app or a Vercel project, or naming an alias |
| [Drawing the estate graph](./graph-rendering.md) | changing how the web estate page lays out or draws its dependency lines |
| [Internals](./internals.md) | changing discovery, the safety checks, or the Windows code paths |
| [Lessons](./lessons.md) | running a big cleanup for real; the known gaps |

## What it does not do

- **Decide what to delete.** Every actuator is opt-in and every refusal is
  printed with its reason.
- **Push to a branch you work on.** `sync` fetches, and `shelve` pushes only to
  `shelved/*` refs unless `--to-upstream` is passed. The `push` commands in
  `estate risk` are printed for you to run.
- **Judge whether a clone is still wanted.** It reports last-commit age and
  duplicate origins; abandonment is your call.
