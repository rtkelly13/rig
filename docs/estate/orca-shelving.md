# Daily Orca shelving

Rig owns this policy and runs `estate-orca-shelve` once daily through its existing finite jobs coordinator.
Orca's cleanup settings are not changed and no new daemon is installed. The job reads
`config/estate-orca-shelving.json`; `idle_days` defaults to 14 and must be positive.

```text
rig estate orca-shelve --all --json
rig estate orca-shelve --repo rig --idle-days 21 --json
rig estate orca-shelve --all --apply
rig jobs log estate-orca-shelve --lines 50
```

The first two commands only observe. `--apply` enables pushes, GitHub changes and removal. `--write <path>`
chooses the applied triage report; otherwise it lives beside the jobs decisions feed as `orca-shelving.json`.
Explicit `--orca-data` selects the profile. Only that profile's linked Git worktrees are in scope.

## Why fourteen days

A read-only audit on 2026-11-21 found 194 local linked Orca worktrees. Considering the newest source/untracked
file mtime, HEAD commit time and Orca sidebar activity, 60 exceeded seven days, 31 exceeded fourteen,
14 exceeded twenty-one and none exceeded thirty. Three fourteen-day candidates were dirty; 23 lacked a PR
link in Orca. A missing sidebar link is not proof of no PR: the workflow asks GitHub by branch.
These counts precede safety checks and are not promises of removal.

Fourteen days is a practical default for this estate: it allows two weekly cycles while clearing materially
more than twenty-one days. Age is only eligibility. A source edit, untracked file, commit, workspace activity,
new PR head or recent PR activity keeps work. Read-only git status/fetch/index refreshes and regenerated
build output do not reset this workflow's file/commit clock. Missing timestamps, unreadable files, links or
submodules are kept rather than guessed idle. A deleted tracked file uses its containing directory's mtime.
Non-regenerable ignored files remain a shelving refusal, so their age cannot authorize losing them.

## One recoverable sequence

1. Read the complete Orca inventory (up to 10,000 rows; truncation refuses the sweep), agent activity,
   git ownership and file/commit activity. Main checkouts, pinned/current workspaces, busy or unknown
   agents and processes, locked trees, nested repositories and unreadable state are kept.
2. Plan rig's existing [shelver](shelving.md), including its ignored-file, large-file, reflog and remote-history guards.
   No unattended include/discard-ignored flag is used. Refusals stay in triage for a person to resolve.
3. Push a `TEMP-SHELVED:` marker under `shelved/<user>/<name>`, also for clean/pushed branches. Dirty work uses
   the shelver's temporary index, preserving the original index and PR branch. Verify the exact SHA on origin.
4. If the original branch has an open PR, add `stale` and `shelved` labels. Otherwise create a **draft** PR from
   the recovery shelf, titled `TEMP: Shelved <original-branch>` and labelled `shelved`. The body names the branch,
   shelf, verified SHA, restore command and trace. A retry reuses an open recovery PR for that shelf; it never
   reopens a deliberately closed recovery PR. Existing label definitions are retained.
5. Re-check activity and compare files/HEAD with the verified snapshot after the GitHub operations, then let the
   existing shelver remove the worktree. Ask Orca to remove the now-missing workspace entry from its sidebar.
   A push, verification, PR or activity-check failure keeps the worktree. A sidebar failure retains the shelf,
   PR and journal, and is reported; the existing finished-worktree job can clear the missing entry on its next run.

The job does not change or delete remote original branches, close PRs or merge recovery PRs. The shelf is a
recovery artifact. Restore it with `rig estate unshelve <shelf-name>` from the owning clone before resuming work.

## Triage and traces

Every worktree gets a typed triage row, including branches without PRs, refusals and failures. Applied sweeps
atomically replace `orca-shelving.json` and append phase records to `orca-shelving.json.jsonl` with timestamps,
worktree identity, branch, shelf ref, verified SHA, PR URL and trace ID. Records are flushed before deletion.
The shelver also retains its established `~/.estate-shelved.jsonl` recovery ledger. Remote refs and marker trailers
remain authoritative even if a local report is removed.

Incomplete and refused rows appear in rig web's estate attention/decisions feed, including no-PR branches.
Successfully shelved branches without PRs now have draft PRs and enter the ordinary PR triage refresh. A coordinator
failure does not stop subsequent Chrome cleanup. Exit 2 means Orca is unavailable; the coordinator retries later.

The implementation follows [desired → observed → plan → apply](../target-architecture.md#one-shape-for-every-feature-desired--observed--plan--apply)
from the #30 architecture plan, reusing the existing shelver and external-state ports. The policy is rig data,
not a new Orca automation or external service. All tooling remains free/open source.

## Local performance measurement

On this Mac (Apple Silicon, .NET 11 Debug CLI), a read-only sweep on 2026-11-21 triaged 190 linked worktrees:
161 kept, six ready to shelve and 23 needing decisions. This is a later live snapshot than the threshold audit above.
`/usr/bin/time -l` measured **111.29 s wall time**, **12.45 s user + 11.38 s system CPU**, and
**97,648,640 bytes (93.1 MiB) maximum RSS**. That is about **21.4% of one core averaged across the sweep**.
The time tool reports invocation/child CPU; its peak RSS is not the sum of every simultaneous helper's RSS.
The process exited, leaving **zero additional idle RSS/CPU**. This measures observation and planning; pushes,
PR creation and removal are covered by real-Git contract tests, not a destructive live performance trial.
No companion MCP server, new resident scheduler or paid dependency is introduced.

A separate fresh-process `--help` invocation measured **400 ms** wall time and **74.2 MiB maximum RSS** with a warm filesystem cache. Under the full parallel test load an earlier sample took 3.90 s; this is not a disk-cache-cold or native Release startup claim.
