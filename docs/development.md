# Development

The full command set, test conventions and quality gates for this repo. `AGENTS.md` keeps only
the commands an agent runs every session.

## Commands

`AGENTS.md` lists the everyday ones. Beyond those:

```bash
dotnet run --project src/Rig.Cli -- status --tests      # git state, then the solution's tests
pnpm install && pnpm css:build                          # rebuild every committed web asset
pnpm css:check                                          # fails if any committed output is stale
```

Every stylesheet is Tailwind 4 (CSS-first, so no `tailwind.config.js`) built on
`@rtkelly-labs/design-system`. Each entry imports the package's `theme.css`, and the outputs are
committed because the server must work without Node:

| Entry | Output |
|---|---|
| `web/app.src.css` | `src/Rig.Web/templates/app_assets/app.css` (with the SPA bundle) |
| `tools/image-drift/styles/gallery.src.css` | `tools/image-drift/src/image_drift/templates/gallery.css`, inlined into preview pages |
| `styles/c4.src.css` | `src/Rig.Web/C4/static/c4.css` |

Rebuild after touching a template, an entry or a design-system bump. Colours, type sizes and
spacing come from design-system tokens only: `DesignTokensTests` in `tests/Rig.Web.Tests` fails on raw hex/`rgb()`
or arbitrary-value classes (`text-[0.82rem]`) in the sources and templates. A missing token is
added upstream in rtkelly-labs/design-system, not hard-coded here.

## The C# front door

`rig` is C# (ADR 0022, #85). `src/Rig.Cli` is the front door. There are no Python entry points: `rig gh …`, `rig latex build` and `rig help` (the overview) are the installed
shim, and `dotnet run --project src/Rig.Cli -- …` is the same command from a checkout.
The `desktop-manager` script is gone; its commands are `rig desktop`, `rig jobs`, `rig runtime` and
`rig chrome-tabs`.

