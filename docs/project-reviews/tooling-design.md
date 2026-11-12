# Developer Tooling & Design scheduled review

Schedule key: `tooling-design`
Canonical memory: https://github.com/rtkelly13/rig/issues/25
Contract: [contract.md](contract.md), fetched at the same main commit as this file.

Follow the shared contract. This file is the current schedule-specific policy;
read it afresh on every run. Review scope and focus can change through merged
rig PRs without editing the Orca automation. This file does not alter schedule
frequency, the automation, credentials or the allowed issue-write destination.

## Repository allowlist

- `rtkelly-labs/design-system`
- `rtkelly-labs/mermaid-toolkit`
- `rtkelly-labs/.github`
- `rtkelly13/rig`
- `rtkelly13/github-agentic-workflow-experimentation`
- `Resultful/Resultful`
- `Resultful/FluentAssertions.Resultful`

## Focus

Design consistency and accessibility, developer experience, tooling reliability, CI/security governance and highest-value actionable improvements. Resultful: result semantics, API compatibility, packaging and FluentAssertions integration. Read historical Resultful memory #23 before assessment; publish combined reports only to #25. Establish a baseline when unassessed; thereafter deepen Resultful on material changes or when fifteen calendar weeks have elapsed since its last deep assessment. Track its last assessment and next due review in group memory; do not repeat an unchanged audit.

## Structural health

Apply the contract's structural health lens to every repository in this group.

For `rtkelly13/rig`:

- **Target:** judge against [docs/target-architecture.md](../target-architecture.md).
- **Plan:** the improvement plan is [#30](https://github.com/rtkelly13/rig/issues/30), tracked in the
  [rig simplification project](https://github.com/users/rtkelly13/projects/1). Reference plan item IDs (B3, D5,
  G2 …) rather than re-deriving findings the plan already holds. Report which items moved.
- **Snapshot metrics:**
  - entries left in the import-linter `ignore_imports` lists;
  - the raw-`subprocess` allowlist size;
  - the test patching allowlist size;
  - modules over the ratchet's `long-module` threshold.
- **Baseline:** rig's first structural baseline qualifies immediately.

The other repositories in this group follow the contract's 84-day cadence, independently of rig.

## Historical memory

- https://github.com/rtkelly13/rig/issues/23 — read as historical evidence; publish only to the canonical issue above.

## Selection guidance

Eight repositories share one run budget. Cheap intake is not a full audit of all
eight. Keep separate coverage rows and a bounded next-run queue. A previously
reported rig cleanup risk must not displace all other repositories indefinitely:
check its relevant PR head/state cheaply, then reference its finding ID if unchanged.

Resultful's historical issue is a read source only. Establish its initial
baseline when unassessed. After that, review it deeply when material changes
justify it or when fifteen calendar weeks have elapsed since its last deep assessment.
Store its last assessment and next due date in the canonical memory. Explain
any urgent displacement; do not silently reset its date or create a separate task.

Retained repository trackers: [organisation governance #15](https://github.com/rtkelly13/rig/issues/15),
[design-system #16](https://github.com/rtkelly13/rig/issues/16), and
[mermaid-toolkit #18](https://github.com/rtkelly13/rig/issues/18).
Import relevant historical evidence once; these trackers do not add write targets.

The group runs weekly; the deeper Resultful timer uses elapsed calendar time,
not three weekly occurrences. An initial unassessed baseline qualifies immediately;
after a deep assessment record its date and a next due date 105 days later.
Cheap weekly intake and a changed overall repository SHA do not reset that date.
