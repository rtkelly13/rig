---
type: C4 Code
title: Parquet.SourceGenerator.Models — types
description: Types in the namespace and which of them name each other. Each type links to its source.
resource: https://github.com/rtkelly-labs/Parquet.SourceGenerator/tree/348590d5b72c
generated: { by: rig-c4-derive/0.1, at: 2026-11-04 }
tags: [derived]
---
```mermaid
classDiagram
direction LR
class ColumnEncoding {
  <<enum>>
}
class DiagnosticInfo {
  <<record>>
}
class EquatableArray {
  <<struct>>
  +Empty
  +Length
  +Equals()
  +GetHashCode()
  +AsSpan()
  +GetEnumerator()
}
class GeneratorConfiguration {
  <<record>>
  +Default
  +From()
}
class PropertyKind {
  <<enum>>
}
class PropertyModel {
  <<record>>
}
class SortKeyEligibility {
  +IsEligible()
}
class SourcePosition {
  <<record>>
}
class TargetClassModel {
  <<record>>
}
ColumnEncoding --> EquatableArray
DiagnosticInfo --> EquatableArray
GeneratorConfiguration --> DiagnosticInfo
PropertyKind --> EquatableArray
PropertyModel --> EquatableArray
SortKeyEligibility --> PropertyKind
SortKeyEligibility --> PropertyModel
SourcePosition --> EquatableArray
TargetClassModel --> EquatableArray
TargetClassModel --> PropertyModel
click DiagnosticInfo href "https://github.com/rtkelly-labs/Parquet.SourceGenerator/blob/348590d5b72c/src/Parquet.SourceGenerator/Models/DiagnosticInfo.cs"
click SourcePosition href "https://github.com/rtkelly-labs/Parquet.SourceGenerator/blob/348590d5b72c/src/Parquet.SourceGenerator/Models/DiagnosticInfo.cs"
click EquatableArray href "https://github.com/rtkelly-labs/Parquet.SourceGenerator/blob/348590d5b72c/src/Parquet.SourceGenerator/Models/EquatableArray.cs"
click GeneratorConfiguration href "https://github.com/rtkelly-labs/Parquet.SourceGenerator/blob/348590d5b72c/src/Parquet.SourceGenerator/Models/GeneratorConfiguration.cs"
click ColumnEncoding href "https://github.com/rtkelly-labs/Parquet.SourceGenerator/blob/348590d5b72c/src/Parquet.SourceGenerator/Models/PropertyModel.cs"
click PropertyKind href "https://github.com/rtkelly-labs/Parquet.SourceGenerator/blob/348590d5b72c/src/Parquet.SourceGenerator/Models/PropertyModel.cs"
click PropertyModel href "https://github.com/rtkelly-labs/Parquet.SourceGenerator/blob/348590d5b72c/src/Parquet.SourceGenerator/Models/PropertyModel.cs"
click SortKeyEligibility href "https://github.com/rtkelly-labs/Parquet.SourceGenerator/blob/348590d5b72c/src/Parquet.SourceGenerator/Models/SortKeyEligibility.cs"
click TargetClassModel href "https://github.com/rtkelly-labs/Parquet.SourceGenerator/blob/348590d5b72c/src/Parquet.SourceGenerator/Models/TargetClassModel.cs"
```

## ColumnEncoding

**enum** · Specifies the physical column encoding hint for code generation.

[PropertyModel.cs](https://github.com/rtkelly-labs/Parquet.SourceGenerator/blob/348590d5b72c/src/Parquet.SourceGenerator/Models/PropertyModel.cs) in src/Parquet.SourceGenerator/Models/ · 146 lines

## DiagnosticInfo

**record** · Value-equatable model representing a Roslyn diagnostic for incremental pipeline caching.

[DiagnosticInfo.cs](https://github.com/rtkelly-labs/Parquet.SourceGenerator/blob/348590d5b72c/src/Parquet.SourceGenerator/Models/DiagnosticInfo.cs) in src/Parquet.SourceGenerator/Models/ · 81 lines

## EquatableArray

**struct** · Immutable, value-equatable array wrapper designed for Roslyn incremental generator caching.

[EquatableArray.cs](https://github.com/rtkelly-labs/Parquet.SourceGenerator/blob/348590d5b72c/src/Parquet.SourceGenerator/Models/EquatableArray.cs) in src/Parquet.SourceGenerator/Models/ · 106 lines

Public: `Empty`, `Length`, `Equals()`, `GetHashCode()`, `AsSpan()`, `GetEnumerator()`

## GeneratorConfiguration

**record** · Value-equatable project configuration carried through the incremental pipeline.

[GeneratorConfiguration.cs](https://github.com/rtkelly-labs/Parquet.SourceGenerator/blob/348590d5b72c/src/Parquet.SourceGenerator/Models/GeneratorConfiguration.cs) in src/Parquet.SourceGenerator/Models/ · 68 lines

Public: `Default`, `From()`

## PropertyKind

**enum**

[PropertyModel.cs](https://github.com/rtkelly-labs/Parquet.SourceGenerator/blob/348590d5b72c/src/Parquet.SourceGenerator/Models/PropertyModel.cs) in src/Parquet.SourceGenerator/Models/ · 146 lines

## PropertyModel

**record** · Value-equatable model representing a single property or field binding.

[PropertyModel.cs](https://github.com/rtkelly-labs/Parquet.SourceGenerator/blob/348590d5b72c/src/Parquet.SourceGenerator/Models/PropertyModel.cs) in src/Parquet.SourceGenerator/Models/ · 146 lines

## SortKeyEligibility

**class** · Decides whether a root member can drive sorted row-group pruning (issue #151), and says why not when it cannot.

[SortKeyEligibility.cs](https://github.com/rtkelly-labs/Parquet.SourceGenerator/blob/348590d5b72c/src/Parquet.SourceGenerator/Models/SortKeyEligibility.cs) in src/Parquet.SourceGenerator/Models/ · 97 lines

Public: `IsEligible()`

## SourcePosition

**record** · A source position held as plain data, so a diagnostic can sit in a cached pipeline value without holding a Location (and through it the SyntaxTree).

[DiagnosticInfo.cs](https://github.com/rtkelly-labs/Parquet.SourceGenerator/blob/348590d5b72c/src/Parquet.SourceGenerator/Models/DiagnosticInfo.cs) in src/Parquet.SourceGenerator/Models/ · 81 lines

## TargetClassModel

**record** · Value-equatable model representing a class, record, or struct marked for Parquet code generation.

[TargetClassModel.cs](https://github.com/rtkelly-labs/Parquet.SourceGenerator/blob/348590d5b72c/src/Parquet.SourceGenerator/Models/TargetClassModel.cs) in src/Parquet.SourceGenerator/Models/ · 21 lines
