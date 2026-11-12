# Machine jobs (`desktop-manager jobs`)

Everything that keeps this machine aligned runs from one registry
(`src/desktop_manager/jobs/registry.py`). Two scheduled tasks drive it, so there
is one place to see what runs at logon, what runs every 15 minutes, and what runs
daily.

```bash
rig jobs status                        # each job: when it runs, last outcome, whether the next tick takes it
rig jobs status --trigger logon
rig jobs last                          # the latest sign-in run: every job, why it ran or not, how it went
rig jobs last --trigger tick
rig jobs log chrome-organise
desktop-manager jobs run --dry-run     # what a tick would run now
desktop-manager jobs run --only estate-risk   # one job, due or not
desktop-manager jobs install           # register the tasks (run from the runtime copy)
```

`status`, `last` and `log` run in C# (`src/Rig.Cli/Jobs`, over `src/Rig.Core/Jobs`); `run`,
`install` and `uninstall` are still Python, and `rig jobs <those>` hands them to `desktop-manager jobs`.
Both sides read and write the same `state.json` and `last-<trigger>.json`, held byte for byte to
`tests/Rig.Cli.Tests/Jobs/jobs-oracle.json`, which Python records.

## Scheduled tasks

`jobs install` registers three tasks in the Task Scheduler folder
`\Shared Utilities\`, and removes the separate tasks they replace (`Workspace
Restore`, `Chrome Tabs Organise`, `Workspace Display Guard`):

| Task | When | Runs |
|:-----|:-----|:-----|
| `Logon` | at sign-in | waits for the Explorer shell, then `jobs` on the `logon` trigger |
| `Tick` | every 15 minutes | `jobs` on the `tick` trigger |
| `Display Guard` | at sign-in, for the whole session | `workspace guard` (see [startup-and-display-guard.md](startup-and-display-guard.md)) |

Install from the [runtime copy](runtime.md), so the tasks follow `current`
through later `runtime install` and `runtime rollback`:

```bash
"%LOCALAPPDATA%\shared-utilities\runtime\current\.venv\Scripts\desktop-manager.exe" jobs install
```

Run from a checkout, `jobs install` uses the main checkout's `.venv` and refuses a
linked worktree, because the tasks are machine-wide.

## Jobs

A run takes its jobs in registry order, one at a time:

| Job | Triggers | Cadence | What it does |
|:----|:---------|:--------|:-------------|
| `restore` | logon | every run | `workspace restore --defer-logon-commands` against the plan |
| `claude-sessions` | logon | every run, flag `claude-session-restore` | `claude-sessions startup`: resume sessions the shutdown cut off, start `claude` in the deferred terminals |
| `chrome-organise` | tick | every run | `chrome-tabs organise --unattended`, waiting up to 10 minutes for an idle machine |
| `align` | tick | every run, idle 3 min | `workspace align`: put open plan windows back on their desktop and slot, launching nothing |
| `estate-sync` | logon, tick | daily | `estate sync`: fetch and prune every clone under the code root (`C:\code`: personal, the org and work folders, external) |
| `estate-risk` | logon, tick | daily | `estate risk`: report uncommitted or unpushed work across the code root |
| `estate-trim` | logon, tick | daily | `estate trim --apply`: delete `node_modules`, venvs and build output in worktrees idle 7+ days, across the code root |
| `estate-orca` | logon, tick | daily, retried while Orca is down | `estate orca --apply`: register every cloned managed project in the personal Orca, in its group |
| `orca-sync` | logon, tick | daily, retried while Orca is down | `workspace orca sync --apply`: apply `config/orca.json` (agent default args, repo colours, scripts, base refs) |
| `workspace-maintain` | logon, tick | every run | the workspace repo's `maintain run --due`, which tracks its own due tasks |
| `otel-collector` | logon, tick | every run | `otel-stack collector ensure`: keep the local collector listening and prune its files past 7 days ([otel-stack.md](../otel-stack.md)) |
| `workload-sample` | logon, tick | every run | `workload sample`: record memory use by app, Claude sessions, large processes and bursts; keep 7 days ([workload.md](../workload.md)) |

- **Cadence.** `every run` runs whenever its trigger fires. `daily` runs once its
  last start is 20 hours old. `once per boot` runs on the first matching trigger
  after a boot.
- **Flagged jobs.** A job with a `feature` runs only while that
  [rig flag](../feature-flags.md) is on, and `rig jobs status` shows `flag <name> off`
  otherwise. Naming the job with `--only` runs it anyway.
- **C# jobs.** The `estate`, `workspace orca` and `otel-stack` jobs run `rig <group> …` from the
  runtime copy, which starts the C# CLI with `dotnet run`. The task's pythonw inherits the logged-on
  user's environment, so `dotnet` must be on the user or system `PATH` (the .NET installer puts
  `C:\Program Files\dotnet` on the system one). Without it those jobs fail and say so in their log.
- **Optional jobs.** A job whose required paths are missing is skipped, not failed.
  `worktree-reclaim` and `workspace-maintain` run the work workspace repo's own
  CLI, and are registered only when `tools.estate.workspace_repo` in
  `~/.workspace/config.json` names that checkout (a folder under the code root, or
  a path). Without it, those two jobs are not listed at all.
- **Isolation.** Each job is a subprocess with no console window and its own
  timeout. A failing or hung job does not stop the jobs after it.
- **Idle-gated jobs.** A job with `idle_minutes` is skipped, with no outcome
  recorded, unless the keyboard and mouse have been untouched that long when its
  turn comes. `align` follows `chrome-organise`, which waits for an idle machine, so
  it usually runs on the same tick. `jobs run --only align` runs it regardless.
- **Exit codes.** Any non-zero exit is recorded as a failure. `claude-sessions`
  exits 3 when sessions were left over (run `claude-sessions recover`). `align`
  exits 3 when it could not move a window, such as one running as administrator,
  and does so on every tick until that window moves or closes.
- **Retries.** A job's `retry_exit_codes` mean "could not run yet". When its last
  run this boot exited with one, each tick runs it again, even if it is a logon-only
  job, until it exits otherwise. `claude-sessions` exits 4 when the workstation was
  locked at logon, so its sessions come up on the first tick after unlocking.
  `restore` and `claude-sessions` both exit 4 when fewer screens are live than the
  plan needs, so they run once the screens are back.
  `estate-orca` and `orca-sync` exit 3 when the personal Orca is not running, so
  they run on the first tick after it starts. Both target `orca-personal` on
  Windows (the estate's instance), not the work instance a scheduled task would
  otherwise default to.
- **One run at a time.** A run holds `run.lock`. A logon run waits up to 30 minutes
  for a tick that started first. A tick that finds another run in progress skips.

To add a job, append it to `default_jobs` with its triggers, cadence, timeout and
any `requires` paths.

## Tracing a run

Every run has an id, `<trigger>-<YYYYmmdd-HHMMSS>`, such as `logon-20261011-080201`.
Every `coordinator.log` line names it, it heads that run's section of each job log
(`--- <time> [<id>] <command>`), and the job sees it as `SHARED_UTILITIES_JOB_RUN`.

- **Summary.** Each outcome keeps the last line the job printed, such as
  `align: 0 moved, 11 in place`. `coordinator.log`, `rig jobs status` and `rig jobs last`
  show it beside the exit code.
- **Why it failed.** A failed job also keeps the last 12 lines of its output.
  `coordinator.log` prints them under the failure, and `rig jobs status` and `rig jobs last`
  print them below the table. A job that writes its narrative to its own log, as
  `chrome-organise` does to `chrome-tabs\organise.log`, is read from that log's
  last section instead.
- **The latest run.** `last-logon.json` and `last-tick.json` record each trigger's
  latest run: every job it ran or skipped, with the reason, and each outcome. The
  record is saved after every job, so a run killed part-way shows as not finished.
  `rig jobs last` reads it.
- **Run history.** Every job run also appends one line to `runs.ndjson`: job, run
  id, start, duration, exit code, trigger, error, its output file, and the trace
  and span ids it ran under. That run's slice of the job log is saved as
  `runs\<job>\<run_id>.log`, capped at its last 256 KB. The history keeps the
  newest 2,000 runs (trimmed once it passes 2,500) and each job its newest 50
  outputs. `state.json` stays the scheduler's source of truth; the history is for
  people, and a write failure there never fails a run.
- **Spans.** Each job runs inside a `job <name>` span (scope
  `desktop_manager.jobs`, attributes `job.trigger`, `job.run_id`,
  `job.returncode`), written by the scheduled coordinator as the
  `desktop-manager-jobs` tool. A failed job's span has error status.
- **Traces.** Jobs run from a runtime copy write their spans to
  `%LOCALAPPDATA%\shared-utilities\traces\<tool>\`, outside every copy, so they
  outlive a `runtime install`. Each logon run deletes trace files there older than
  14 days. Chrome organise keeps its own traces directory.

## Where things live

Everything is under `%LOCALAPPDATA%\shared-utilities\jobs\`:

- `coordinator.log`: each scheduled run's decisions, which jobs ran or were skipped and why, and why a job failed.
- `logs\<job>.log`: each job's own output, one section per run. One previous generation is kept as `.log.1`.
- `state.json`: each job's last start, duration, exit code, error, summary and failure detail.
- `last-logon.json`, `last-tick.json`: each trigger's latest run.

- `runs.ndjson`: one line per job run, newest last (see **Run history**).
- `runs\<job>\<run_id>.log`: each recorded run's own output.

Restore still logs to `workspace-restore\logs`. Chrome organise still logs to
`chrome-tabs\organise.log` and writes its traces to `chrome-tabs\traces`.

## In `rig web`

`src/desktop_manager/jobs/web.py` mounts four endpoints into the rig
web server (`mounted_routes()` in `web_cli.py`): three reads and one write.

| Endpoint | Answers |
|:---------|:--------|
| `GET /api/ops/jobs` | every registered job (name, description, cadence as text, triggers, timeout) with its last outcome from `state.json`, or `null` |
| `GET /api/ops/runs?job=<name>&limit=1..1000` | job runs, newest first (default 200), optionally of one job |
| `GET /api/ops/runs/<job>/<run_id>/output` | what that run printed; `output` is `null` when nothing was saved or it has been pruned |
| `POST /api/ops/jobs/run` with `{"job": <name> \| null}` | starts `jobs run --trigger tick` detached, for one job (due or not) or every due job, and answers `{"job", "pid"}` at once; 404 for a job the registry lacks |

Runs made before the history existed still appear: a job's last outcome in
`state.json` that the history does not hold is listed with an empty `trace_id`,
and its output is read from its section of the job log. The output endpoint
answers 404 unless the job is in the registry, the run id has the
`<trigger>-<YYYYmmdd-HHMMSS>` shape, and the history or `state.json` records that
run; files are only read from paths the history wrote.
