---
type: C4 Component
title: Parquet.SourceGenerator.Legacy — namespaces
description: Namespaces as components; relationships from type references, checked against the call graph.
resource: https://github.com/rtkelly-labs/Parquet.SourceGenerator/tree/348590d5b72c
generated: { by: rig-c4-derive/0.1, at: 2026-11-04 }
tags: [derived]
---
```mermaid
C4Component
title Parquet.SourceGenerator.Legacy: namespaces
System_Ext(csc, "C# compiler", "Roslyn")
Container_Ext(apigates, "Parquet.SourceGenerator.ApiGates", "analyzer · netstandard2.0", "7 source files")
Container_Ext(runtime, "Parquet.SourceGenerator.Runtime", "library · netstandard2.0, netstandard2.1, net8.0, net9.0", "16 source files")
Container_Boundary(legacy, "Parquet.SourceGenerator.Legacy") {
  Component(ns_diagnostics, "Diagnostics", "1 type", "DiagnosticDescriptors", $link="/code/diagnostics.md")
  Component(ns_emitter_components, "Emitter.Components", "16 types", "AllocationBudgetComponent, ArrowExtractionMode, ArrowLeafMapping, ArrowMappingComponent …", $link="/code/emitter_components.md")
  Component(ns_legacy, "Legacy", "1 type", "ParquetLegacyIncrementalGenerator", $link="/code/legacy.md")
  Component(ns_legacy_emitter, "Legacy.Emitter", "1 type", "LegacyCodeEmitter", $link="/code/legacy_emitter.md")
  Component(ns_models, "Models", "9 types", "ColumnEncoding, DiagnosticInfo, EquatableArray, GeneratorConfiguration …", $link="/code/models.md")
  Component(ns_parser, "Parser", "9 types", "ColumnOptions, CompoundKinds, CompoundOutcome, MemberScope …", $link="/code/parser.md")
}
Rel(csc, ns_legacy, "runs")
Rel(ns_emitter_components, ns_models, "uses", "4 types")
Rel(ns_legacy, ns_legacy_emitter, "uses", "1 type, 1 call")
Rel(ns_legacy, ns_models, "uses", "2 types, 2 calls")
Rel(ns_legacy, ns_parser, "uses", "3 types, 1 call")
Rel(ns_legacy_emitter, ns_emitter_components, "uses", "8 types, 14 calls")
Rel(ns_legacy_emitter, ns_models, "uses", "4 types")
Rel(ns_parser, ns_diagnostics, "uses", "1 type")
Rel(ns_parser, ns_models, "uses", "7 types, 1 call")
```

## ns_diagnostics

`Parquet.SourceGenerator.Diagnostics`: 1 type in 1 file, 331 lines, in src/Parquet.SourceGenerator/Diagnostics/

| File | Also declares | Lines |
|---|---|---|
| `DiagnosticDescriptors.cs` |  | 331 |

## ns_emitter_components

`Parquet.SourceGenerator.Emitter.Components`: 16 types in 12 files, 2381 lines, in src/Parquet.SourceGenerator/Emitter/Components/

| File | Also declares | Lines |
|---|---|---|
| `AllocationBudgetComponent.cs` |  | 103 |
| `ArrowMappingComponent.cs` | ArrowExtractionMode, ArrowLeafMapping | 327 |
| `BatchValidationComponent.cs` |  | 28 |
| `BufferPoolComponent.cs` |  | 311 |
| `DecompressionGuardComponent.cs` |  | 18 |
| `DictionaryPageComponent.cs` |  | 157 |
| `EmittedText.cs` |  | 63 |
| `IndentedWriter.cs` | BlockScope, IndentScope | 129 |
| `PropertyMappingComponent.cs` |  | 367 |
| `RowGroupLayoutComponent.cs` |  | 138 |
| `SchemaComponent.cs` |  | 558 |
| `StringDeduplicatorComponent.cs` |  | 182 |

## ns_legacy

`Parquet.SourceGenerator.Legacy`: 1 type in 1 file, 84 lines, in src/Parquet.SourceGenerator.Legacy/

| File | Also declares | Lines |
|---|---|---|
| `ParquetLegacyIncrementalGenerator.cs` |  | 84 |

## ns_legacy_emitter

`Parquet.SourceGenerator.Legacy.Emitter`: 1 type in 1 file, 1005 lines, in src/Parquet.SourceGenerator.Legacy/Emitter/

| File | Also declares | Lines |
|---|---|---|
| `LegacyCodeEmitter.cs` |  | 1005 |

## ns_models

`Parquet.SourceGenerator.Models`: 9 types in 6 files, 519 lines, in src/Parquet.SourceGenerator/Models/

| File | Also declares | Lines |
|---|---|---|
| `DiagnosticInfo.cs` | SourcePosition | 81 |
| `EquatableArray.cs` |  | 106 |
| `GeneratorConfiguration.cs` |  | 68 |
| `PropertyModel.cs` | ColumnEncoding, PropertyKind | 146 |
| `SortKeyEligibility.cs` |  | 97 |
| `TargetClassModel.cs` |  | 21 |

## ns_parser

`Parquet.SourceGenerator.Parser`: 9 types in 3 files, 2496 lines, in src/Parquet.SourceGenerator/Parser/

| File | Also declares | Lines |
|---|---|---|
| `CompoundKinds.cs` |  | 32 |
| `ParquetApiLevel.cs` |  | 24 |
| `TargetParser.cs` | ColumnOptions, CompoundOutcome, MemberScope … | 2440 |

## apigates

See [its namespaces](/components/apigates.md).

## runtime

See [its namespaces](/components/runtime.md).

## Derivation

Two pathways, scored against each other:

- **Both** type references and the call graph: 5 relationships.
- **Type references only** (mentions, signatures, or calls the graph could not resolve): `ns_emitter_components → ns_models`, `ns_legacy_emitter → ns_models`, `ns_parser → ns_diagnostics`.
- **Call graph only** (a call with no type named, e.g. a static helper): none.
