# ADR 0022: C# on .NET 10 Is rig's Primary Language

- **Status:** Accepted
- **Applies to:** all of rig except the brand-asset pipeline (`image_drift`)
- **Supersedes:** [0002](0002-uv-for-python-packaging.md), [0003](0003-pyright-strict-type-checking.md) and
  [0004](0004-ruff-for-linting-and-formatting.md) for everything except `image_drift`. Amends
  [0005](0005-per-language-testing-stacks.md): rig's own testing stack becomes the .NET one.

## Context

rig is about 86k lines of Python and 57k lines of tests. Three things have driven the cost of keeping it in Python:

1. **The Windows desktop layer.** `desktop_manager/win32/` reaches Win32, COM and UI Automation through
   ctypes, comtypes, pywin32 and pyvda. Each is a thin, loosely typed binding that the strict type gates
   cannot see through, and pyvda pins rig to whatever Windows builds its maintainer has caught up with.
   .NET reaches the same APIs natively, with generated, typed P/Invoke (CsWin32) and a first-party UI
   Automation client.
2. **The type gates.** basedpyright strict, `ty`, the type-soup ratchet (`packages/type-ratchet`) and
   import-linter exist to make Python behave like a statically typed, layered language. C# with nullable
   warnings as errors and project references gives that by default.
3. **The estate.** Most of the libraries rig governs (Lake.NET, oxbow, Diffra, Parquet.*) are .NET 10 repos
   using Central Package Management. rig already carries .NET-specific governance for them.

`image_drift` is the exception. It depends on vtracer, scour, shapely and numpy, and
[ADR 0009](0009-deterministic-svg-render-pipeline.md) guarantees byte-identical output from them. No .NET
library reproduces those bytes, so porting it would change every production cut.

## Decision

1. **New rig code is C# on .NET 10.** The SDK is pinned in `global.json`. Projects build with nullable
   enabled, warnings as errors and `AnalysisLevel` `latest-all`.
2. **Packages use Central Package Management** (`Directory.Packages.props`), as every sibling .NET repo does,
   so Renovate's nuget manager keeps them current. Paket was considered and rejected: it is unused elsewhere
   in the estate and Renovate does not manage it natively.
3. **The CLI is System.CommandLine with Spectre.Console rendering.** rig's command tree is three levels deep
   (`rig gh threads list`); System.CommandLine supports nested subcommands natively, and oxbow already uses
   the same pairing. CommandLineParser was considered and rejected: it supports one level of verbs and has
   not shipped a release in years.
4. **Tests use xUnit v3, Verify (snapshots), CsCheck (property tests), Reqnroll (Gherkin features), coverlet
   and Stryker.NET.** These map one-for-one onto pytest, inline-snapshot, Hypothesis, pytest-bdd, coverage
   and the mutation workflow. Layer contracts move from import-linter to NetArchTest and project references.
5. **The quality bar does not drop.** The C# coverage floor ratchets up to 95%, matching the Python floor,
   and every gate runs in CI.
6. **Migration is a strangler, not a rewrite.** A C# `rig` front door owns the command line and forwards any
   command it does not own yet to the Python entry point. A command moves in one PR, and its Python is
   deleted in the same or the next PR. No command stops working between PRs.
7. **`image_drift` stays Python,** moved into its own uv project and called from C# through the process
   runner. ruff and pytest remain for it alone.
8. **The target architecture is unchanged in shape.** Desired → observed → plan → apply, one port per kind of
   external state and behaviour tests through seams all carry over. Ports become C# interfaces; fakes become
   C# classes.

The step-by-step plan, its done-when checks and progress live on the tracking issue.

## Consequences

- Machines need the .NET 10 SDK. `uv` stays installed for `image_drift` and for other estate repos.
- `uv run <command>` keeps working throughout the migration through the forwarding front door. Callers in
  other repos (`AGENTS.md` files, `~/.claude/settings.json`, `config/cli-allowlists.json`) are updated once
  the Python entry points are gone.
- `packages/type-ratchet`, basedpyright, `ty` and import-linter are deleted once no rig Python remains
  outside `image_drift`.
- ADRs 0002–0004 stay as the record of how rig's Python was built and still govern `image_drift`.
- Scripts under `scripts/` may be C# file-based apps (`dotnet run script.cs`), in line with the script
  language policy.
