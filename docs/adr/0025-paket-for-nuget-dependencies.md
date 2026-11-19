# ADR 0025: NuGet Dependencies Are Managed by Paket

- **Status:** Proposed (pilot; the owner decides whether it stays)
- **Applies to:** every C# project in `rig.slnx`, `experiments/image-stack-eval`, and Renovate; rig issue #360
- **Supersedes:** decision 2 of [ADR 0022](0022-csharp-is-rigs-primary-language.md) (Central Package Management).
  Amends the transitive-pin wording in item 4 of [ADR 0023](0023-declarative-aot-dotnet-cli-tooling.md).

## Context

[ADR 0022](0022-csharp-is-rigs-primary-language.md) chose Central Package Management and rejected Paket because it
was unused elsewhere in the estate and "Renovate does not manage it natively". Renovate now has a `paket` manager
(it reads `paket.dependencies`, resolves versions from the NuGet datasource, and `lockFileMaintenance` runs Paket to
rewrite `paket.lock`), and what CPM cannot give is a lock file that pins the whole transitive graph in one reviewed
file. Issue #360 asks whether Paket 12 can replace CPM here cleanly. A spike answered that before any conversion.

## Spike results

Run on macOS arm64 with SDK 10.0.401 and Paket 12.0.0 (a local dotnet tool), against `rig.slnx` at the commit
this ADR branches from. Every package version in `Directory.Packages.props` carried over unchanged.

| Question | Answer |
| :--- | :--- |
| net10.0 recognised, restore through `rig.slnx` | Yes. `paket install` writes `paket.lock` and `dotnet restore rig.slnx` restores every project. |
| net11.0 recognised | `paket.dependencies` accepts `framework: net10.0, net11.0` and `paket.lock` records `RESTRICTION: \|\| (== net10.0) (== net11.0)`, so Paket 12 parses it. Re-checked with SDK 11.0.100 in a scratch copy retargeted to `net11.0`: `paket install` left `paket.lock` unchanged and `dotnet restore` restored all projects. That copy's build then failed only on SDK 11 analyzer findings in existing code (CA1307), which the retarget PR owns, not on Paket. |
| Resolver and transitive stability | One resolution for the whole repo (`strategy: min`, `lowest_matching: true`), where NuGet resolves per project. Transitives therefore move; see the diff below. `Microsoft.Testing.Platform`, `Microsoft.Testing.Extensions.TrxReport` 2.5.2 and `System.CommandLine` under DotMake did not change in any project. |
| Storage | `storage: none` keeps packages in `~/.nuget/packages`; Paket writes no `packages/` folder. |
| BannedApiAnalyzers and CsWin32 asset flags | `paket.references` has no `private_assets`. A target in `Directory.Build.targets` runs after Paket's restore target and sets `PrivateAssets=all` on the analyzer, CsWin32 and its three metadata and docs packages, and clears it on everything else. RS0030 was proven by planting a banned call in `src/Rig.Cli/Gh/Pr/`: the build failed with the expected error. |
| Sharing references | They cannot be shared. A project's `paket.references` replaces any parent file, so each of the 18 projects lists its own packages, including the analyzer. An architecture test fails when one omits the analyzer. |
| Where `Paket.Restore.targets` is imported | `paket install` appends `<Import Project=".paket/Paket.Restore.targets" />` to each `.csproj`. The file-based scripts under `scripts/` have no project and no Paket packages, so they run unchanged. |

### Transitive diff

`dotnet list package --include-transitive` before (CPM) and after (Paket), per project, 18 projects. The same pattern
recurs in all of them:

- `Microsoft.Bcl.AsyncInterfaces` 6.0.0 to 9.0.6 in every project that has it.
- `Microsoft.Extensions.{Configuration,DependencyInjection,FileProviders}.Abstractions` and `Primitives` 10.0.12 to
  10.0.14 in the test projects.
- Added: `System.Text.Json` 8.0.5, `System.Diagnostics.DiagnosticSource` 5.0.0, `System.Security.Principal.Windows`
  5.0.0, `System.Memory` 4.5.3 (Rig.Core), `System.Threading.Channels` 9.0.6 (Rig.Specs).
