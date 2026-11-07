---
type: C4 Component
title: Parquet.SourceGenerator.ApiGates — namespaces
description: Namespaces as components; relationships from type references, checked against the call graph.
resource: https://github.com/rtkelly-labs/Parquet.SourceGenerator/tree/348590d5b72c
generated: { by: rig-c4-derive/0.1, at: 2026-11-04 }
tags: [derived]
---
```mermaid
C4Component
title Parquet.SourceGenerator.ApiGates: namespaces
System_Ext(csc, "C# compiler", "Roslyn")
Container_Ext(sourcegenerator, "Parquet.SourceGenerator", "analyzer · netstandard2.0", "37 source files")
Container_Ext(legacy, "Parquet.SourceGenerator.Legacy", "analyzer · netstandard2.0", "26 source files")
Container_Ext(runtime, "Parquet.SourceGenerator.Runtime", "library · netstandard2.0, netstandard2.1, net8.0, net9.0", "16 source files")
Container_Boundary(apigates, "Parquet.SourceGenerator.ApiGates") {
  Component(ns_apigates, "ApiGates", "9 types", "ApiCatalogue, ApiGateDiagnostics, ApiLedger, ApiShapeBudget …", $link="/code/apigates.md")
}
```

## ns_apigates

`Parquet.SourceGenerator.ApiGates`: 9 types in 7 files, 1801 lines, in tools/Parquet.SourceGenerator.ApiGates/

| File | Also declares | Lines |
|---|---|---|
| `ApiCatalogue.cs` |  | 88 |
| `ApiGateDiagnostics.cs` |  | 45 |
| `ApiLedger.cs` |  | 177 |
| `GeneratedApiBaseline.cs` |  | 672 |
| `GeneratedApiBudgetGate.cs` | ApiShapeBudget, ApiShapeMeasurement | 333 |
| `InternalSeamGateAnalyzer.cs` |  | 212 |
| `SeamSignature.cs` |  | 274 |

## sourcegenerator

See [its namespaces](/components/sourcegenerator.md).

## legacy

See [its namespaces](/components/legacy.md).

## runtime

See [its namespaces](/components/runtime.md).

## Derivation

Two pathways, scored against each other:

- **Both** type references and the call graph: 0 relationships.
- **Type references only** (mentions, signatures, or calls the graph could not resolve): none.
- **Call graph only** (a call with no type named, e.g. a static helper): none.
