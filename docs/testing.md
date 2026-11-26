# Testing

Which style of test to write, and how to measure whether a test is any good.
[development.md](development.md) has the Windows-safe rules and the CI gates.

## Two styles, chosen by what is being pinned

| Style | Pins | Write one when | Lives in |
|:------|:-----|:---------------|:---------|
| **Property** (CsCheck) | An invariant of an algorithm, for every input | The code is pure and the rule holds across a domain: tiling, round-trips, idempotence, symmetry, agreement with an oracle | `*PropertyTests.cs` beside the example tests |
| **Example** | One concrete case | A specific regression, an edge the generator won't find, or mechanics below the level of a rule | `*Tests.cs` |

Scenarios worth reading as a spec are Gherkin, run through Reqnroll ([BDD specs](development.md#bdd-specs-testsrigspecs)).

## C# test projects

The C# side ([ADR 0022](adr/0022-csharp-is-rigs-primary-language.md)) has seven test projects under `tests/`, all
xUnit v3 on Microsoft.Testing.Platform. `tests/Directory.Build.props` sets `xunit.v3.mtp-v2`, `coverlet.MTP`, a
global `using Xunit` and aggressive parallel collections, so a new project needs only its project reference.

| Project | Pins |
|:--|:--|
| `Rig.Core.Tests` | `Rig.Core` behaviour, with CsCheck properties beside the example tests |
| `Rig.Cli.Tests` | command parsing and output |
| `Rig.Web.Tests` | ASP.NET routes, MCP, the SPA assets and the design-token rules |
| `Rig.Web.BrowserTests` | the SPA in headless Chromium (Playwright), both themes; needs `playwright.ps1 install chromium` |
| `Rig.Architecture.Tests` | the layer rules: NetArchTest for references out of a project, ArchUnitNET for cycles and the role layering |
| `Rig.CommandOwnership.Tests` | the ownership gate's discovery |
| `Rig.Specs` | the Gherkin scenarios, through Reqnroll |

```bash
dotnet test --project tests/Rig.Core.Tests/Rig.Core.Tests.csproj   # one project
dotnet test --solution rig.slnx                                    # every project
```

A property declares a `Gen<T>` for the domain, then states the rule in a `[Fact]` that samples it
(`Tilings.Sample(...)`). `GeometryPropertyTests` and `GitignorePropertyTests` are the models.

Useful shapes, each with an example in `tests/Rig.Core.Tests/Desktop/GeometryPropertyTests.cs`:

- **Partition.** The slots of a layout cover their parent exactly once.
- **Agreement.** The pixel and unit layouts agree whenever the screen divides evenly.
- **Oracle.** The clever implementation matches a brute-force one on a small grid.
- **Algebra.** Symmetry, identity, invariance under translation or reordering, monotonicity.
- **Round-trip and idempotence.** `parse(render(x)) == x`, and `align(align(x)) == align(x)`.

### Shared fakes

Tests share one vocabulary rather than each file building its own doubles
([target-architecture.md](target-architecture.md#behaviour-suites-share-one-vocabulary)).
`tests/Rig.Core.Tests/Fakes/` holds one fake per port (`FakeProc`, `FakeGitHub`, `FakeChromeTabActions`, `FakePaketManifests`, ...), typed
as that port. Extend the shared one rather than writing a double inline.

Line endings are LF everywhere: `.editorconfig` sets `end_of_line = lf` and `.gitattributes` pins `*.cs` to
`eol=lf`, so raw string literals match the `\n` output tests compare against. Every JSON rig writes is LF on
every platform (`JsonOutput` and the runtime state writer), and CRLF is written only where a Windows consumer
needs it, such as a `.cmd` shim or Windows Terminal settings. A checked-in file a test hashes gets `-text` in
`.gitattributes`.

## Measuring test quality

Each measure answers a different question. None of them is a target on its own.
[test-quality.md](test-quality.md) has the evidence behind each measure and how each type of test is scored.

| Measure | Answers | Command |
|:--------|:--------|:--------|
| Line + branch coverage | What code runs at all? | `scripts/coverage-floor.cs` over coverlet reports ([below](#the-c-coverage-floor)) |
| Mutation score | Would the tests notice if this code were wrong? | Not wired up for C#; run a mutation tool locally before deleting a test that adds no unique coverage |

Coverage says code was *executed*. Mutation testing says it was *checked*: a suite can run nearly every line of a
module and assert on little of it.

### The C# coverage floor

`RigCoverageFloor`, `RigCoverageTarget` and `RigCoverageSlack` in `Directory.Build.props` set the rules, and
`scripts/coverage-floor.cs` applies them. The floor starts at the 95% target: there is no backlog to ratchet
through, so each command ported to C# arrives with its tests. CI fails a build below the floor, and fails one
more than the slack above it so that the floor is raised in the same PR as the tests that earn it.

Coverage is line and branch together, merged across every test project's coverlet json report. Those reports
keep each branch's identity (offset, target, path), so two projects that take different sides of one `if`
together count as covering both; cobertura's per-line `1/2` counts could not tell.

```bash
dotnet build rig.slnx -m:2
dotnet test --solution rig.slnx --no-build --coverlet --coverlet-output-format json \
  --coverlet-exclude-by-file "**/Microsoft.Interop.ComInterfaceGenerator/**" \
  --coverlet-exclude-by-file "**/Microsoft.Windows.CsWin32/**"
dotnet run scripts/coverage-floor.cs -- TestResults/coverage.*.json
```

Win32 and UI Automation adapters cannot run on the Linux job, so keep logic out of them. Code that can be tested
belongs in a type the Linux job measures.

Layer rules live in `tests/Rig.Architecture.Tests`, next to the project references that already stop a lower layer
referencing a higher one. `StructureTests` reads every type reference from the compiled IL, folds it into namespaces,
and classifies each edge by the roles in `Graph/Roles.cs`. A new area fails until it is given a role. A new
feature-crossing or upward edge fails with the type pairs behind it, which is what to move down a layer; the agent
making the change does the move, and the test checks it. Each listed exception that stops occurring fails until its
line is deleted.

## CI time budget

CI time is capped so that something slow fails loudly instead of growing quietly: every job sets
`timeout-minutes` in `.github/workflows/ci.yml` (Linux build and test jobs 8 to 12, Windows 15). The cap is for
hangs. A test that creeps towards a minute needs a cheaper fixture, or belongs behind the Windows-only or
`@regression` gates.
