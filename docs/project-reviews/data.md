# Data scheduled review

Schedule key: `data`
Canonical memory: https://github.com/rtkelly13/rig/issues/22
Contract: [contract.md](contract.md), fetched at the same main commit as this file.

Follow the shared contract. This file is the current schedule-specific policy;
read it afresh on every run. Review scope and focus can change through merged
rig PRs without editing the Orca automation. This file does not alter schedule
frequency, the automation, credentials or the allowed issue-write destination.

## Repository allowlist

- `rtkelly-labs/Parquet.SourceGenerator`
- `rtkelly-labs/Lake.NET`
- `rtkelly-labs/Parquet.TypeProvider`
- `rtkelly-labs/parquet-dotnet`
- `rtkelly-labs/Arrow.SourceGenerator`

## Focus

Parquet/Arrow/Iceberg correctness and interoperability, source generation, resource safety and release evidence.

## Selection guidance

Keep coverage separately for each repository and for main versus candidate PRs.
Do not generalise a Parquet finding to Arrow or Iceberg without evidence. Track
format/version interoperability and generator/runtime boundaries explicitly.
Choose one changed or unassessed boundary; carry the remaining queue forward.
Read relevant retained per-repository trackers linked from the management guide
when importing their historical findings; do not restart a baseline already
supported by source-specific evidence there.

## Historical memory

Import relevant prior evidence once, preserving source refs and assessment dates:
[Parquet #13](https://github.com/rtkelly13/rig/issues/13),
[Arrow #14](https://github.com/rtkelly13/rig/issues/14),
[Iceberg #17](https://github.com/rtkelly13/rig/issues/17),
[parquet-dotnet #19](https://github.com/rtkelly13/rig/issues/19), and
[TypeProvider #20](https://github.com/rtkelly13/rig/issues/20).
These are read sources; new scheduled reports go only to #22.
