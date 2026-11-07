---
type: C4 Context
title: Parquet.SourceGenerator — system context
description: Who uses the generator, what loads it, and what the code it writes depends on. Hand-written; every view below it is derived from code.
resource: https://github.com/rtkelly-labs/Parquet.SourceGenerator
generated: { by: claude-code/opus-5-5, at: 2026-11-04 }
---

```mermaid
C4Context
title Parquet.SourceGenerator: system context
Person(dev, ".NET developer", "Marks C# types [ParquetSerializable]")
System(psg, "Parquet.SourceGenerator", "Roslyn incremental generator + runtime helpers", $link="containers.md")
System_Ext(csc, "C# compiler", "Roslyn, in the consumer's build")
System_Ext(parquetnet, "Parquet.Net", "Reads and writes Parquet files")
System_Ext(nuget, "NuGet.org", "Package feed")
Rel(dev, psg, "Annotates models for")
Rel(csc, psg, "Loads as an analyzer")
Rel(psg, parquetnet, "Emits code that calls")
Rel(psg, nuget, "Ships as packages via")
```

This view is the only hand-drawn one in the bundle. Everything below `psg` is written by
`rig c4 derive dotnet` from the repository's `.csproj` files and C# sources, so it can be regenerated and
checked for staleness. See [the projects](containers.md).

## psg

The generator runs **inside the consumer's compiler**: it is loaded as an analyzer, reads the annotated types
and emits `<Model>ParquetExtensions.g.cs`. Nothing of the generator itself ships in the consumer's
assemblies. The runtime package does: attributes plus the helpers the emitted code calls.

## parquetnet

Not a dependency of any project under `src/`. Only the *emitted* code calls Parquet.Net, which is why the
derived views cannot see this edge and it has to be drawn here.

## csc

The same alias appears in the derived container and component views, so `rig c4 model` merges them into one
element across three levels.
