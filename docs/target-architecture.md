# Target architecture: what rig should look like

This is the shape rig is converging on. It describes the end state, not the work to get there. When a change
moves code toward it, cite the section. When a change moves away from it, either don't make it or update this
page in the same PR.

## What rig is

rig is a set of small tools over three kinds of external state: **git clones** under the code root,
**GitHub** settings and pull requests, and **the desktop** (windows, virtual desktops, Chrome, scheduled tasks).
It also owns a few local pipelines that turn inputs into checked-in files (brand assets, AI-tool config).

Every feature reads one of those states, compares it with what is wanted, and either reports the difference or
applies it. Code that does none of those three things doesn't belong in rig. Personal-life automation goes to
`hearth`. A project's own build goes to that project's repo. Experiments go to `experiments/` until they are
scored against something.

## Language

rig is C# on .NET 11 ([ADR 0022](adr/0022-csharp-is-rigs-primary-language.md)). The brand-asset pipeline
(`tools/image-drift`) is the one Python project, kept because its output must stay byte-identical
([ADR 0009](adr/0009-deterministic-svg-render-pipeline.md)); it has its own gates and is not part of the C#
layering below.

## One shape for every feature: desired → observed → plan → apply

Each feature has four parts:

1. **Desired**: declared in `config/` as data. Code holds no policy, so no per-repo maps, hard-coded flags or
   personal paths.
2. **Observed**: read through a port (below). It is decoded once, at the boundary, into named types.
3. **Plan**: a pure function from (desired, observed) to a list of typed `Change` rows. It has no I/O, so it
   is trivially testable.
4. **Apply**: walks the plan through the same port. It is idempotent, so a second run produces an empty plan.

Every command therefore works the same way:

- **Flags:** `--dry-run` prints the plan, `--json` emits it, and `--apply` executes it.
- **Exit codes:** 0 when clean, 1 on drift, 2 on error.
- **Status is data.** Callers branch on the typed plan or diff, never on rendered text.

The rendering layer, whether a Spectre.Console table or a text diff, sits on top and is never parsed back.

## Ports: one per kind of external state

The outside world is reached through a few narrow interfaces. Product code receives them as constructor or method
arguments and never news them up inline.

| Port | Real adapter | Test fake |
|---|---|---|
| `IProc` (process runner) | `Rig.Core` process adapter | `FakeProc` |
| `IGitHubApi`, GraphQL PR reads | GitHub CLI and API adapters | `FakeGitHub`, `FakeGraphQlPrs` |
| `IChromeTabActions`, desktop and window ports | UIA, Win32 and AppleScript adapters | `FakeChromeTabActions`, `FakeDesktop` |
| Filesystem and roots | the code-root resolver | temp directories, `HOME` pointed at one |

The rules that follow from this:

- **One process runner.** There is no other `Process.Start` for git or gh. It always applies timeouts, UTF-8
  decoding, no stdin, no console window and tracing.
- **Untrusted paths are hardened.** Any git call whose working directory is under the code root's `external/`
  or `other/` gets the hardened flags automatically. Callers cannot opt out.
- **One code-root resolver.** `CODE_ROOT`, then the code-root marker, then `~/code`. Nothing else derives roots
  from the assembly location.
- **Shared parsers, once each.** Repo slugs, branch refs, SVG paths and wikilinks each have exactly one parser.

## Layering

References point down this stack and never up or sideways. They are project references, and
`tests/Rig.Architecture.Tests` enforces the namespace rules inside a project: NetArchTest for what a namespace may
reference, ArchUnitNET for cycles:

1. **Composition roots**: `src/Rig.Cli/Program.cs` for `rig`, and `Rig.Web.RigWebHost` for the web host (each route
   group is a static `Map` it calls). These are the
   only projects that mount commands or routes.
2. **Features**: the command and route groups in `Rig.Cli` and `Rig.Web`, and the feature namespaces in `Rig.Core`
   (`Estate`, `Desktop`, `Chrome`, `ReviewQueue`, ...). Features are independent of each other. When two features
   need the same thing, it moves down a layer.
3. **Adapters**: UIA, Win32, AppleScript and the process runner. They translate the outside world into typed values
   and contain no planning or policy, so an adapter never references a feature.
4. **Pure domain**: geometry, plans and diffs, matching and scoring. No I/O.
5. **Leaves**: roots, JSON and YAML boundaries, tracing, slugs. They reference nothing else in rig.

`Rig.Web` and `Rig.Cli` reference `Rig.Core`, never the reverse, and neither references the other's features.
The graph is acyclic at every depth, namespaces included. A cycle almost always means a shared concept is living
inside one feature; the fix is to move it down.

## Data

- Records for every shape, decoded once at the boundary with source-generated `System.Text.Json` (or YamlDotNet for
  YAML) into named types. No `JsonNode` or `Dictionary<string, object>` bags pass through feature code.
- Policy and catalogues are JSON or YAML in `config/`, validated on load.
- Local state is plain files where possible (NDJSON logs, JSON caches). SQLite only for multi-process writes.

## Command surface

