# Background maintenance on macOS

`rig jobs install` registers `~/Library/LaunchAgents/dev.rtkelly.rig.jobs.plist`.
One launchd job runs a finite `rig jobs run --trigger tick --scheduled` at login
and every 15 minutes. There is no resident coordinator and no `KeepAlive` loop.
Existing run locking prevents overlapping sweeps. Jobs run serially, with their
own timeouts and persisted daily cadence.

Windows desktop restore, align and display guard are not scheduled on Mac. This
pass covers Chrome, the shared web dashboard and estate maintenance, without
adding window/Spaces restore APIs.

## Install and inspect

Use a published runtime, not `dotnet run` or a feature worktree, as the scheduled
executable. Runtime installation builds a pinned commit and keeps a previous copy
for rollback. For normal deployment after this change lands:

```bash
rig runtime install
"$HOME/Library/Application Support/rig/runtime/current/rig/rig" jobs run --dry-run
"$HOME/Library/Application Support/rig/runtime/current/rig/rig" jobs install
"$HOME/Library/Application Support/rig/runtime/current/rig/rig" jobs status
"$HOME/Library/Application Support/rig/runtime/current/rig/rig" jobs last
```

To test a reviewed feature commit before merge, install from its checkout with
`runtime install --ref HEAD --no-fetch --repo <checkout>`. Installation validates
the executable before changing launchd registration. No Windows plan or Python
launcher is required. Registering the LaunchAgent starts its first tick immediately;
repeating an unchanged installation leaves a loaded registration alone.

`jobs status` reports whether the plist and launchd registration exist. A finite
job normally has no PID between runs; that is healthy. On Mac, `jobs last` defaults
to the tick trigger. `jobs uninstall` removes only the coordinator; separately
installed web/collector/knowledge services remain installed.

The runtime `current` link is used for scheduled jobs and web registrations, so
future installs and rollbacks change the executable on its next launch. The estate
root comes from `CODE_ROOT` (normally `~/code`), never the parent of the worktree
from which a runtime was installed. The LaunchAgent records the operator's PATH,
HOME and CODE_ROOT because launchd does not source an interactive shell profile.

The installer preserves an existing foreign `rig` launcher on PATH. Check where
`rig` points before assuming it is the installed runtime; the absolute command
above always selects the pinned copy. A global .NET tool remains accessible as
`~/.dotnet/tools/rig` independently of the runtime.

## What runs

- `web`: ensure the shared dashboard on `127.0.0.1:8090` every tick. This uses the
  existing service manager and one web LaunchAgent, with HTTP MCP at `/api/mcp`.
  It does not install a second host on 8092 or an MCP proxy per agent.
- Daily estate jobs: sync/prune remote refs, report risk, trim regenerable output
  in idle worktrees, align Orca registration/settings, prune old caches, remove
  finished worktrees/branches conservatively, refresh the PR/issue triage inbox,
  and write the decisions feed. The explicit code root includes personal, org,
  work and other clone folders under it. Configured work roots outside it are
  also included in the decisions feed.
- Orca worktree deletion runs through Orca's own removal command with its active,
  dirty, unmerged and unknown-PR protections. Generic tidy follows it for proven
  merged worktrees/branches, passing `--merged` and selected checks. No `--force`
  or general stale-temp deletion is scheduled. Protected ignored notes, dirty
  work, active processes and local-only commits retain the existing refusal rules.
- The OpenTelemetry collector is checked each tick; a healthy existing collector
  is reused. Optional work-workspace commands use `.venv/bin/python` on Mac and
  are skipped if their configured interpreter/entry point is missing.
- Chrome runs last so its UI work cannot delay the estate reports. Its native Mac
  driver performs call/presence checks. `--wait-minutes 0` avoids keeping a process
  resident waiting for idle: grouping deferred while the machine is busy is tried
  at a later tick. Chrome Automation/Accessibility permissions still apply.

A failed job records its output and does not prevent later jobs from running.
Orca absence is retryable on a later tick. Decisions that require a person remain
in the feed; the scheduler does not shelve uncommitted work or force removals.

## Logs and retention

State, run history and per-job logs live under
`~/Library/Application Support/rig/jobs/`. The scheduler's stdout/stderr go to
`launchd.log` and `launchd-error.log`; `coordinator.log` narrates each run and
rotates past 512 KiB. `jobs log <name>` reads a job's output. Mac ticks also prune
trace files older than 14 days; they never wait for a Windows Explorer shell.

The web host is the intentional resident process. Inspect it with `rig tools status
web`. Housekeeping consumes CPU and memory while a sweep runs, then its processes
exit. Daily jobs are not repeated on every tick once their recorded run is recent.
Use `jobs run --dry-run` to inspect the current due plan, and `jobs run --only <job>`
to retry one job manually, retaining the same run lock.

This implements macOS support in the existing jobs/launchd adapters, following
[target architecture: one external-state port and one code-root resolver](../target-architecture.md#ports-one-per-kind-of-external-state).