- Removed from Rig.Cli, Rig.Specs and the test projects: `Microsoft.Extensions.*` packages that Rig.Web's
  `Microsoft.AspNetCore.App` framework reference now prunes.

None of these is a package rig references directly.

### What is not clean

- **One resolution for the repo.** The moves above follow from it; they are a reviewed diff in `paket.lock` rather
  than silent drift, but they are moves.
- **NU1510.** Paket emits a `PackageReference` for every lock entry, including ones the net10.0 shared framework
  already provides, so NuGet warns that it will not prune them. `Directory.Build.props` sets
  `NoWarn` `NU1510` for that; the warning would otherwise fail the build under `TreatWarningsAsErrors`.
- **Private assets need a custom target** (above), and without it every package of the `PackAsTool` project
  (Rig.Cli) is private, which hides Spectre.Console, DotMake and System.CommandLine from the test projects.
- **Paket edits every `.csproj`** (an XML declaration, an import, no trailing newline).
- **Nested checkouts.** A worktree below another checkout that still has a `Directory.Packages.props` inherits it and
  fails with NU1506. `ImportDirectoryPackagesProps=false` works around it; it does not arise once the file is gone
  from every ancestor.

## Decision

1. **NuGet packages are declared in `paket.dependencies` and locked in `paket.lock`.** Every package line is an exact
   constraint (`nuget X == 1.2.3`), which is what Renovate's paket versioning updates. `storage: none`,
   `strategy: min`, `lowest_matching: true`, and `framework: net10.0, net11.0`.
2. **Each project lists what it uses in its own `paket.references`.** `Directory.Packages.props` and
   `ManagePackageVersionsCentrally` are removed; there is no dual setup.
3. **Paket is a pinned local tool** (`.config/dotnet-tools.json`). CI runs `dotnet tool restore` before any
   restore or build, and the NuGet and Playwright cache keys hash `paket.lock`.
4. **Pinning the CLI stack** (ADR 0023 item 4) is now an exact line for `DotMake.CommandLine` and
   `System.CommandLine` in `paket.dependencies`, and `paket.lock` pins their transitives.
5. **Renovate** enables the `paket` manager and `lockFileMaintenance` for it.
6. **Estate connectivity** reads `paket.dependencies` and `paket.references` through the `IPaketManifests` port (the
   CPM reader stays, for the sibling repos that use it). The real reader is the `Rig.Paket` sidecar, a separate
   console project on Paket.Core's own parsers (`DependenciesFile`, `LockFile`, `ReferencesFile`). Paket.Core,
   FSharp.Core and Newtonsoft.Json load only there, so they never enter `Rig.Core` and Native AOT stays open for rig.
   rig starts it once per repo, writes the paths to its standard input and reads JSON from its standard output. A
   `paket.dependencies` takes resolved versions from the `paket.lock` beside it, else the constraint. The sidecar lives
   in a `paket/` folder under rig's binaries (`RuntimeManager` publishes it there, and `check` starts it), and a missing
   sidecar skips the repo's Paket files with one inventory warning.

## Pilot exit criterion

The first Renovate PR after this lands must change both `paket.dependencies` and `paket.lock` and pass CI. If it
changes only one, or cannot run Paket, revert to CPM (git is the rollback) and amend this ADR.

## Consequences

- Machines run `dotnet tool restore` once per checkout (the first `dotnet restore` after it installs nothing else).
- A new project needs a `paket.references` with the analyzer; the architecture test names any that omit it.
- A package bump is one line in `paket.dependencies` plus the regenerated `paket.lock`; transitive changes show in
  the lock diff.
- `.paket/Paket.Restore.targets` is vendored by `paket install` and is regenerated, not edited, when Paket updates.
- The published runtime carries a second executable (`paket/Rig.Paket`) and connectivity starts one extra process per
  repo that has Paket files. Paket.Core's old transitive floors carry advisories, so `paket.dependencies` raises
  `System.Net.Http`, `System.Security.Cryptography.Pkcs` and `System.Security.Cryptography.X509Certificates` for the
  sidecar.
- The retarget to .NET 11 should restore under Paket on CI as it did in the scratch check.