- One root: `rig <area> <verb>`. Every area mounts under it: `estate`, `workspace`, `governance`, `desktop`,
  `vault`, `assets`, `devenv`, `chrome-tabs`.
- One spelling per action, so there are no aliases. A verb means the same thing in every area that has it.
- `help`, `status` and `agent` stay as top-level shortcuts because agents and AGENTS.md depend on them.
- A command exists only if something uses it: a person, a scheduled job, CI or an agent instruction. Commands
  that nothing invokes are deleted, not kept "in case".
- `tools/command-ownership` checks that each registration has a reviewed row in `config/command-ownership.json`
  and that a retired surface stays retired.

## Lean on standard tools for commodity work

Write code only for what is genuinely specific to this estate. Delegate the rest:

| Concern | Delegate to |
|---|---|
| Installing CLI tools and fonts | Brewfile / `winget configure` |
| Pinning GitHub Actions | Dependabot or Renovate digest pinning; `zizmor` as the audit gate |
| PR merge readiness | GitHub's own merge state (`gh pr checks --required`, `mergeStateStatus`) |
| Community health files | the org's `.github` repo |
| Markdown formatting | `mdformat` directly |
| App-data directories | the platform's special-folder APIs |

A new dependency must replace more code than its setup and maintenance cost.

Bespoke code is justified for:

- git identity routing
- non-destructive merges into user-owned config
- Win32/UIA workspace control
- the brand-asset build → score → promote gate
- the check-name guard for required checks
- OTLP-JSON tracing

## Safety gates are absolute

Where rig guards an irreversible or shared outcome, no path bypasses the gate:

- **Brand assets.** Only `promote` changes a master. Production cuts are rendered from the vector master,
  driven by `production-cuts.csv`, with no raster fallback.
- **Required checks.** They pass the check-name guard on every enforcement path.
- **Dry runs.** A `--dry-run` writes nothing, prompts for nothing and runs no key generation or installs.
- **Agent permissions.** These are projected only from `config/cli-allowlists.json`; no second writer.
- **Unattended modes.** They never prompt.

## Tests pin behaviour through the seams

- **Drive the public surface.** Tests run a command (or the plan function behind it) with fakes for its ports,
  then assert on the emitted plan or JSON, the files written, and the fake's final state.
- **No patching.** Reaching into a type to swap its behaviour is a sign that a port is missing; add the port instead.
- **Adapter tests stay separate.** Each Win32/UIA adapter keeps its own tests, against fakes on Linux CI.
- **Pure logic gets table and property tests.** Planners, parsers and scorers are covered by small table-driven or
  CsCheck tests. They are cheap and need no fakes.
- **Every test can fail.** A test that cannot fail, or that greps source text, is deleted.
- **Gates are tested by their refusals.** Each safety gate has a test that the unsafe path is refused.

Size follows from this. The suite should be smaller than the code it covers and organised by behaviour
(`WorkspaceRestoreTests.cs`, `GovernancePlanTests.cs`), not by type.

### Behaviour suites share one vocabulary

- **Shared fakes.** `tests/Rig.Core.Tests/Fakes/` holds one fake per port. Each fake implements its port, so the
  compiler breaks it when the port changes.
- **Shared steps.** `tests/Rig.Specs` holds the Reqnroll step definitions over those fakes. A new scenario reuses
  steps; it doesn't re-implement a fake behaviour inline.
- **Gherkin is reserved** for the few scenarios worth reading as a spec.
- **Contract tests.** Where a fake and its real adapter must agree, both run the same test class: real temp git
  repos on every run, Win32 on the Windows runner. This stops fakes drifting from reality.

## Enforcement

The rules above are checked deterministically in CI. Each check fails on any new violation; there is no
baseline file. Where existing code violates a rule, the cleanup lands first.

| Rule | Check |
|---|---|
| Each primitive has one owning type: process starts → the process runner; env paths and roots → the code-root resolver; one parser per format | Roslyn `BannedApiAnalyzers` (`RS0030`, `BannedSymbols.txt`) with the owner exempted; no inline suppression |
| No reference cycles between projects or namespaces | Project references (MSBuild rejects project cycles) and `Rig.Architecture.Tests` `CycleTests` (ArchUnitNET) |
| References follow the layer order; features are independent; adapters never reference features | `Rig.Architecture.Tests` (NetArchTest) |
| Callers use a type's public API, not its internals | `internal` plus `InternalsVisibleTo` only for the test project; analyzers as errors |
| Nullable and analyzer warnings | `TreatWarningsAsErrors` and `dotnet format --verify-no-changes` |
| Code is covered | The 95% line and branch floor ([testing.md](testing.md#the-c-coverage-floor)) |
| Each command registration is reviewed and retired surfaces stay retired | `tools/command-ownership` over `config/command-ownership.json` |
| Fakes live only in `tests/Rig.Core.Tests/Fakes/`; each implements its port | Review, and the compiler for the port |
| Workflows are pinned and least-privilege | zizmor in CI; Dependabot `github-actions` |

New gates land in three steps: add the check with today's violations listed as explicit exceptions, migrate the
code, then delete the exceptions in a separate PR.
