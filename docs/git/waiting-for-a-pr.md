# Waiting for a pull request to land

`gh pr merge N --auto --squash` queues the merge and returns. Nothing in `gh` then says whether the
PR is still moving. Wait with `rig gh pr wait`, not a shell loop:

```bash
rig gh pr wait 141 --repo rtkelly-labs/Diffra          # read-only
rig gh pr wait 141 --repo rtkelly-labs/Diffra --json   # one JSON report on stdout
rig gh pr land 141 --repo rtkelly-labs/Diffra          # gh pr merge --auto --squash, then wait
```

`--repo` defaults to this clone's origin.

## Why not a loop

A loop that exits on `MERGED` or a failed check waits for ever on a PR that is blocked by anything
else. That has happened: every check on a PR passed, auto-merge was queued, and the PR sat
in `mergeStateStatus: BLOCKED` for over 30 minutes because the ruleset requires every review thread
resolved and 14 were open. `gh pr checks --watch` has the same blind spot: it watches checks, not
the merge.

## What it decides

Each poll reads the PR's state, merge state, auto-merge, merge-queue entry and checks in one GraphQL
query, the base branch's active rules from `repos/o/r/rules/branches/<base>`, and the unresolved
threads the way `rig gh threads list` does.

| Exit | Outcome | When |
|:-----|:--------|:-----|
| 0 | `merged` | The PR merged |
| 1 | `blocked` | It cannot land until someone acts; `reason` says what |
| 2 | `error` | GitHub could not be read (the first read, or more than five in a row), or `land` could not queue the merge |
| 3 | `timeout` | `--timeout` (default 60m) passed while it was still moving |

A cause GitHub names stops the wait on the poll that sees it:

| `reason` | Meaning |
|:---------|:--------|
| `check_failed` | A required check failed, was cancelled, timed out or needs action |
| `unresolved_threads` | Review threads are open and the ruleset (or classic protection) requires them resolved; the report lists them |
| `changes_requested`, `review_required` | The review decision blocks the merge |
| `conflicts` | The branch conflicts with its base |
| `draft`, `closed` | The PR is a draft, or was closed without merging |

A state with no named cause must last `--settle` (default 30s) first, because GitHub takes a moment
between the last check passing and merging or enqueuing:

| `reason` | Meaning |
|:---------|:--------|
| `blocked` | `BLOCKED` with no check pending and none of the causes above |
| `behind` | `BEHIND` its base with no check pending |
| `auto_merge_off` | Auto-merge is off and the PR is not in the merge queue, so nothing will merge it |
| `removed_from_merge_queue` | It was in the queue on an earlier poll and is now neither queued nor merged |

`auto_merge_off` applies whether or not checks have finished: `wait` is for a PR that something will
merge, so on a PR with no auto-merge queued it gives up after the settle time even while checks run.
To watch checks alone, use `gh pr checks --watch`; to queue the merge and wait, use `rig gh pr land`.

A required check that has not reported yet is missing from the check rollup, so it is counted as
pending from the ruleset's `required_status_checks`. A failed check that is not required is ignored.

## While it waits

Progress goes to stderr, one line each time what it is waiting on changes: the pending checks, the
merge-queue position. The gap between polls is 5s after a change and grows by half each quiet poll
up to 60s, so a change is seen quickly and an hour's quiet wait is about 65 polls (one or two requests each).

`--timeout 0` looks once and reports, which is the quick way to ask "can this land as it stands?".

## Limits

- A required check that never reports (a workflow skipped by a path filter, say) is counted as
  pending, so the wait runs to `--timeout` and exits 3 naming it.
- `land --json` prints the JSON report only once the wait has started. If the first read or
  `gh pr merge` fails, the reason goes to stderr, stdout is empty and the exit code is 2.
