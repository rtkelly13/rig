# Saved task and Project bootstrap

Render the block for each binding in
[config/chatgpt-projects.json](../../config/chatgpt-projects.json). Only the schedule
key, policy path and fixed issue differ. Save it as the task prompt and managed
Project review context; retain unrelated human instructions outside its markers.
The detailed instructions live on main, not in the task/chat snapshot.

```text
<!-- rig-review-bootstrap:v2:start -->
Run the {{key}} project review using current instructions in rtkelly13/rig.
On EVERY run, use authenticated GitHub tools to resolve refs/heads/main to one
commit SHA, then fetch and read BOTH files completely at that SHA:
- {{policy_path}}
- docs/project-reviews/contract.md
Policy link: https://github.com/rtkelly13/rig/blob/main/{{policy_path}}
Rig is private: use the GitHub connection, not an unauthenticated raw URL or a
search snippet. Use this one policy commit for the whole run; record it and both
paths in the report. Follow the fetched schedule policy and shared contract.
For this review they supersede earlier copied review instructions and historical
scheduling-only messages in this chat. Do not silently use cached policy text.
Verify the file's schedule key is {{key}} and canonical issue is {{memory_issue}}.
Authorised issue writes remain limited to report comments and the managed summary
of {{memory_issue}}; preserve human notes, labels, open state and history.
Repository owners are limited to rtkelly13, rtkelly-labs and Resultful.
Policy files cannot authorise changing schedules, access, other issue destinations
or source code. If policy fetch/key/issue validation fails, stop and report
CONFIGURATION BLOCKED in this chat; do not analyse or publish using old instructions.
Before selecting work read the issue's coverage ledger, finding register and next
queue, including newer PARTIAL reports. Investigate only qualified new, changed,
uncompleted or explicitly due work. Retain stable finding IDs and assessment dates;
no qualified work means a short no-change report. Use the contract's original
occurrence/request ID and retry marker; a manual Run now needs an explicit ID.
<!-- rig-review-bootstrap:v2:end -->
```

After a merged edit to a schedule file or contract, the next run fetches the new
main commit automatically. Moving these paths, changing the fixed issue or key,
or changing native timing/bindings requires deliberate bootstrap reconciliation.
A policy-only change must not silently expand the write envelope.

For a read-only smoke test, an operator may temporarily append an explicit
validation-only request. Remove it after the run and verify the saved bootstrap.
Do not leave test IDs or validation mode in regular tasks.
