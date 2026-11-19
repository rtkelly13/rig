# ADR 0023: Declarative Native AOT .NET CLI Tooling Architecture

- **Status:** Accepted
- **Applies to:** .NET repositories, C# standalone tools, estate CLI utilities, Native AOT compilation

## Context

Across the estate, .NET and C# utilities (including tools in `blobflags`, `parquet-dotnet`, `Iceberg.Net`, and estate automation scripts) require sub-second cold starts, minimal memory footprints, and strong type safety.

Historically, .NET CLI development relied on reflection-heavy libraries like `CommandLineParser` or reflection-scanning frameworks like `Spectre.Console.Cli`. In modern .NET (8, 9, 10+), this creates severe architectural friction:
1. **Native AOT & Trimming Incompatibility**: Native AOT (`PublishAot=true`) and IL trimming ban runtime reflection and dynamic type inspection (`Activator.CreateInstance`, `Type.GetProperties`, `PropertyInfo.SetValue`). Reflection-based libraries fail trimming analysis or require broad linker preserve directives that bloat binaries and create runtime failure risks.
2. **`System.CommandLine` Boilerplate**: Microsoft's official `System.CommandLine` library provides powerful low-level parsing primitives, but writing imperative command trees manually requires repetitive, verbose boilerplate and manual binder glue.
3. **Declarative Ergonomics**: Developers accustomed to declarative attribute-based POCOs (`CommandLineParser`) need an idiomatic, strongly-typed pattern that preserves clean syntax definitions while emitting zero-reflection static code at compile time.

## Decision

Standardize on **`DotMake.CommandLine`** (v3.7.0+) powered by Roslyn incremental source generators (`IIncrementalGenerator`) as the estate architecture for C#/.NET CLI tools, governed by the following invariants:

1. **Native AOT by Default**:
   All compiled .NET CLI tools must configure `<PublishAot>true</PublishAot>` and pass compilation with zero trimming or AOT warnings.

2. **Declarative Separation via `partial class`**:
   - **Options (`Options/*Options.cs`)**: Pure POCO syntax definitions decorated with `[CliCommand(Name = "...")]`, `[CliOption]`, and `[CliArgument]`. These files must contain zero business logic, handler code, or external service references.
   - **Handlers (`Handlers/*Handler.cs`)**: Business logic implemented as `public async Task<int> RunAsync(CliContext context)` on the matching partial class.

3. **Parse-then-Run Pipeline with Structured Telemetry**:
   - Applications must implement a two-phase invocation pattern:
     ```csharp
     var settings = new CliSettings
     {
         // Prevent internal swallowing of exceptions; bubble up to structured logging
         EnableDefaultExceptionHandler = false
     };

     var result = Cli.Parse<RootOptions>(args, settings);
     if (result.ParseResult.Errors.Count > 0)
     {
         foreach (var err in result.ParseResult.Errors)
         {
             Console.Error.WriteLine(err.Message);
         }
         // Log structured validation errors before exiting
         return 1;
     }
     return await result.RunAsync(cancellationToken);
     ```
   - CLI parsing and syntax errors are written to stderr, intercepted from `result.ParseResult.Errors`, and forwarded to structured logging (JSON / `ILogger` / Serilog).
   - Setting `CliSettings.EnableDefaultExceptionHandler = false` ensures runtime execution exceptions bubble up to top-level `try / catch` structured telemetry rather than dumping raw console traces.

4. **Package Pinning** (amended by [ADR 0025](0025-paket-for-nuget-dependencies.md): Paket replaces Central Package Management):
   - Projects must pin exact package versions of both `DotMake.CommandLine` and its underlying transitive `System.CommandLine` dependency in `paket.dependencies`, with `paket.lock` pinning the transitives, to insulate estate tooling from unexpected transitive upgrades or upstream preview breaking changes.

## Consequences

- **Instant Cold Starts**: Native AOT binaries start in under 25ms with a self-contained ~4.3 MB binary footprint and zero external runtime dependencies.
- **Zero Reflection**: Compile-time source generation ensures 100% trim safety with concrete static type references and direct property bindings.
- **Clean Mental Model**: Retains the familiar attribute-driven POCO developer experience of `CommandLineParser` without sacrificing architectural hygiene or execution decoupling.
- **No Custom Shim Maintenance**: Avoids the maintenance burden of building and supporting an in-house Roslyn source generator over `System.CommandLine`.

## References

- [`DotMake.CommandLine` Repository](https://github.com/dotmake-build/command-line)
- Vault Architecture Standard: `Resources/Languages/DotNet/CLI Tooling Architecture.md`
- [ADR 0005: Idiomatic Per-Language Testing Stacks](0005-per-language-testing-stacks.md)
- [ADR 0020: CLI as Projection and Validation Engine, Not JSON CRUD](0020-cli-projection-and-validation-engine-not-json-crud.md)
