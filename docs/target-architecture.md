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

## One shape for every feature: desired → observed → plan → apply

Each feature has four parts:

1. **Desired**: declared in `config/` as data. Python holds no policy, so no per-repo maps, hard-coded flags or
   personal paths.
2. **Observed**: read through a port (below). It is decoded once, at the boundary, into named types.
3. **Plan**: a pure function from (desired, observed) to a list of typed `Change` rows. It has no I/O, so it
   is trivially testable.
4. **Apply**: walks the plan through the same port. It is idempotent, so a second run produces an empty plan.

Every command therefore works the same way:

- **Flags:** `--dry-run` prints the plan, `--json` emits it, and `--apply` executes it.
- **Exit codes:** 0 when clean, 1 on drift, 2 on error.
- **Status is data.** Callers branch on the typed plan or diff, never on rendered text.

The rendering layer, whether a rich table or a text diff, sits on top and is never parsed back.

## Ports: one per kind of external state

The outside world is reached through a few narrow, typed interfaces. Product code receives them as arguments
and never builds them inline or imports them lazily inside a function.

| Port | Real adapter | Test fake |
|---|---|---|
| `Proc`: `git`, `gh`, `gh_json[T]` | `rig/proc.py` over `tracing.run` | `FakeGitHub` (responses keyed by route, write log); real temp git repos for git |
| `DesktopPort`: desktops, windows, launch, place, clock | Win32 adapters in `desktop_manager/win32/` | `FakeDesktop`, a stateful in-memory model |
| `ChromeDriver`, `TerminalDriver` | UIA adapters | stateful fakes |
| Filesystem and roots | `rig/roots.py`, `platformdirs`, `rig.fs_links` | `tmp_path`, `HOME=tmp` |

The rules that follow from this:

- **One process runner.** There is no other `subprocess.run` for git or gh. It always applies timeouts, UTF-8
  decoding, no stdin, no console window and tracing.
- **Untrusted paths are hardened.** Any git call whose working directory is under the code root's `external/`
  or `other/` gets the hardened flags automatically. Callers cannot opt out.
- **One code-root resolver.** `CODE_ROOT`, then the code-root marker, then `~/code`. Nothing else derives roots
  from `__file__`.
- **Shared parsers, once each.** Repo slugs, branch refs, SVG paths and wikilinks each have exactly one parser.
- **Windows primitives live in `desktop_manager/win32/`**: window enumeration, input, foreground, UIA client
  and scheduled tasks. Feature modules build on them and never re-declare ctypes structures.

## Layering

Imports point down this stack and never up or sideways:

1. **Composition roots**: `rig_cli.py`, `desktop_manager/cli.py`. These are the only modules that mount other
   packages' commands. `desktop_manager/web_cli.py` is the web equivalent: features expose a `rig.web_routes.Route`
   and it hands them to `StudioServer(routes=...)`, so the server imports no feature to serve its JSON.
2. **Features**: `rig.estate`, `rig.workspace`, `rig.repo_governance`, `rig.image_drift`, `rig.obsidian`,
   `rig.devenv`, and the `desktop_manager` features (restore, align, chrome, work, claude_sessions, jobs).
   Features are independent of each other. When two features need the same thing, it moves down a layer.
3. **Adapters**: `desktop_manager/win32/`, UIA, `rig/proc.py`. They translate the outside world into typed
   values and contain no planning or policy, so an adapter never imports a feature.
4. **Pure domain**: geometry, plans and diffs, matching and scoring. No I/O.
5. **Leaves**: `roots`, `typed_json`, `typed_yaml`, `tracing`, slugs. They import nothing from rig.

`rig` sits below `desktop_manager` as a whole. `desktop_manager` may import `rig`, never the reverse.

The package graph is acyclic at every depth. That includes the top level (`rig` ↔ `desktop_manager`) and
sibling packages (`rig.estate` ↔ `rig.workspace`), not just individual modules. A cycle almost always means a
shared concept is living inside one feature; the fix is to move it down, not to add a lazy import.

## Data

- Named, frozen dataclasses for every shape, decoded by pydantic at the boundary (see
  [python-types.md](python-types.md)). Serialisation goes through pydantic too; no hand-written `to_dict`.
- Policy and catalogues are JSON or YAML in `config/`, validated on load.
- Local state is plain files where possible (NDJSON logs, JSON caches). SQLite only for multi-process writes;
  DuckDB for analytics over traces and feeds.

## Command surface

