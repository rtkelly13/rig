---
type: C4 Component
title: Parquet.SourceGenerator.Runtime — namespaces
description: Namespaces as components; relationships from type references, checked against the call graph.
resource: https://github.com/rtkelly-labs/Parquet.SourceGenerator/tree/348590d5b72c
generated: { by: rig-c4-derive/0.1, at: 2026-11-04 }
tags: [derived]
---
```mermaid
C4Component
title Parquet.SourceGenerator.Runtime: namespaces
Container_Ext(sourcegenerator, "Parquet.SourceGenerator", "analyzer · netstandard2.0", "37 source files")
Container_Ext(apigates, "Parquet.SourceGenerator.ApiGates", "analyzer · netstandard2.0", "7 source files")
Container_Ext(legacy, "Parquet.SourceGenerator.Legacy", "analyzer · netstandard2.0", "26 source files")
Container_Boundary(runtime, "Parquet.SourceGenerator.Runtime") {
  Component(ns_sourcegenerator_runtime, "SourceGenerator", "20 types", "FooterScanner, NullableColumnExtractor, PageHeaderReader, ParquetColumnAttribute …", $link="/code/sourcegenerator--runtime.md")
}
```

## ns_sourcegenerator_runtime

`Parquet.SourceGenerator`: 20 types in 14 files, 3024 lines, in src/Parquet.SourceGenerator.Runtime/

| File | Also declares | Lines |
|---|---|---|
| `NullableColumnExtractor.cs` |  | 293 |
| `ParquetColumnAttribute.cs` | ParquetColumnEncoding | 105 |
| `ParquetColumnStatistics.cs` |  | 256 |
| `ParquetDecimalAttribute.cs` |  | 35 |
| `ParquetDecimalScale.cs` |  | 132 |
| `ParquetDecompressionGuardStream.cs` | FooterScanner, PageHeaderReader | 952 |
| `ParquetIgnoreAttribute.cs` |  | 13 |
| `ParquetPruneStatistics.cs` |  | 54 |
| `ParquetSerializableAttribute.cs` |  | 19 |
| `ParquetSerializerOptions.cs` | ParquetCompressionLevel, ParquetCompressionMethod | 256 |
| `ParquetSortKeyAttribute.cs` |  | 29 |
| `ParquetTimestampAttribute.cs` | ParquetTimestampUnit | 57 |

And 2 more files.

## sourcegenerator

See [its namespaces](/components/sourcegenerator.md).

## apigates

See [its namespaces](/components/apigates.md).

## legacy

See [its namespaces](/components/legacy.md).

## Derivation

Two pathways, scored against each other:

- **Both** type references and the call graph: 0 relationships.
- **Type references only** (mentions, signatures, or calls the graph could not resolve): none.
- **Call graph only** (a call with no type named, e.g. a static helper): none.
