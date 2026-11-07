---
type: C4 Code
title: Parquet.SourceGenerator.Emitter.Compound — types
description: Types in the namespace and which of them name each other. Each type links to its source.
resource: https://github.com/rtkelly-labs/Parquet.SourceGenerator/tree/348590d5b72c
generated: { by: rig-c4-derive/0.1, at: 2026-11-04 }
tags: [derived]
---
```mermaid
classDiagram
direction LR
class ChainStepKind {
  <<enum>>
}
class CompoundBuffers {
  +GetPackedType()
  +EmitWriteRentals()
  +EmitSingleWriteReturn()
  +EmitWriteReturns()
  +EmitRentals()
  +EmitReturns()
}
class CompoundMapping {
  +GetCompoundLeafWriteExpression()
  +GetCompoundLeafReadExpression()
  +EmitCompoundExtractionCall()
  +EmitListExtractionCall()
  +EmitWriteHelpers()
  +EmitCompoundReconstruction()
  +EmitReadHelpers()
  +EmitRootMemberAssignments()
}
class CompoundSchema {
  +GetFieldCreationExpression()
  +EmitSchema()
  +EmitStaticFields()
}
class EmissionPlan
class LeafColumn
class StructNode
ChainStepKind --> CompoundBuffers
CompoundBuffers --> EmissionPlan
CompoundBuffers --> LeafColumn
CompoundMapping --> EmissionPlan
CompoundMapping --> LeafColumn
CompoundMapping --> StructNode
CompoundSchema --> EmissionPlan
CompoundSchema --> LeafColumn
EmissionPlan --> CompoundBuffers
LeafColumn --> CompoundBuffers
StructNode --> CompoundBuffers
click CompoundBuffers href "https://github.com/rtkelly-labs/Parquet.SourceGenerator/blob/348590d5b72c/src/Parquet.SourceGenerator/Emitter/Compound/CompoundBuffers.cs"
click CompoundMapping href "https://github.com/rtkelly-labs/Parquet.SourceGenerator/blob/348590d5b72c/src/Parquet.SourceGenerator/Emitter/Compound/CompoundMapping.cs"
click CompoundSchema href "https://github.com/rtkelly-labs/Parquet.SourceGenerator/blob/348590d5b72c/src/Parquet.SourceGenerator/Emitter/Compound/CompoundSchema.cs"
click ChainStepKind href "https://github.com/rtkelly-labs/Parquet.SourceGenerator/blob/348590d5b72c/src/Parquet.SourceGenerator/Emitter/Compound/LeafColumns.cs"
click EmissionPlan href "https://github.com/rtkelly-labs/Parquet.SourceGenerator/blob/348590d5b72c/src/Parquet.SourceGenerator/Emitter/Compound/LeafColumns.cs"
click LeafColumn href "https://github.com/rtkelly-labs/Parquet.SourceGenerator/blob/348590d5b72c/src/Parquet.SourceGenerator/Emitter/Compound/LeafColumns.cs"
click StructNode href "https://github.com/rtkelly-labs/Parquet.SourceGenerator/blob/348590d5b72c/src/Parquet.SourceGenerator/Emitter/Compound/LeafColumns.cs"
```

## ChainStepKind

**enum** · What a leaf column's member chain step contributes to the def ladder.

[LeafColumns.cs](https://github.com/rtkelly-labs/Parquet.SourceGenerator/blob/348590d5b72c/src/Parquet.SourceGenerator/Emitter/Compound/LeafColumns.cs) in src/Parquet.SourceGenerator/Emitter/Compound/ · 419 lines

## CompoundBuffers

**class** · Buffer rental/return lifecycle for models containing compound members.

[CompoundBuffers.cs](https://github.com/rtkelly-labs/Parquet.SourceGenerator/blob/348590d5b72c/src/Parquet.SourceGenerator/Emitter/Compound/CompoundBuffers.cs) in src/Parquet.SourceGenerator/Emitter/Compound/ · 289 lines

Public: `GetPackedType()`, `EmitWriteRentals()`, `EmitSingleWriteReturn()`, `EmitWriteReturns()`, `EmitRentals()`, `EmitReturns()`

## CompoundMapping

**class** · Compound write extraction and read reconstruction for the v6 emitter (issue #176 M2).

[CompoundMapping.cs](https://github.com/rtkelly-labs/Parquet.SourceGenerator/blob/348590d5b72c/src/Parquet.SourceGenerator/Emitter/Compound/CompoundMapping.cs) in src/Parquet.SourceGenerator/Emitter/Compound/ · 872 lines

Public: `GetCompoundLeafWriteExpression()`, `GetCompoundLeafReadExpression()`, `EmitCompoundExtractionCall()`, `EmitListExtractionCall()`, `EmitWriteHelpers()`, `EmitCompoundReconstruction()`, `EmitReadHelpers()`, `EmitRootMemberAssignments()`

## CompoundSchema

**class** · Schema and static-field emission for compound models: StructField recursion in the schema, and one cached DataField per leaf column reached through the StructField cast chain.

[CompoundSchema.cs](https://github.com/rtkelly-labs/Parquet.SourceGenerator/blob/348590d5b72c/src/Parquet.SourceGenerator/Emitter/Compound/CompoundSchema.cs) in src/Parquet.SourceGenerator/Emitter/Compound/ · 85 lines

Public: `GetFieldCreationExpression()`, `EmitSchema()`, `EmitStaticFields()`

## EmissionPlan

**class** · The flattened column plan + struct reconstruction tree for one model.

[LeafColumns.cs](https://github.com/rtkelly-labs/Parquet.SourceGenerator/blob/348590d5b72c/src/Parquet.SourceGenerator/Emitter/Compound/LeafColumns.cs) in src/Parquet.SourceGenerator/Emitter/Compound/ · 419 lines

## LeafColumn

**class** · One leaf column of the flattened emission plan.

[LeafColumns.cs](https://github.com/rtkelly-labs/Parquet.SourceGenerator/blob/348590d5b72c/src/Parquet.SourceGenerator/Emitter/Compound/LeafColumns.cs) in src/Parquet.SourceGenerator/Emitter/Compound/ · 419 lines

## StructNode

**class** · A struct node to reconstruct per row group on the read path.

[LeafColumns.cs](https://github.com/rtkelly-labs/Parquet.SourceGenerator/blob/348590d5b72c/src/Parquet.SourceGenerator/Emitter/Compound/LeafColumns.cs) in src/Parquet.SourceGenerator/Emitter/Compound/ · 419 lines
