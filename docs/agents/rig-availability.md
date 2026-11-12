# Global Tooling: Rig Availability

`rig` is the central developer tooling and estate governance CLI installed globally across this machine.

AI agents working in any repository across `~/code/personal` or organizational folders (`rtkelly-labs`, `Resultful`) have access to `rig` directly on `PATH`.

## Purpose & Context Budget

Do not duplicate `rig` command flags or extensive tool documentation across localized repository `AGENTS.md` files. This document serves as the minimal, budget-conscious reference designed to be injected into global harness contexts (e.g., `~/code/personal/AGENTS.md`, Claude Code profiles, Antigravity global rules, and OpenCode configurations) under progressive disclosure.

## Primary Cross-Repo Commands for Agents

### 1. Landing and Waiting on Pull Requests

Never write custom shell polling loops (e.g., looping `gh pr view` or `gh pr checks --watch`). These loops cannot detect merge-queue stalls or unresolved review threads required by repository rulesets.

- **`rig gh pr land <PR> [--repo owner/name]`**: Queues squash auto-merge (`gh pr merge --auto --squash`) and waits until the PR is merged or blocked.
- **`rig gh pr wait <PR> [--repo owner/name] [--timeout 60m] [--settle 30s] [--json]`**: Waits for an already queued PR to merge.

**Exit codes**:
- `0`: Merged successfully.
- `1`: Blocked. Exits immediately with the blocking reason (`check_failed`, `unresolved_threads`, `changes_requested`, `review_required`, `conflicts`, `draft`, `closed`, `auto_merge_off`, `removed_from_merge_queue`).
- `2`: Error reading GitHub API or executing merge command.
- `3`: Timeout exceeded while progress is still pending.

See [docs/git/waiting-for-a-pr.md](../git/waiting-for-a-pr.md) for full details.

### 2. Review Thread Resolution

- **`rig gh threads list <PR> [--repo owner/name]`**: Lists unresolved PR review threads with their IDs.
- **`rig gh threads resolve <PR> <THREAD_ID> --sha <SHA> [--note "..."] [--repo owner/name]`**: Resolves a review thread after pushing a fix matching the commit SHA.

### 3. Estate & Repository Health

- **`rig status`**: Overview of git branches, uncommitted changes, and health checks across the estate.
- **`rig estate orca-worktrees`**: Lists or cleans finished or orphaned agent worktrees.

### 4. Just-In-Time Discovery

- **`rig help`**: High-level overview of available tooling modules.
- **`rig <group> --help`**: Specific flag documentation and exit codes.
