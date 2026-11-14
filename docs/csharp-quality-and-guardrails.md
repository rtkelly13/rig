# C# Quality & Performance Guardrails

This document defines the C# static analysis, banned API enforcement, and performance guardrails configured across `rig`.

## Principles

1. **Functional Constructs Over Mutable Batch Operations**:

   - Prefer modern C# 12 collection expressions (`[.. items, .. more]`), LINQ (`SelectMany`, `Where`), and immutable arrays/lists over imperative `List.Add` / `List.AddRange` pipelines.
   - For materialized collections, evaluate `ToArray()` or collection expressions directly rather than accumulating into mutable scratch lists.

1. **Segregation of Pure Data and Behavior**:

   - Data transfer records, snapshots, and observation models should be pure immutable data structures without dynamic business logic or heavy LINQ computations in getters.
   - Computations belong in pure static domain functions (e.g. `LandingModel.MissingChecks(snapshot, rules)`).

1. **Static Lambdas & Zero-Allocation Closures**:

   - Anonymous functions and local functions that do not capture outer scope should be marked `static` (`static x => ...`, `static int Helper(...)`).
   - This ensures at compile-time that no closure context or display class is allocated on the heap.

1. **Narrow, Explicit Exception Handling**:

   - Catch blocks must never catch `Exception` without a specific exception filter or immediate rethrow.
   - Catch only exact expected exception types (e.g., `when (e is IOException or UnauthorizedAccessException)`).

______________________________________________________________________

## Tooling & Enforcement

### 1. Banned API Analyzers (`Microsoft.CodeAnalysis.BannedApiAnalyzers`)

Rig integrates `Microsoft.CodeAnalysis.BannedApiAnalyzers` centrally in `Directory.Build.props` reading `BannedSymbols.txt`.

- **Rules**:
  - Legacy non-generic collections (`ArrayList`, `Hashtable`) are banned in favor of generic and frozen collections.
  - Mutable batch operations (`List<T>.AddRange`) are flagged in favor of functional collection expressions (`[.. a, .. b]`) and LINQ transforms (`SelectMany`).
- **Severity**: Configured via `.editorconfig` (`dotnet_diagnostic.RS0030.severity`). Subsystems can elevate warnings to errors.

### 2. Performance Analysis Mode (`AnalysisModePerformance = AllEnabled`)

Rig enables all performance rules from the .NET SDK (`CA18xx`):

- `CA1822`: Mark members as static when they do not access instance state.
- `CA1829`: Use `Length`/`Count` property instead of `Enumerable.Count()`.
- `CA1834`: Use `StringBuilder.Append(char)` instead of string overload.
- `CA1859`: Use concrete types when possible for devirtualization.
- `CA1860`: Avoid using `Enumerable.Any()` when checking for empty collections where `Length` or `Count` is available.

### 3. Roslyn Code Style Rules (`.editorconfig`)

- `csharp_prefer_static_anonymous_function = true:warning`
- `csharp_prefer_static_local_function = true:warning`
- `dotnet_diagnostic.IDE0028.severity = suggestion` (Collection initialization can be simplified)
- `dotnet_diagnostic.IDE0300.severity = suggestion` (Collection expression can be used)
- `dotnet_diagnostic.IDE0305.severity = suggestion` (Collection expression for fluent calls)

### 4. Code Formatting & CI Enforcement

- **EditorConfig**: `.editorconfig` enforces file-scoped namespaces (`IDE0161`), usings outside namespace with sorted system directives, strict brace placement (`IDE0011`), unused directive elimination (`IDE0005`), dead code elimination (`IDE0051`, `IDE0052`, `IDE0059`), and standardized naming conventions across all C# projects.
- **Local Enforcement**: Run `dotnet format rig.slnx` to automatically format whitespace, code style, and imports.
- **CI Enforcement**: The `dotnet` job in `.github/workflows/ci.yml` runs `dotnet format rig.slnx --no-restore --verify-no-changes` ensuring unformatted code cannot merge. In addition, `Directory.Build.props` enables `<EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>` and `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`, treating code style warnings as build errors.
