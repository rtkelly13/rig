# Managed ChatGPT project groups

Manage native ChatGPT **Projects** as a consistent group, with independent
instructions, repository scope, memory and scheduled task for each managed
project. The target is [config/chatgpt-projects.json](../../config/chatgpt-projects.json).
It is desired state, not proof that a provider resource has been applied.
Analysis instructions now live in [per-schedule policies](../project-reviews/README.md)
on main; saved tasks/Project review contexts hold only a [bootstrap](../project-reviews/bootstrap.md).

The [review-group layout](five-review-groups.md) defines four active reviews
once per group each week, staggered across Monday/Tuesday/Thursday/Friday. Consult the linked issue index for the latest
applied audit; target configuration alone is not provider verification.

## Target layout

| Native ChatGPT Project | Policy | Sources | Memory |
| --- | --- | --- | --- |
| Data | Manage; rename the existing Source Generators identity | Parquet, Arrow, Iceberg, TypeProvider and the parquet-dotnet fork | [#22](https://github.com/rtkelly13/rig/issues/22) |
| Diffra (Artifact Driven Baselines) | Manage existing identity | rtkelly-labs/Diffra | [#12](https://github.com/rtkelly13/rig/issues/12) |
| Resultful | Retain Project/instructions/chats; independent task paused | Reviewed within Tooling & Design | [Historical #23](https://github.com/rtkelly13/rig/issues/23), reports [#25](https://github.com/rtkelly13/rig/issues/25) |
| Personal | Manage distinct project | Eight personal application/infrastructure repositories | [#24](https://github.com/rtkelly13/rig/issues/24) |
| c11n (configuration) | Preserve name, instructions, sources and tasks | Existing context | Unchanged |
| Design System | Manage tooling/design context; preserve identity, name, chats and sources | design-system, mermaid-toolkit, rig, workflow experiments, org governance and both Resultful repositories | [#25](https://github.com/rtkelly13/rig/issues/25) |

The [index #21](https://github.com/rtkelly13/rig/issues/21) links memories; it
has no shared review task. Rig is private. The full 155-repository access
inventory remains in gitignored local notes. Work/collaborator access is not
adoption scope. Archived the-vault-system is excluded from scheduled review.

The target uses four active weekly tasks at 02:00 Europe/London: Data Monday
(first 2 November 2026), Diffra Tuesday (3 November), Tooling & Design Thursday
(29 October) and Personal Friday (30 October). Each group runs every week, for
four sessions per week, at least 24 hours apart. Use `INTERVAL=1` and each group's
own `BYDAY`; retain its ID and continuation chat. One observed active-task slot
remains free. Model allowance remains shared with other usage.
Each run checks changes and critical blockers, then investigates the most useful
changed, unverified or overdue area. Quiet projects get a short no-change update,
not a repeated full audit. Separate projects/tasks still share plan allowance.

Resultful consolidation keeps its native Project, original instructions and chats,
its permanent #377 history, and its paused task record. The tooling policy includes both Resultful repositories and reads #377 as history;
the task and Project bootstrap fetch that policy every run.
New combined reports go only to #379. Establish a baseline when unassessed, then
deepen Resultful on material changes or when fifteen calendar weeks have elapsed
since the last deep assessment, recording last assessment and next due review in #379.
The earlier five-week/five-task setup and publication audits below are historical evidence;
the JSON and layout above define the current four-task target.

### Historical Resultful consolidation — 2026-10-27

The independent Resultful task was paused without deletion. A fresh Scheduled
view showed four active and four paused task records. The combined tooling
Project settings were saved, reopened and compared exactly with the rendered
eight-repository context. A fresh saved-task editor independently confirmed all
eight repositories, #377 historical reads/#379 publication, the baseline and
three-occurrence policy, the original report/retry contract and generic marker,
and absence of a temporary test ID. The other three active schedules retain
their original five-week starts. No review run was triggered for this scope-only
change; Resultful's source quality remains unassessed until its combined review.

## Plan → apply → audit

### Plan

1. Read the target JSON, common [context template](project-context-template.md),
   all referenced schedule policies and [report contract](../project-reviews/contract.md).
2. Inventory native Projects, instructions and task associations through the
   signed-in UI. Match stable observed identities before names, since names can
   change. Inspect the complete project list where available; state any coverage
   limit instead of calling the sidebar exhaustive.
3. Save observed state, capture timestamp and field provenance under
   `temp/chatgpt-work-tasks/`. Keep instructions, raw account snapshots and
   cookies out of public outputs. Do not export credentials.
4. Produce a field-level diff: create, rename, replace managed instruction block,
   create/reconcile task, preserve, different or unverified. Include the exact
   repository allowlist, memory issue, recurrence, timezone and destination.
5. Mark unknowns explicitly. An unknown task association is not a missing task;
   inspect it before proposing creation. Never infer that the Platform Projects
   API or Agents API automations expose this ChatGPT account.

In the original setup, the native rename plan preserved Source Generators' original
identity as Data. The empty Data project created during setup was repurposed as
Personal. Resultful is new. Diffra, c11n and Design System keep their identities.
No project deletion, chat movement or source removal is part of the plan.

### Historical monthly four-group audit, 2026-10-27

The four managed instruction copies were read back exactly in native Project
settings. c11n/Design System names, instructions and memory/Library settings were
preserved. All four saved task prompts were inspected in the task editors for
their repository allowlists, memory issues and report markers. Native Automations
data confirmed four enabled monthly tasks matching the target and all three
legacy tasks paused. Their conversation IDs were independently found in the
matching native Project chat lists. The provider's direct project-binding and
next-run fields were unavailable; the conversation/UI join is the association
evidence. No deep review was triggered during this setup.

The native scheduler rejected the first Personal create attempt because this
account had five active tasks. After the specified legacy replacements were
verified and the two old weekly tasks paused, Personal was created successfully.
Treat five as an observed account cap, not a universal plan guarantee. It is
separate from the shared five-hour/weekly usage allowance.

### Historical five-group reconciliation, 2026-10-27

All five managed contexts were reloaded and compared exactly with the rendered
template; c11n remained unchanged. All six retained Default memory and Enabled
Library access. Saving Design System's new instructions normalised an existing
trailing space in its name; its visible name and identity remain Design System.
The four existing task IDs were updated to the staggered five-week cadence and
read back through native Automations tools. Personal's saved task prompt now has
eight repositories, with tooling removed.

The fifth task was restored in place to its permanent five-week recurrence with
the original conversation binding and durable six-repository prompt. Fresh
Scheduled UI independently showed five enabled reviews and all three legacy
reviews paused. The provider still omits direct project-binding fields; the
conversation is listed inside the retained Design System Project. The list's
short cadence label displayed “Daily” for these custom schedules, so verify the
full saved RRULE and DTSTART rather than treating that label as the recurrence.

Publication verification exposed a useful distinction: the first one-time run
returned `no_update`, null result and unknown delivery; a later native Run now
performed a partial review but correctly withheld publication because no manual
request ID had been supplied. For a manual verification, supply a fixed operator
request ID in the saved prompt before triggering Run now. Preserve that key on
retries; remove only that temporary verification block afterward. Wait for the
administrative chat to finish before triggering a run in the same conversation.
Check the actual report/description writes; a completed review in Scheduled does
not prove GitHub publication. The memory index records the publication outcome.

The developer browser later encountered a challenge that a manual click did not
resolve. Disconnecting agent-browser and opening the same dedicated profile as
an ordinary browser let the user sign in. Native CUA controls then completed the
schedule reconciliation. Keep the explicit developer profile, do not attach to
daily Chrome, and let the user complete challenges/sign-in. No credential export
or broad cookie clearing is needed for this fallback.

### Publication outcome, 2026-10-27

Native Run now executed the saved fifth task with the explicit verification ID
`tooling-design:publication-verification:2026-10-27-v2`. Its unattended worker
published [one partial review comment](https://github.com/rtkelly13/rig/issues/25#issuecomment-6447519232)
and updated the marked issue description. Independent GitHub readback verified
one matching report marker, the description's attempt link, unchanged text
outside the markers, unchanged labels and open state. The latest-complete group
report remains none: this was a partial evidence review, not an assessment of
all six repositories. It executed no source tests and made no source changes.
This verifies the manually triggered scheduled worker's publication path; it
does not establish delivery of every future calendar occurrence or every group.

The temporary test block was removed after verification. Native readback
confirmed the test request ID is absent, while the durable context, six-repository
scope, generic marker, enabled state, conversation and recurrence remain intact. Keep the report
and its occurrence ID in history; future runs use their own original occurrence
keys. Do not replace the report with a setup audit or relabel partial analysis
as a complete review.

### Apply

Apply only the named plan. Recheck the observed base before each mutation and
stop on concurrent edits. In order:

1. Reuse/rename/create native project identities, preserving existing chats and
   sources. Do not recreate a project merely to rename it.
2. Render the stable bootstrap with that group's key, policy path and fixed
   memory issue. Fetch repository scope/focus from main; do not inline a second copy. Preserve human text outside the markers. Reject duplicate or
   missing ambiguous markers rather than overwriting unrelated instructions.
3. Start or identify the dedicated review chat **inside** that native Project.
   Test available tools and use the current project's instruction context.
4. Create/reconcile one task targeting that review chat, with the target title,
   recurrence, timezone and enabled state. A scheduling-only request must say
   not to run the analysis immediately.
5. Read back the native task and its project/chat association. Only then retire
   duplicate legacy tasks as specified by the target. Keep Arrow paused until
   its existing approval and migration are deliberately resolved.

Record each attempted operation and result in a local journal. If a create
response times out, re-inventory by observed identity/title before retrying.
Do not guess provider IDs or duplicate a resource because its response was lost.
No pruning of unlisted projects/tasks is authorised by this target.

The managed group task may review several related repositories, but it must not
adopt another group merely because GitHub tools can see it. Keep c11n in the preserve set throughout reconciliation. Design System is
the retained identity for the active tooling group: change only its managed instruction block and task,
preserving its name, identity, chats and sources.

### Audit

Use fresh observations after reload, independently of action responses.
For each target field report **verified**, **different** or **unverified**:

- Stable project identity and saved name.
- Exact saved bootstrap key/paths/fixed issue; fetched main policy commit,
  repository scope and shared-contract reads.
- Preserved c11n instructions and name; preserved all existing project identities,
  chats, source context and memory settings, including the adopted Design System.
- Task identity/title, full prompt, recurrence, timezone, enabled state, next run,
  and destination chat **and project**.
- Open permanent memory issues and the marked active description.
- Latest-attempt/complete pointers and matching workflow report comments.
- Legacy task disposition and any unretired duplicates.

A successful Save click or the cloud model's prose is not the audit. A current
sidebar name alone does not verify project instructions or scheduled-task
inheritance. Do not set an unobserved field to an assumed success/default.
Keep a readable plan/apply/audit report in `temp/` and put a concise current
setup summary, unresolved differences and next actions in the rig index.

## UI transport and API boundaries

[OpenAI Projects](https://learn.chatgpt.com/docs/projects) documents native
project chats, sources and instructions.
[Scheduled tasks](https://learn.chatgpt.com/docs/automations) documents tasks
returning to a chat. The public Platform `/organization/projects` API manages
API-platform resources, not these ChatGPT Projects. No supported public API
for this account's native project/task state was established in this workflow.

The signed-in UI exposed a project-pins GET and project create/update requests.
An independent same-origin, cookie-only GET to the observed pins endpoint
returned **401**. That is a failed API verification route, not proof the UI
cannot read state. Do not extract bearer tokens or call guessed private routes.
Use fresh UI/DOM observations as the current adapter. An undocumented web
endpoint is not a supported long-term API contract.

The installed 0.31.1 browser CLI sometimes reported click success before the UI
changed. Radix project menus responded to a pointer-down on the observed button;
project forms were submitted through their visible form's `requestSubmit()`.
These are UI actions, not direct backend mutations. Inspect the resulting DOM
and wait for visible state before reusing refs. A hidden/mounted dialog should
not be mistaken for an open dialog. Reopen saved settings after reload.

## Active memory and history

The issue description is the working memory: current milestone/source baseline,
findings, assessment dates, next actions/acceptance criteria, pending decisions,
and separate latest-attempt and latest-complete report links. Dated report
comments hold evidence and history. Check paginated workflow-owned comments for
a newer complete occurrence if the description pointer is stale after a failed
body update. A random last comment may be a human note, test or adoption event.

Initial memory events explicitly record metadata/adoption only. They are not
completed deep reviews and do not establish a current quality baseline.
Update only areas actually reassessed; “not inspected” does not mean unchanged.

Use the deterministic run/request key in the report contract. Retries recover
the original occurrence across date boundaries; if it cannot be recovered, stop
before publishing and request it. Preserve report history and human notes
outside managed markers. Comment success plus body failure is partial publication,
not grounds to duplicate the report or discard the previous complete pointer.

Repository-level trackers remain available as detailed memories:

| Repository | Permanent issue |
| --- | --- |
| `rtkelly-labs/.github` | [#15](https://github.com/rtkelly13/rig/issues/15) |
| `rtkelly-labs/Arrow.SourceGenerator` | [#14](https://github.com/rtkelly13/rig/issues/14) |
| `rtkelly-labs/design-system` | [#16](https://github.com/rtkelly13/rig/issues/16) |
| `rtkelly-labs/Diffra` | [#12](https://github.com/rtkelly13/rig/issues/12) |
| `rtkelly-labs/Lake.NET` | [#17](https://github.com/rtkelly13/rig/issues/17) |
| `rtkelly-labs/mermaid-toolkit` | [#18](https://github.com/rtkelly13/rig/issues/18) |
| `rtkelly-labs/parquet-dotnet` | [#19](https://github.com/rtkelly13/rig/issues/19) |
| `rtkelly-labs/Parquet.SourceGenerator` | [#13](https://github.com/rtkelly13/rig/issues/13) |
| `rtkelly-labs/Parquet.TypeProvider` | [#20](https://github.com/rtkelly13/rig/issues/20) |
