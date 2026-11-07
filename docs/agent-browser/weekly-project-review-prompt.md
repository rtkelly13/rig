# Weekly project review prompt — legacy full-baseline example

Active tasks now load [per-schedule policy files](../project-reviews/README.md)
and the [shared continuation contract](../project-reviews/contract.md) from main
on every run. The detailed format below is historical reference for a justified
broad baseline; it must not force every category to be re-audited on each run.

Use with the [ChatGPT Work task workflow](chatgpt-work-tasks.md). This is a
historical example, not a creation template or enabled schedule. New managed
tasks use the live bootstrap; retain this example only for a justified broad
baseline after applying the current policy and continuation rules.

## Task definition

```text
Title: <task title>
Cadence: <weekday and time> in <IANA timezone>, starting <first run>
Destination: <new chat per run OR existing chat identified in the UI>
Execution: OpenAI cloud, using <available environment and connected tools>
Projects: <explicit owner/repository list>
Analysis method: <existing script path/command or complete analysis instructions>
Task slug: <task-slug>
Report destination: <fixed-issue-url>
Publication: one full report comment per run; update the managed issue summary
```

## Durable run instructions

```text
## Standard weekly report and fixed GitHub issue (rig-v1)

Retain the project-specific analysis instructions above. Task slug: <task-slug>.
The fixed report issue is <fixed-issue-url>. Use that existing issue on every
run; the authorised GitHub writes for this reporting workflow are comments on
that issue and edits to its managed summary section. Code, roadmap, milestone,
new issue and PR changes are proposals in this report, not actions to execute.

Check paginated workflow-owned comments for a newer complete report than the
managed pointer, which may be stale after a failed body update. Use the latest
complete occurrence as the baseline, then compare its findings with current
source and GitHub evidence. Record the repository, branches and exact commits
actually inspected, including relevant stacked PR heads/bases. If Library
context or source is inaccessible, state that limitation. Never present a
proposal, unmerged PR, previous report claim or unexecuted check as verified
current behaviour. Retain the original schedule, execution mode and pause state.

Use this standard Markdown format, retaining every numbered section even when
there is no meaningful change:

# Weekly project review: <project> — <report date>
Run ID: <stable ID for this occurrence, reused on retries>
Review period: <start/end>; analysed at: <UTC timestamp>
Analysis status: COMPLETE / PARTIAL / FAILED
Source: <repository, branches and commit SHAs>; prior report: <comment URL or none>

## 1. Release readiness and changes
State READY / AT RISK / BLOCKED / UNKNOWN / NOT APPLICABLE for the project’s
declared milestone, separately from analysis completeness. Do not assume 0.1;
governance-only repositories may have no release milestone. Summarise findings, with
merged, open-PR, proposed, deferred and verified-on-candidate work distinguished.
Say plainly when there is no meaningful change; recheck prior claims.

## 2. CI, test effectiveness and measured quality
Provide a table: metric | current | previous | change | evidence/date.
Cover CI job/step durations, reliability/flakiness, executed test projects and
counts, line/branch coverage, static analysis/security, mutation/fuzz/property
checks, interop/AOT/package consumption and performance/resource measurements
where relevant. Missing evidence is UNKNOWN, never zero or success. Distinguish
test count from coverage and identify vacuous gates, stale evidence and absent
positive/negative controls. Recommend measurements where baselines are missing.

## 3. Security, hostile input and resource safety
Assess applicable limits, malformed inputs, arithmetic, buffer ownership,
cleanup and allocation/decompression guards; keep project-specific boundaries.

## 4. API, compatibility, architecture and quality of life
Cover public surface reduction/stability, protocol and dependency compatibility,
design-doc consistency, samples and usability. Identify documentation or
MILESTONE.md corrections as proposals with evidence, including no-change areas.

## 5. Roadmap, issues, PR stacks and blockers
Link the relevant GitHub issues/PRs and identify actual state, dependencies,
head/base branches, review blockers and what prevents a release candidate.

## 6. Prioritised agent-ready execution plan
For each high-value task give priority, evidence/problem, scoped change,
dependencies, acceptance criteria, validation including a meaningful failure
control, and expected risk/quality improvement. Explain the critical path and
which tasks can run independently. Prioritise demonstrated release risks over
speculative features or optimising already-cheap checks.

## 7. Evidence and limitations
List sources, checks actually executed versus inspected results, unavailable
tools/Library material, unknowns, stale measurements and incomplete analysis.

## 8. Publication outcome
Include the fixed issue URL, the report comment URL and whether comment and
summary publication were both verified. Report permission failures explicitly.

Derive the run ID before writing: scheduled runs use
<task-slug>:scheduled:<original intended occurrence date in UTC, YYYY-MM-DD>.
This assumes at most one scheduled occurrence per task per UTC date; if that
changes, include the original scheduled time too. Recover the original
occurrence from the triggering run/chat on retries, including across midnight
or week boundaries; never substitute the retry's current date. Manual runs use
<task-slug>:manual:<explicit request ID supplied once and retained on retry>.
If the original occurrence/request ID cannot be recovered, inspect the run
history and matching comments or ask for it before publishing; do not mint a
fresh ID. Every publication target for the same occurrence shares this run ID;
the project index is discovery context, not a shared review schedule.

Publish the full report as one dated comment on the fixed issue. Include the
marker <!-- rig-review-run:<task-slug>:<run-id> -->. Read existing comments,
including pagination, before retrying; reuse this run ID and update only the
matching workflow-owned comment rather than creating a duplicate. Do not use
an unrelated 'last comment' as the retry target. Preserve earlier runs and all
human comments. Do not create a replacement issue if this issue is unavailable.

After the comment exists, update only the section between
<!-- rig-review-summary:start --> and <!-- rig-review-summary:end --> in the
issue body. Keep all text outside those markers intact. Include the latest
attempt date/status, release readiness, key changes, top three next actions,
and links to the latest attempt and latest complete report. For PARTIAL/FAILED
analysis, retain the previous complete-report link. If markers are missing or
ambiguous, publish the comment and report the summary update as blocked.

Use the available GitHub tools or authenticated CLI, following normal access
and approval controls. Read back the posted comment and issue body before
claiming success. If only one write succeeds, report partial publication and
retain its URL for retry. If publication is blocked, include the full report in
the task response and provide a downloadable Markdown file where supported.
Never bypass approvals or include credentials or unrelated private account data.
```

The format is derived from the existing Diffra, Parquet and Arrow review prompts
and the observed Diffra/Parquet reports: measured evidence, explicit unknowns,
release-risk prioritisation, stacked PR state and concrete acceptance criteria.
Each schedule keeps its project-specific focus and receives the same output
contract with its own slug and issue URL substituted.
