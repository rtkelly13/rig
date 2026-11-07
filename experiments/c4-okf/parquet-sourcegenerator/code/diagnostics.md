---
type: C4 Code
title: Parquet.SourceGenerator.Diagnostics — types
description: Types in the namespace and which of them name each other. Each type links to its source.
resource: https://github.com/rtkelly-labs/Parquet.SourceGenerator/tree/348590d5b72c
generated: { by: rig-c4-derive/0.1, at: 2026-11-04 }
tags: [derived]
---
```mermaid
classDiagram
direction LR
class DiagnosticDescriptors {
  +MustBePartial
  +DuplicateColumnName
  +NoPropertiesFound
  +NonPublicPropertyIgnored
  +InvalidDecimalPrecisionScale
  +UnsupportedPropertyType
  +MemberNotAssignable
  +NoParameterlessConstructor
}
click DiagnosticDescriptors href "https://github.com/rtkelly-labs/Parquet.SourceGenerator/blob/348590d5b72c/src/Parquet.SourceGenerator/Diagnostics/DiagnosticDescriptors.cs"
```

## DiagnosticDescriptors

**class** · Defines Roslyn diagnostic rules for compile-time validation of [ParquetSerializable] types.

[DiagnosticDescriptors.cs](https://github.com/rtkelly-labs/Parquet.SourceGenerator/blob/348590d5b72c/src/Parquet.SourceGenerator/Diagnostics/DiagnosticDescriptors.cs) in src/Parquet.SourceGenerator/Diagnostics/ · 331 lines

Public: `MustBePartial`, `DuplicateColumnName`, `NoPropertiesFound`, `NonPublicPropertyIgnored`, `InvalidDecimalPrecisionScale`, `UnsupportedPropertyType`, `MemberNotAssignable`, `NoParameterlessConstructor`
