# C# port: command ownership and retirement

Tracking: [#85](https://github.com/rtkelly13/rig/issues/85), ADR 0022, and
[#30 G5](../superpowers/plans/2026-10-29-rig-simplification-stack.md#pr-g5-one-cli-root).
This is the enforcement part of the port, alongside the platform acceptance proposal on #85.
The parent session owns forward progression; this worktree owns alignment/deprecation and the ownership gate.
The already completed trace-report retirement below is preserved in this PR so the parent can avoid overlap.

## The inventory is inspected, then reviewed

[`config/command-ownership.json`](../../config/command-ownership.json) contains an exact registration
per surface and capability/platform. It is a review ledger, not the discovery mechanism.

```text
dotnet build rig.slnx -c Release
dotnet run --project tools/command-ownership -- --help
dotnet run --project tools/command-ownership --no-build -c Release -- . src/Rig.Cli/bin/Release/net10.0/rig.dll --inventory
dotnet run --project tools/command-ownership --no-build -c Release -- . src/Rig.Cli/bin/Release/net10.0/rig.dll --base origin/main
```

The inspector combines:

- The **production composition root**, traversing System.CommandLine objects after construction, including
  commands registered in loops, actions on groups, and aliases. The Windows Chrome registration branch is expanded
  with a driver that refuses native execution on this Mac. Inspection invokes no command actions.
  Pre-parser agent shortcuts, the bare launch, model shortcuts and actual external agent targets are exported too.
- Python AST discovery of Typer/Click factories, followed by **runtime traversal** of their composed command trees,
  including hidden commands and dynamically listed Click children. Unmounted apps are inspected too. Unsupported
  local factories, stateful callback closures, failed imports and unresolved children fail discovery.
  New console targets, GUI/plugin entry points and executable projects need explicit runtime adapters; a new ledger
  row cannot silently approve an unsupported framework. Named Python shims must resolve to their C# command.
- Every Python `__main__`/guard under core `src/`, console script target, development Python file, C# `Main`,
  top-level C# file app, C# command root and project (Roslyn syntax, project XML and evaluated MSBuild output types),
  Node `bin`/package scripts and standalone Node scripts, tracked executable modes, and checked-in
  command/shell/PowerShell/LaunchAgent launchers. Launcher contents are fingerprinted.
- Each actual `Forwarded.Commands` route expanded into the inspected Python leaves. Probe arguments go back
  through **`Forwarded.Owns` itself**. A migrated action that still dispatches to Python fails discovery.
- Both composed job registries, including the optional workspace jobs, using inert fixture paths. They are
  callers, not independent implementations. No job is run. The MCP server's actual tool list and the Python web
  child's mounted route callables are inspected without serving requests. Legacy web callables are fingerprinted
  as opaque dispatch units; this is not proof of individual HTTP endpoint parity.

The Python probe is a development inspection adapter for the legacy runtime, not new rig product behavior.
The ownership rules, source inspection and gate runner are C#. Remove the probe with the last core Python routes.

`Id` identifies the observed registration. `Capability` is its normalized command path; `Owner` is the canonical
implementation. A Python wrapper counts as an alias only when its inspected AST is exactly a whole-line dispatch
to the real `csharp_cli.run`, without branches, fallback or independent behavior. Reusing one callback under
several names also shares one implementation. C# aliases come from the actual command object's alias collection.
New registrations need reviewed ledger rows. Metadata must match discovery, so changing a ledger owner or platform
cannot conceal an independent implementation.

### Current owners and callers

| Capability/surface | Canonical implementation and callers | Source state / remaining condition |
| --- | --- | --- |
| C# registrations (`cs:` rows) | `rig`, plus 13 Python console-script compatibility spellings | Switched in source; shared tests/CI evidence does not establish native acceptance or deployment |
| `align` | Python `desktop-manager workspace align` | Not ported; Windows desktop acceptance required |
| `triage` | Python `rig.triage.cli` | Not ported; two runtime-discovered leaves |
| `c4` | Python `rig.c4_okf.cli` | Not ported; five leaves; open #78 touches this area, do not duplicate it |
| `jobs` | C# status/last/log/run; Python install/uninstall | Disjoint subcommands; #209 owns installer/runtime progression |
| `web` | C# public host, Python service/database/MCP/upstream children | Mixed ownership; #207 owns connectivity cutover; opaque child-route registrations remain explicit |
| `chrome-tabs` | C# traces everywhere, C# native actions on Windows, Python native actions on macOS and organise | Windows duplicate registrations are the exact baseline below; #206 owns macOS native actions |
| `chrome-tabs traces runs/show/summary/prune` | C# only; legacy spelling dispatches the whole line | Legacy report CLI and 462 lines of reader/report code deleted; writer/daily retention still have scheduled Python callers |
| `desktop-manager workspace`, `claude-sessions`, `runtime`, `work` | Inspected Python leaves in the ledger | Not ported unless separately recorded; keep their exact caller and removal condition visible during each cutover |
| Core Python modules, development file apps, Node scripts and launchers | `module:`, `python-file:`, `main:`, `file-app:`, `node:`, `launcher:` rows | Development tooling is inventoried separately from product CLI behavior |
| Web/MCP and jobs | `web-child:`, `mcp:`, `python-job:`, `cs-job:` rows | Children/callers remain Python where named; source deletion requires switching them too |
| Brand pipeline | `tools/image-drift` | Intentional Python exception, its own project/gates (ADR 0022) |

There are 14 current console scripts, rather than the issue's original 15. The ledger follows actual registrations.
`desktop-manager help` describes the desktop CLI, so it is a separate capability from `rig help`.
`desktop-manager workspace` controls desktop plans; `rig workspace` manages development configuration.

## Refusal rules

One independent implementation owns each leaf capability on each supported platform. Partial ports must be
disjoint; a spelling alias delegates to the same implementation. New unregistered executables, duplicate owners,
unresolvable aliases, stale forwarding and retired source/entries fail the gate. Tombstones are permanent.

The initial baseline has exactly **ten** capability/platform overlaps: seven Windows Chrome commands
(`group`, `install`, `list`, `prune`, `rank`, `saved-groups`, `uninstall`) and the web-launch callback on Linux,
macOS and Windows. Each entry names both implementations. C# is the desired canonical owner for these rows.
The legacy registrations must disappear or become verified thin dispatchers; a Mac Chrome cutover must remove
its obsolete Python registrations in the same PR rather than add macOS baseline allowances.

This narrow temporary baseline is explicitly authorized for #85; it does not relax other architecture gates.
CI compares it with the PR base: entries can only disappear, never gain an owner, capability or platform. Resolved
baseline entries must be deleted, so an allowance cannot linger and permit a later resurrection. New retired entries
and deleted source paths are added as slices land. There is no refresh/accept-baseline command in CI.

`command-ownership` runs even for docs-only PRs, on GitHub's **merged result**, and feeds the required aggregate
`ci` check. It also verifies current main is an ancestor of that result. Main's inspected ruleset requires strict
up-to-date `ci` checks with no bypass actors. A concurrent merge therefore requires a fresh result; a stale branch's
green ownership check is insufficient. The workflow does not grant write permissions or change branch protection.

This proves **command registration and dispatch ownership**, not arbitrary semantic equivalence. New names can
hide equivalent business code; reviewers must assess capability naming and adapters. Source reflection cannot
prove future arbitrary plugin frameworks complete. A new framework, extra composition root or uninspectable
registration needs a discovery adapter and regression test; never suppress discovery failures. Installed machines
and external repositories are a separate rollout inventory, not inferred from CI's source tree.

## Evidence and native acceptance

The initial ledger contains **460 inspected surfaces**. Local validation on macOS at the PR's source state:
the full C# solution run passed 4,625 tests with 25 explicit skips; the final CLI rerun passed 918/918 and the
final discovery rerun passed 8/8. Combined line/branch coverage holds the unchanged **95%** floor with
only CI's existing generated-interop filters (exact final measurement on the PR). The focused Python suite passed
106/106, including all organise trace-consumer assertions after removing their dependency on the retired reader.
The published trace artifact
passed eight smoke cases with an empty PATH and an unavailable Python checkout, including destructive pruning
only inside `temp/acceptance`. Strict types, import contracts, the ratchet, lint/format, security, workflow audit
and documentation audit passed. Windows CI and interactive procedures remain separately recorded on #85.

The trace slice preserves the C# `ChromeTracesTests` parity assertions and `ChromeTabsCommandTests` argument,
JSON, ordering, errors, exit codes and retention cases. The Python tests retain writer/exporter/daily-retention
behavior and exercise whole-line alias dispatch, including unknown arguments and help. The ownership suites test
duplicate/unregistered/retired refusals, disjoint ports, thin aliases, baseline expansion, dynamic C# registrations,
new executable sources, unsupported discovery and stale fallback. No coverage exclusions or lowered gates are added.

Record passing commands, commit, coverage and skips in the PR and #85. A built/published trace-report smoke run
must use a fixture directory and an empty PATH with Python forwarding unavailable. No interactive desktop is
required for reports; native actions are outside this slice. Compilation and skipped native tests are not acceptance.

Read-only local rollout observation on 2026-11-13: `~/.local/bin/rig` resolves to the native .NET tool at
`~/.dotnet/tools/rig`. There is no `desktop-manager` or `chrome-tabs` on this shell's PATH. The two rig LaunchAgents
are `dev.rtkelly.rig.open-knowledge` (Node/open-knowledge) and `dev.rtkelly.rig.otelcol` (native collector);
their plist arguments do not launch core Python. This does not prove all estate configs or other machines migrated.
No installed tool, LaunchAgent or scheduled task was modified for this PR.

Named remaining acceptance procedures, all run from a reviewed/published artifact in a disposable environment:

| Procedure | Runnable entry and required environment | Evidence/expected result |
| --- | --- | --- |
| `WIN-DESKTOP` | Interactive Windows, disposable plan/windows: `desktop-manager workspace validate-plan --config <plan>` then `desktop-manager workspace restore --config <plan> --dry-run`, then an explicitly scoped restore/audit/align | Record Windows build, permissions, before/after layout, exact command, logs and restored state. Real Win32/UIA placement is pending, not a hosted-runner pass |
| `WIN-JOBS` | #209's reviewed runtime in a disposable Windows account: `rig jobs install --help`, `rig jobs status`, its named task-install/logon acceptance | Record task XML, target artifact, due/skip behavior, background console behavior and uninstall/cleanup; no production task changes merely to test source |
| `WIN-CHROME` | Interactive Windows, dedicated dev Chrome profile: `rig chrome-tabs list --profile <dev> --json`; `rig chrome-tabs prune --profile <dev> --dry-run`; controlled prune/group/saved-groups on disposable tabs | Record actual UI effects, focus restoration and cleanup; delete the seven losing Python registrations after callers/acceptance permit |
| `MAC-CHROME` | #206 artifact, repository dev browser profile: `rig chrome-tabs list --profile <dev> --json`, controlled prune/group with disposable windows/tabs | Record Accessibility/Automation grants, native calls, actual changes, cleanup and restored focus. No daily Chrome profile |
| `WEB-MCP` | Disposable DB/ports, no production service: `rig web --port <free> --no-open`; `rig web mcp` over stdio | Browser → route → data → response and real initialize/tools exchange; identify each remaining Python child, preserve protocol stdout |
| `ROLLOUT` | After platform acceptance: inspect runtime/PATH/task/LaunchAgent/agent-config/estate-repo callers; use the documented installer for that slice | Record machine and artifact SHA. Keep the last working deployed runtime until replacement acceptance. Source deletion alone never establishes rollout |

Windows CI can be triggered from this Mac by the `ci:windows` label. It supplies compilation/noninteractive
evidence only. These procedures remain pending until a native run is recorded. Do not close #85 on this PR.
