---
type: C4 Container
title: Parquet.SourceGenerator — projects
description: Every project under src/ and what it references, read from the .csproj files.
resource: https://github.com/rtkelly-labs/Parquet.SourceGenerator/tree/348590d5b72c
generated: { by: rig-c4-derive/0.1, at: 2026-11-04 }
tags: [derived]
---
```mermaid
C4Container
title Parquet.SourceGenerator: projects
Person(dev, ".NET developer", "Marks C# types [ParquetSerializable]")
System_Ext(csc, "C# compiler", "Roslyn, in the consumer's build")
System_Ext(parquetnet, "Parquet.Net", "Reads and writes Parquet files")
System_Ext(nuget, "NuGet.org", "Package feed")
System_Boundary(psg, "Parquet.SourceGenerator") {
  Container(sourcegenerator, "Parquet.SourceGenerator", "analyzer · netstandard2.0", "37 source files", $link="/components/sourcegenerator.md")
  Container(apigates, "Parquet.SourceGenerator.ApiGates", "analyzer · netstandard2.0", "7 source files", $link="/components/apigates.md")
  Container(legacy, "Parquet.SourceGenerator.Legacy", "analyzer · netstandard2.0", "26 source files", $link="/components/legacy.md")
  Container(runtime, "Parquet.SourceGenerator.Runtime", "library · netstandard2.0, netstandard2.1, net8.0, net9.0", "16 source files", $link="/components/runtime.md")
}
Rel(dev, psg, "Annotates models for")
Rel(psg, parquetnet, "Emits code that calls")
Rel(psg, nuget, "Ships as packages via")
Rel(csc, sourcegenerator, "loads into the build", "Roslyn")
Rel(sourcegenerator, runtime, "references", "ProjectReference")
Rel(apigates, sourcegenerator, "checks at build", "analyzer reference")
Rel(csc, apigates, "loads into the build", "Roslyn")
Rel(csc, legacy, "loads into the build", "Roslyn")
Rel(legacy, runtime, "references", "ProjectReference")
Rel(apigates, legacy, "checks at build", "analyzer reference")
Rel(apigates, runtime, "checks at build", "analyzer reference")
```

Derived by `rig c4 derive dotnet` from commit `348590d5b72c`; edits here are overwritten. The people and systems outside the boundary are copied from the parent view.

## sourcegenerator

Ships as a Roslyn analyzer. Targets netstandard2.0. 37 source files, 14029 lines.

Project file: `Parquet.SourceGenerator.csproj` in src/Parquet.SourceGenerator/

## apigates

Ships as a Roslyn analyzer. Targets netstandard2.0. 7 source files, 1801 lines.

Project file: `Parquet.SourceGenerator.ApiGates.csproj` in tools/Parquet.SourceGenerator.ApiGates/

## legacy

Ships as a Roslyn analyzer. Targets netstandard2.0. 26 source files, 6827 lines. 23 of them are compiled in from another project.

Project file: `Parquet.SourceGenerator.Legacy.csproj` in src/Parquet.SourceGenerator.Legacy/

## runtime

Ships as a library. Targets netstandard2.0, netstandard2.1, net8.0, net9.0. 16 source files, 3040 lines.

Project file: `Parquet.SourceGenerator.Runtime.csproj` in src/Parquet.SourceGenerator.Runtime/
