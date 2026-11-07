# Review policies on main

These files are the editable instructions for the four native ChatGPT Work review
tasks. Codex maintains them through rig PRs; after squash merge the next task run
fetches the updated main policy. A task does not automatically refresh an attached
file or its chat history: the [saved bootstrap](bootstrap.md) explicitly fetches
current GitHub content every time.

| Schedule | Policy | Canonical memory |
| --- | --- | --- |
| Data | [data.md](data.md) | [#22](https://github.com/rtkelly13/rig/issues/22) |
| Diffra | [diffra.md](diffra.md) | [#12](https://github.com/rtkelly13/rig/issues/12) |
| Developer Tooling & Design, including Resultful | [tooling-design.md](tooling-design.md) | [#25](https://github.com/rtkelly13/rig/issues/25) |
| Personal | [personal.md](personal.md) | [#24](https://github.com/rtkelly13/rig/issues/24) |

Each group runs once every week at 02:00 Europe/London: Data Monday, Diffra
Tuesday, Tooling & Design Thursday, Personal Friday. Exact starts and RRULEs live
in the manifest; see the [weekly layout](../agent-browser/five-review-groups.md).
The native scheduler must be reconciled when timing changes; policy edits alone
do not reschedule a task.

Each policy imports [contract.md](contract.md). The contract defines continuation,
coverage, finding identities, delta selection and publication. Policy text lives
here; changing analysis state lives in the issue description and dated reports.
The [binding manifest](../../config/chatgpt-projects.json) holds identities,
frequency, fixed write destination and policy path; repository allowlists/focus
live only in the schedule files. Resultful/c11n context and paused task history
remain retained as documented in the [layout](../agent-browser/five-review-groups.md).

## Editing and rollout

1. Change the appropriate policy, or the shared contract for a common behaviour.
   Keep the schedule key and canonical issue compatible with the saved bootstrap.
2. Review changed repository scope, revisit triggers, finding-state transitions,
   publication boundaries and the no-change path. Update memory rows deliberately
   when adding/removing scope; do not erase evidence or reset assessment dates.
3. Validate relative links and bindings, then use rig's normal PR checks and squash
   merge. No direct main push and no automatic policy edits by scheduled workers.
4. Each run resolves main once, fetches both policy files at that commit and records
   it. A merge during a run takes effect next run. An unavailable file/connection
   blocks the review rather than falling back to a stale copy.
5. When changing a path, key, fixed issue, native cadence or destination, reconcile
   saved task/Project bootstraps and verify readback. Policy edits alone do not
   change those bindings. Keep the independent Resultful task paused.

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

The previous native Run now publication test proved a worker could post a report
and update the issue while preserving outside text/labels/state. A live-policy
smoke test must additionally demonstrate authenticated reads from main and report
the fetched policy commit/paths. The first calendar runs still need operational
monitoring; neither the bootstrap nor a prompt is a hard guarantee against every
future model repetition or service failure.

[OpenAI scheduled-task documentation](https://learn.chatgpt.com/docs/automations)
explains that web tasks can use connected tools and accessible source material.
Fetching versioned policy files on each run is this workflow's explicit convention;
it is not an assumption that an attachment or saved prompt refreshes itself.
