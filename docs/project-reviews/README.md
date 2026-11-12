# Review policies on main

These files are the instructions for the four weekly project reviews. They run as
Orca automations on the personal machine, declared in
[config/orca-automations.json](../../config/orca-automations.json) next to the
daily estate orchestrator. Codex maintains the policies through rig PRs; after a
squash merge the next run fetches the updated main policy, because the
[bootstrap](bootstrap.md) prompt resolves main on every run.

| Schedule | Policy | Canonical memory |
| --- | --- | --- |
| Data | [data.md](data.md) | [#22](https://github.com/rtkelly13/rig/issues/22) |
| Diffra | [diffra.md](diffra.md) | [#12](https://github.com/rtkelly13/rig/issues/12) |
| Developer Tooling & Design, including Resultful | [tooling-design.md](tooling-design.md) | [#25](https://github.com/rtkelly13/rig/issues/25) |
| Personal | [personal.md](personal.md) | [#24](https://github.com/rtkelly13/rig/issues/24) |

Each group runs once a week at 02:00 Europe/London: Data Monday, Diffra Tuesday,
Tooling & Design Thursday, Personal Friday. A run missed while the machine slept
still starts within 12 hours. Each policy imports [contract.md](contract.md),
which defines continuation, coverage, finding identities, delta selection and
publication. Policy text lives here; changing analysis state lives in the issue
description and dated reports.

## Automations

The reviews used to be native ChatGPT Work tasks driven through the browser
([config/chatgpt-projects.json](../../config/chatgpt-projects.json) keeps that
history). Nothing could change them without a person at the UI, so they moved to
Orca, which rig reconciles from the CLI:

```bash
uv run workspace orca automations           # plan
uv run workspace orca automations --apply   # create or update them
```

Entries are `personal`-role only: on a work machine they show as `skip`, and the
bootstrap stops a run whose machine is not personal. Each entry picks its agent
(`provider.agent`, `codex` for the reviews). See
[Orca: scheduled automations](../orca.md#scheduled-automations) for how instances,
binding and fields work.

## Editing and rollout

1. Change the appropriate policy, or the shared contract for a common behaviour.
   Keep the schedule key and canonical issue matching the manifest.
2. Review changed repository scope, revisit triggers, finding-state transitions,
   publication boundaries and the no-change path. Update memory rows deliberately
   when adding/removing scope; do not erase evidence or reset assessment dates.
3. Validate relative links and bindings, then use rig's normal PR checks and squash
   merge. No direct main push and no automatic policy edits by scheduled workers.
4. Each run resolves main once, fetches both policy files at that commit and records
   it. A merge during a run takes effect next run. An unavailable file blocks the
   review rather than falling back to a stale copy.
5. Changing a path, key, issue, agent or schedule is a manifest change in the same
   PR; after merge, run `uv run workspace orca automations --apply` on the personal
   machine and check the plan comes back `ok`.

## Evaluation and continual refinement

Use [evaluations.md](evaluations.md) for independent run scoring, sampling cadence,
evidence capture and the PR/canary/rollback loop. Copy the
[evaluation record](evaluation-record.md) and compare policy changes against the
[regression cases](regression-cases.md). Track evaluations and refinement status
in index #21; this is an operator workflow, not an additional cloud schedule.

## Memory migration

Create coverage, finding and continuation sections in the existing managed issue
block, preserving prior setup metadata, human text, report pointers and history.
Seed unassessed areas explicitly. Import source-inspected partial evidence with
its exact commit/ref and limitations; do not relabel it as a full/main assessment.
A newer PARTIAL report contributes useful memory even when no complete group
report exists. Carry stable finding IDs and the next queue across occurrences.

## Evidence boundary

The ChatGPT runs proved a worker can post a report and update the issue while
preserving outside text, labels and state. The Orca runs must show the same: the
first scheduled run of each group needs its report checked for the policy commit,
paths and run ID, and its issue description read back. Neither the bootstrap nor
a prompt is a hard guarantee against every future model repetition or service
failure.