- One root: `rig <area> <verb>`. Every area mounts under it: `estate`, `workspace`, `governance`, `desktop`,
  `vault`, `assets`, `devenv`, `browser`.
- One spelling per action, so there are no aliases. A verb means the same thing in every area that has it.
- `help`, `status` and `agent` stay as top-level shortcuts because agents and AGENTS.md depend on them.
- A command exists only if something uses it: a person, a scheduled job, CI or an agent instruction. Commands
  that nothing invokes are deleted, not kept "in case".

## Lean on standard tools for commodity work

Write code only for what is genuinely specific to this estate. Delegate the rest:

| Concern | Delegate to |
|---|---|
| Installing CLI tools and fonts | Brewfile / `winget configure` |
| Pinning GitHub Actions | Dependabot or Renovate digest pinning; `zizmor` as the audit gate |
| PR merge readiness | GitHub's own merge state (`gh pr checks --required`, `mergeStateStatus`) |
| Community health files | the org's `.github` repo |
| Markdown formatting | `mdformat` directly |
| App-data directories | `platformdirs` |

A new dependency must ship types (py.typed or stubs in `typings/`), and must replace more code than its stub
and setup cost.

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
- **No patching.** Module-attribute monkeypatching is a sign that a port is missing; add the port instead.
- **Adapter tests stay separate.** Each Win32/UIA adapter keeps its own tests, using the `as_windows` doubles
  on Linux CI.
- **Pure logic gets table tests.** Planners, parsers and scorers are covered by small table-driven tests. They
  are cheap and need no fakes.
- **Every test can fail.** A test that cannot fail, or that greps source text, is deleted.
- **Gates are tested by their refusals.** Each safety gate has a test that the unsafe path is refused.

Size follows from this. The suite should be smaller than the code it covers and organised by behaviour
(`test_restore_behaviour.py`, `test_governance_plan.py`), not by module.

### Behaviour suites share one vocabulary

- **Shared fakes.** `tests/fakes/` holds exactly one fake per port. Each fake is typed as its port, so the
  type checker breaks it when the port changes.
- **Shared steps.** `tests/behaviour/steps/` holds typed given/when/then functions over those fakes, e.g.
  `given_windows(...)`, `when_restore(...)`, `then_window_on(...)`. Scenarios are plain pytest tests built
  from steps. A new scenario reuses steps; it doesn't re-implement a fake behaviour inline.
- **Optional Gherkin.** `.feature` files are reserved for the few scenarios worth reading as a spec. They bind
  to the same step library.
- **Contract tests.** Each fake and its real adapter run the same contract suite: real temp git repos on every
  run, Win32 on the Windows runner. This stops fakes drifting from reality once the copies are gone.

## Enforcement

The rules above are checked deterministically in CI. Each check fails on any new violation; there is no
baseline file. Where existing code violates a rule, the cleanup lands first.

| Rule | Check |
|---|---|
| Each primitive has one owning module: `subprocess` → `rig/proc.py`; `ctypes`/`comtypes` → `win32/`; env paths and `__file__` roots → `rig/roots.py`; task XML → `win32/tasks.py`; one parser per format | Ruff `banned-api` (TID251) with per-file-ignores for the owner, or a ratchet boundary rule. The ratchet's existing `noqa` ban means neither can be suppressed inline. |
| No package-level import cycles, at any depth from the top level down to modules | import-linter `acyclic_siblings` (grimp counts relative, lazy and `TYPE_CHECKING` imports). basedpyright `reportImportCycles` already covers modules. |
| Imports follow the layer order; features are independent; adapters never import features | import-linter `layers`, `independence` and `forbidden` contracts in `[tool.importlinter]` |
| Callers use a module's public API, not its `_private` helpers | basedpyright `reportPrivateUsage` / `reportPrivateLocalImportUsage` |
| Declared dependencies match imports | deptry, with a module-name map |
| No copy-paste re-implementations | pylint `duplicate-code`, report-only. Primitive ownership is the real control. |
| Workflows are pinned and least-privilege | zizmor in CI; Dependabot `github-actions` |

New gates land in three steps: add the contract with today's violations listed as explicit ignores, migrate
the code, then delete the ignores in a separate PR.
| Fakes live only in `tests/fakes/`; no `mock.patch`, `monkeypatch.setattr` or `sys.modules` injection outside an allowlist | Ratchet rules over `tests/` (needs per-path rule sets in type-ratchet) |
| Fakes conform to their port | Fakes annotated as the Protocol; basedpyright strict |
| Fakes match reality | Shared contract suites run against fake and real adapter |