The docs write commands two ways. `rig <command>` is the installed front door on PATH (the runtime
copy's shim) and is the form for operating the estate. `dotnet run --project src/Rig.Cli -- <command>`
runs this checkout's code, so it is the form for developing rig and for CI. The per-tool `uv run status` and `uv run workspace` scripts are gone.

C# parses every command itself; nothing is forwarded to Python. The Python tools C# still starts (mdformat, through pinned `uvx`) find the checkout from `RIG_HOME` when it is set, else
`<code root>/personal/rig`.

The [command ownership gate](csharp-port/ownership.md) inspects the C# command tree, executables and launchers,
then checks `config/command-ownership.json`. Add a ledger row for each new surface, and tombstone each deleted
file in `RetiredFiles`, in the same PR. CI rejects an unregistered executable or a retired surface coming back.
This gate runs on the PR's merged result and participates in the required `ci` check.

`rig web mcp` is a stdio MCP server served by the C# CLI. `dotnet run` restores and builds without writing to stdout,
even from a clean `src/Rig.Cli` with no `obj` or `bin`, so the protocol stays clean; a failed build never starts the
server.

```bash
dotnet run --project src/Rig.Cli -- --help              # from a checkout
dotnet pack src/Rig.Cli -c Release                      # writes artifacts/packages/Rig.Cli.<version>.nupkg
dotnet tool update --global --add-source artifacts/packages Rig.Cli   # installs or updates ~/.dotnet/tools/rig
```

The installed tool is opt-in: `rig` on PATH (`~/.local/bin/rig`) runs the runtime copy's `rig` script, which reaches
the same C# CLI through `dotnet run`, built in that copy on first use. Call the tool as `~/.dotnet/tools/rig` to skip
that build.

## BDD specs (`tests/Rig.Specs`)

`tests/Rig.Specs` holds Gherkin specifications for the estate's highest-risk worktree operations and the
reconciliation pipeline. Reqnroll (on xUnit v3 and Microsoft.Testing.Platform) generates one test per scenario.

```bash
dotnet test tests/Rig.Specs/Rig.Specs.csproj                    # fast: in memory, @regression skipped
RIG_REGRESSION=1 dotnet test tests/Rig.Specs/Rig.Specs.csproj   # include the on-disk scenarios
RIG_SPEC_MODE=real dotnet test tests/Rig.Specs/Rig.Specs.csproj # run every scenario on disk
CI=true dotnet test tests/Rig.Specs/Rig.Specs.csproj            # what CI does
```

Each scenario has two executors. The default one drives the pure domain (`WorktreeRemovalJudge`, the shelving
carry rules, the reconciliation plan and runner) with no disk and no git; the on-disk one builds a bare origin,
real worktrees and real files. A scenario tagged `@real` always uses the on-disk executor, and
`RIG_SPEC_MODE=real` switches every scenario. `@regression` marks the slow scenarios; they are skipped outside CI
unless `RIG_REGRESSION=1` is set. Reqnroll writes `*.feature.cs` beside each feature: they are generated,
gitignored, and rebuilt on every build.

## Tests and CI

All tests are C# (xUnit v3 on Microsoft.Testing.Platform); [testing.md](testing.md) covers the styles and the
coverage floor. The brand pipeline `tools/image-drift` is the one Python project and runs its own gates (below).

- **Linux**: the `dotnet` job builds and tests `rig.slnx`; `dotnet-coverage` checks the 95% floor.
- **Windows**: `dotnet-windows` and `image-drift-windows` run conditionally, to conserve runner minutes on private repos:
  - nightly on `main` (03:43 UTC), so a break that lands on `main` surfaces the next morning;
  - on pull requests whose branch or title contains `windows` or `win32`, or with the label `ci:windows` (adding the
    label to an open PR starts a run);
  - on manual `workflow_dispatch`.
- Pure Windows logic, path semantics and wrappers run on Linux against fakes. Only a test that needs live Win32 or a
  real desktop is left to the Windows runner: skip it elsewhere with `Assert.SkipUnless(OperatingSystem.IsWindows(), ...)`.
- The browser tests are Playwright tests in `tests/Rig.Web.BrowserTests`, driving an in-process `RigWebHost`
  ([web-routes.md](web-routes.md#testing)). Install Chromium once after a build:
  `pwsh tests/Rig.Web.BrowserTests/bin/Debug/net10.0/playwright.ps1 install chromium`.

Windows-safe tests (the Windows jobs catch these, but write them right first time):

- **Links**: never create a symlink directly: without Developer Mode it fails with WinError 1314. Product code goes
  through `Rig.Core.Skills.IDirectoryLinker` for the same reason.
- **Paths in output**: render paths with forward slashes wherever a report, snapshot or assertion compares text.
- **Line endings**: LF everywhere. When a test hashes a checked-in file, give that file type `-text` in
  `.gitattributes`, as `*.svg` has.
- **Machine state**: never depend on the real monitor count, open windows or running processes. The C# fakes
  (`FakeDesktop`, `FakeProc`) answer for them.
- **Filenames**: Windows cannot create names containing `:`, `?` or `*`; skip those tests on Windows.
- **Console width**: compare wrapped CLI output with whitespace normalised. The Windows console wraps at 80 columns.

## Standards and quality gates

- **Package managers**: Paket for C# (`dotnet tool restore` once, then `dotnet restore`; versions live in `paket.dependencies`, ADR 0025), `pnpm` for the Tailwind build, `uv` for `tools/image-drift` only.
- **Local analytics**: DuckDB for structured ledger querying and cross-referencing large JSON feeds, to avoid
  repeating API calls.
- **Workflow**: every change goes through a pull request, never a direct push to `main`. All required CI checks pass
  before merge. Merges are squash only (enforced in repo settings).
- **C# gates**: `dotnet build` with analyzers and nullable on, `dotnet format --verify-no-changes`, the layer rules in
  `tests/Rig.Architecture.Tests`, and the coverage floor ([testing.md](testing.md#the-c-coverage-floor)).
- **Ownership gate**: `tools/command-ownership` checks `config/command-ownership.json` against the C# command tree
  (see [The C# front door](#the-c-front-door)).
- **LOC report** (report-only, never a gate):
  `dotnet run --project tools/loc-report -- . [--markdown]` prints C# code lines per `src/` area and the Python
  carry-over counts; CI appends it to the job summary. Markers live in `tools/loc-report/Markers.cs`.
- **Repo gates**: `ls-lint` for filenames and script languages, `zizmor` for workflows, and
  `rig repo-governance agents-audit . --strict` for AGENTS.md pointers.
- **image-drift**: `cd tools/image-drift && uv sync && uv run ruff check . && uv run ty check && uv run pytest`;
  CI runs the same, plus basedpyright and bandit, in the `image-drift` jobs.
