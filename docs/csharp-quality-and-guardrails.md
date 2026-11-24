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

## Output Rendering

`Rig.Cli` renders through `IAnsiConsole` (Spectre.Console), created by `RigConsole.Create` in
`src/Rig.Cli/Rendering/`. The seam keeps one output contract for two audiences:

- **Interactive terminal**: ANSI colour, semantic badges (`create` green, `update` yellow, `delete`
  red, refusal orange) and rounded borders.
- **Redirected, captured or headless output**: the same layout with ANSI disabled. `RIG_HEADLESS`,
  `NO_COLOR`, `CI` and `TERM=dumb` force that mode; a writer that is not the real standard stream is
  treated as machine output.

Rules:

- Put dynamic text through `TextSanitizer.Markup` before it enters a Spectre markup string. It strips
  ANSI sequences and escapes brackets, so `[P1]` and `[core.hooksPath]` print as written.
- Use `SpectreOutputExtensions.RenderVerdict` for the shared verdict panel, and `TextTable` for tables. Do not
  reimplement column widths or padding by hand.
- `TextTable` lives in `Rig.Cli.Rendering`. Use `Render` for the plain machine contract and
  `RenderTo` when a command owns an `IAnsiConsole`.
- `--json` writes the typed model directly; never wrap it in presentation text.

### JSON

Write JSON through `Rig.Core.Text`. `JsonOutput.Format` and `JsonOutput.Line` render a node;
`JsonOutputOptions` carries the settings for a typed payload (`Indented`, `IndentedUnicode`,
`Compact`, `CompactUnicode`, and the snake_case pair the web app and `rig models` read). Every
shared option sets `NewLine = "\n"`, so a file rig writes is byte-identical on Windows and Unix
(#260). Do not declare another `JsonSerializerOptions` for an indented, compact or unicode
spelling: a site that needs a naming policy or a converter copies a shared one with
`new(JsonOutputOptions.IndentedUnicode) { … }`.

Read a payload through `Rig.Core.Text.JsonShape`; no module hand-walks `JsonObject` fields. The
strict accessors (`String`, `Int`, `Bool`, `Items`, …) keep pydantic's `FormatException` for data
rig owns; the `As*` and `Field*` pair answers null instead, for a payload another tool owns. A
site keeps its own exception type and wording on top of either.

`Rig.Core.Text.PyRepr` is the one copy of Python's `repr`/`str` and pydantic's compact JSON for a
value a message quotes.

______________________________________________________________________

## Tooling & Enforcement

### 1. Banned API Analyzers (`Microsoft.CodeAnalysis.BannedApiAnalyzers`)

Rig integrates `Microsoft.CodeAnalysis.BannedApiAnalyzers` centrally: `Directory.Build.props` imports `Common.props` from
[rtkelly-labs/foundations](https://github.com/rtkelly-labs/foundations), which reads the shared `BannedSymbols.txt` and a
root `BannedSymbols.txt` when the repo adds one of its own.

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
