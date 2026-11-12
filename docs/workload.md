# Workload sampler

Every 15 minutes rig writes one JSON line describing what is using memory: free
RAM, memory by app, Claude Code sessions, the large processes and any bursts of
one program. A day of samples answers "why was the machine slow at 3 pm" and
"how many sessions can I run" with numbers instead of a guess.

```bash
rig workload sample              # take one sample now; prints one summary line
rig workload status              # folder, day files, newest sample, service state
rig workload summary [--hours N] # read the last N hours back (default 24)
rig workload service install | uninstall | status   # macOS LaunchAgent
```

## What a sample records

One line per sample, under 4 KB, in `<rig data>/workload/YYYY-MM-DD.jsonl`
(UTC date). `<rig data>` is `%LOCALAPPDATA%\shared-utilities` on Windows and
`~/Library/Application Support/shared-utilities` on macOS.

| Field | What it holds |
|---|---|
| `v`, `ts`, `host`, `os` | format version, UTC time to the second, machine name, `windows` or `macos` |
| `ram` | `total_mb`, `free_mb` (available physical memory) |
| `processes` | `count`, `private_mb` and `working_set_mb` summed over every process |
| `apps` | the top 10 apps by private memory, then `other`: processes, private MB, working set MB |
| `claude` | Claude Code `sessions`, `session_mb` (min, median, max of each session process), `tree_mb` (the same over each session's whole process tree), and `children`: the 10 commonest program names running under sessions, with counts |
| `large` | up to 10 processes over 1 GB private or working set: pid, name, owning app, MB, start time, and the command line cut to 160 characters with secrets redacted |
| `bursts` | up to 10 programs running more than 8 copies, with the owning app and their total private MB |

A process belongs to the app of its nearest ancestor (or itself) that matches
an entry in
[`src/Rig.Core/Workload/workload-apps.json`](../src/Rig.Core/Workload/workload-apps.json),
so a `dotnet build` started by Claude Code counts as Claude Code. An entry matches
by executable name or by a path fragment. Add an app there; the file is embedded
in the build. `burst_ignore` lists programs that always run many copies
(`svchost`, browser renderers) so they are never reported as bursts.

Command lines are redacted before they are cut: values after flags such as
`--token` or `--password`, `key=value` pairs whose key names a secret, URL
credentials, bearer tokens, well-known token shapes (GitHub, `sk-`, Slack, AWS,
JWT) and any run of 40 or more token characters become `***`. The rules are in
`src/Rig.Core/Workload/CommandLines.cs`.

Private memory is what a process has committed and nothing else can share. It is
the number that runs a machine out of memory, so apps, sessions and large
processes are ranked by it. Working set is what is in RAM right now; it is
recorded too, because some processes (the WSL VM, `Memory Compression`) are large
in one and small in the other.

## Scheduling

**Windows**: the jobs coordinator's `workload-sample` job runs
`rig workload sample` at logon and on every tick (15 minutes), with a 2-minute
timeout. It arrives with `rig jobs install`; see [jobs](desktop-manager/jobs.md).

**macOS**: the `dev.rtkelly.rig.workload` LaunchAgent, from
`rig workload service install`. It runs `rig workload sample` at load and every
900 seconds (`StartInterval`), with `ProcessType` Background, low-priority IO and
`Nice` 10, and no `KeepAlive`: nothing stays running between samples. launchd
gives agents no shell PATH, so the plist sets one: the folders holding `rig`,
`dotnet` and `uv`, then the system folders. Install needs `rig` on PATH
(`rig runtime install`). Output goes to `<rig data>/logs/workload.log`.

## Retention: 7 days

Every sample deletes day files last written more than 7 days ago, never today's.
It is the same rule as the [collector's files](otel-stack.md#retention-7-days),
from the shared `Rig.Core.Files.AgedFiles`. A day is about 250 KB (96 lines of 2–3 KB), so a week is under 2 MB.

## Cost

Measured on the Windows work machine (64 GB, about 470 processes), 10 runs each,
CPU being the process's own user and kernel time:

| Command | Wall (median) | CPU (median) | Runs |
|---|---|---|---|
| built `rig workload sample` (Release) | 0.38 s | 0.38 s | 10 |
| built `rig --help` (Release), startup only | 0.23 s | 0.21 s | 10 |
| `dotnet run --project src/Rig.Cli -- workload sample` | 2.56 s | 3.25 s | 3 |
| `dotnet run --project src/Rig.Cli -- --help` | 2.31 s | 2.89 s | 3 |
| `uv run rig workload sample` (the shim, whole tree) | 3.00 s | n/a | 3 |
| `uv run rig --help` (the shim, whole tree) | 3.01 s | n/a | 3 |

The sample itself costs about 0.15 s of wall and CPU over plain startup.
Everything else is the path a tick takes to reach the C# CLI: the Python shim
and `dotnet run`'s up-to-date check, 2.3–3 s, which every rig job on the tick
already pays. At one sample every 15 minutes that is under 15 s of CPU a day for
the sample itself.

Reading the process table is one `NtQuerySystemInformation` call on Windows and
one `ps` on macOS. Command lines are read only for the few large processes.

## Reading it back

`rig workload summary --hours 8` prints the free-RAM range, the peak private
total and the apps at that peak, the Claude session range and per-session size,
each large process from first to last sample with its growth in MB/h, and the
bursts with how many samples they appeared in.

The files are plain JSON lines, so anything reads them:

```bash
# DuckDB: free RAM and session count through the day
duckdb -c "select ts, ram.free_mb, claude.sessions from read_json_auto('$LOCALAPPDATA/shared-utilities/workload/*.jsonl') order by ts"
# jq: every burst today
jq -c '.ts as $t | .bursts[] | {ts: $t, name, count, app}' 2026-11-11.jsonl
```

## Baseline: 2026-11-11

The first day sampled on the Windows work machine, 09:54 to 18:27, 102 samples.

- Free RAM ranged from 19.7 to 28.7 GB; the peak private total was 44.3 GB.
- Claude Code sessions cost about 0.8 GB each (586–1,040 MB, median about
  790 MB). They went from 6 to 14 over the day, and 14 sessions came to about
  10.8 GB.
- The spikes came from builds and tests run inside sessions, not from the
  sessions themselves: `VBCSCompiler` at 3.5 GB with 19 `dotnet` processes, and a
  pytest run with 14 `python.exe` (one at 2 GB).
- The steady users: the WSL VM (`vmmemWSL`) 5.5–5.8 GB, Chrome 4–7 GB,
  Orca 3–4 GB, VS Code about 3.4 GB.
- The Stream Deck 7.6.1 `QtWebEngineProcess` leaked about 400 MB/h, reaching
  about 2.8 GB; probably the Dynamic Clocks plugin.

So: cap concurrent builds and test runs, not sessions. Sessions are cheap and
predictable; a build or a parallel test run is what takes the free memory down.
