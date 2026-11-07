---
type: C4 Component
title: Parquet.SourceGenerator — namespaces
description: Namespaces as components; relationships from type references, checked against the call graph.
resource: https://github.com/rtkelly-labs/Parquet.SourceGenerator/tree/348590d5b72c
generated: { by: rig-c4-derive/0.1, at: 2026-11-04 }
tags: [derived]
---
```mermaid
C4Component
title Parquet.SourceGenerator: namespaces
System_Ext(csc, "C# compiler", "Roslyn")
Container_Ext(apigates, "Parquet.SourceGenerator.ApiGates", "analyzer · netstandard2.0", "7 source files")
Container_Ext(runtime, "Parquet.SourceGenerator.Runtime", "library · netstandard2.0, netstandard2.1, net8.0, net9.0", "16 source files")
Container_Boundary(sourcegenerator, "Parquet.SourceGenerator") {
  Component(ns_sourcegenerator, "SourceGenerator", "1 type", "ParquetIncrementalGenerator", $link="/code/sourcegenerator.md")
  Component(ns_diagnostics, "Diagnostics", "1 type", "DiagnosticDescriptors", $link="/code/diagnostics.md")
  Component(ns_emitter, "Emitter", "7 types", "ArrowBridgeEmitter, CodeEmitter, ColumnHelpersComponent, NullableReadShape …", $link="/code/emitter.md")
  Component(ns_emitter_columnar, "Emitter.Columnar", "5 types", "BatchReadComponent, ColumnarBatchComponent, LaneKind, MemberNames …", $link="/code/emitter_columnar.md")
  Component(ns_emitter_components, "Emitter.Components", "16 types", "AllocationBudgetComponent, ArrowExtractionMode, ArrowLeafMapping, ArrowMappingComponent …", $link="/code/emitter_components.md")
  Component(ns_emitter_compound, "Emitter.Compound", "7 types", "ChainStepKind, CompoundBuffers, CompoundMapping, CompoundSchema …", $link="/code/emitter_compound.md")
  Component(ns_models, "Models", "9 types", "ColumnEncoding, DiagnosticInfo, EquatableArray, GeneratorConfiguration …", $link="/code/models.md")
  Component(ns_parser, "Parser", "9 types", "ColumnOptions, CompoundKinds, CompoundOutcome, MemberScope …", $link="/code/parser.md")
}
Rel(csc, ns_sourcegenerator, "runs")
Rel(ns_emitter, ns_emitter_columnar, "uses", "2 types, 5 calls")
Rel(ns_emitter, ns_emitter_components, "uses", "14 types, 61 calls")
Rel(ns_emitter, ns_emitter_compound, "uses", "5 types, 37 calls")
Rel(ns_emitter, ns_models, "uses", "5 types")
Rel(ns_emitter_columnar, ns_emitter, "uses", "2 types, 3 calls")
Rel(ns_emitter_columnar, ns_emitter_components, "uses", "4 types, 17 calls")
Rel(ns_emitter_columnar, ns_emitter_compound, "uses", "2 types, 2 calls")
Rel(ns_emitter_columnar, ns_models, "uses", "3 types")
Rel(ns_emitter_components, ns_models, "uses", "4 types")
Rel(ns_emitter_compound, ns_emitter_components, "uses", "5 types, 29 calls")
Rel(ns_emitter_compound, ns_models, "uses", "3 types")
Rel(ns_parser, ns_diagnostics, "uses", "1 type")
Rel(ns_parser, ns_models, "uses", "7 types, 1 call")
Rel(ns_sourcegenerator, ns_emitter, "uses", "2 types, 3 calls")
Rel(ns_sourcegenerator, ns_models, "uses", "3 types, 2 calls")
Rel(ns_sourcegenerator, ns_parser, "uses", "5 types, 1 call")
```

## ns_sourcegenerator

`Parquet.SourceGenerator`: 1 type in 1 file, 166 lines, in src/Parquet.SourceGenerator/

| File | Also declares | Lines |
|---|---|---|
| `ParquetIncrementalGenerator.cs` |  | 166 |

## ns_diagnostics

`Parquet.SourceGenerator.Diagnostics`: 1 type in 1 file, 331 lines, in src/Parquet.SourceGenerator/Diagnostics/

| File | Also declares | Lines |
|---|---|---|
| `DiagnosticDescriptors.cs` |  | 331 |

## ns_emitter

`Parquet.SourceGenerator.Emitter`: 7 types in 6 files, 4847 lines, in src/Parquet.SourceGenerator/Emitter/

| File | Also declares | Lines |
|---|---|---|
| `ArrowBridgeEmitter.cs` |  | 1101 |
| `CodeEmitter.cs` |  | 2397 |
| `ColumnHelpersComponent.cs` | NullableReadShape | 503 |
| `ReadBudget.cs` |  | 44 |
| `ReadBuilderComponent.cs` |  | 379 |
| `RowGroupPruningComponent.cs` |  | 423 |

## ns_emitter_columnar

`Parquet.SourceGenerator.Emitter.Columnar`: 5 types in 2 files, 1612 lines, in src/Parquet.SourceGenerator/Emitter/Columnar/

| File | Also declares | Lines |
|---|---|---|
| `BatchReadComponent.cs` |  | 579 |
| `ColumnarBatchComponent.cs` | LaneKind, MemberNames, ParameterNames | 1033 |

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

## ns_emitter_compound

`Parquet.SourceGenerator.Emitter.Compound`: 7 types in 4 files, 1665 lines, in src/Parquet.SourceGenerator/Emitter/Compound/

| File | Also declares | Lines |
|---|---|---|
| `CompoundBuffers.cs` |  | 289 |
| `CompoundMapping.cs` |  | 872 |
| `CompoundSchema.cs` |  | 85 |
| `LeafColumns.cs` | ChainStepKind, EmissionPlan, LeafColumn … | 419 |

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

- **Both** type references and the call graph: 11 relationships.
- **Type references only** (mentions, signatures, or calls the graph could not resolve): `ns_emitter → ns_models`, `ns_emitter_columnar → ns_models`, `ns_emitter_components → ns_models`, `ns_emitter_compound → ns_models`, `ns_parser → ns_diagnostics`.
- **Call graph only** (a call with no type named, e.g. a static helper): none.
