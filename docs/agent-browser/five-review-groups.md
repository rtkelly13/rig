# Project review groups

The current target in [`config/chatgpt-projects.json`](../../config/chatgpt-projects.json)
has four active review groups. Resultful is covered by Developer Tooling & Design;
its native Project, instructions, chats, historical memory and paused task record
remain available. [Memory index #21](https://github.com/rtkelly13/rig/issues/21)
records the applied audit.

| Group | Repositories | Review emphasis | Canonical memory |
| --- | --- | --- | --- |
| Data | Parquet.SourceGenerator, Arrow.SourceGenerator, Lake.NET, Parquet.TypeProvider, parquet-dotnet | Format correctness, interoperability, generated APIs, resource safety and releases | [#22](https://github.com/rtkelly13/rig/issues/22) |
| Diffra | Diffra | Evidence/baseline protocol, architecture, integration and releases | [#12](https://github.com/rtkelly13/rig/issues/12) |
| Developer Tooling & Design | design-system, mermaid-toolkit, rig, github-agentic-workflow-experimentation, rtkelly-labs/.github, Resultful/Resultful, Resultful/FluentAssertions.Resultful | Design/accessibility, tooling/workflows, governance, Resultful APIs/compatibility and packaging | [#25](https://github.com/rtkelly13/rig/issues/25) |
| Personal apps and infrastructure | blog, docs, hearth, obsidian-vault, ynab-budget-companion, ProceduralGeneration3DMazes, MySQLEFConcurrencyBug, AWSBatch-MapReduce | Software/configuration, maintainability, CI, deployment and practical next actions | [#24](https://github.com/rtkelly13/rig/issues/24) |

Unqualified data/design/mermaid repositories are under rtkelly-labs; remaining
personal/tooling names are under rtkelly13. All 22 active owned repositories are
covered exactly once. Archived the-vault-system and work/collaborator repositories
remain excluded. c11n is retained without adding a managed review task.

## One review per group each week

Each of the four groups runs **weekly**, staggered across Monday, Tuesday,
Thursday and Friday at 02:00 Europe/London. This replaces the historical
five-week rotation; there is no quiet-week slot or five-week wait per group.

| Group | Weekly day | First run under this cadence |
| --- | --- | --- |
| Data | Monday | 2 November 2026, 02:00 |
| Diffra | Tuesday | 3 November 2026, 02:00 |
| Developer Tooling & Design, including Resultful | Thursday | 29 October 2026, 02:00 |
| Personal apps and infrastructure | Friday | 30 October 2026, 02:00 |

Each task uses `FREQ=WEEKLY;INTERVAL=1`, its own `BYDAY` (MO/TU/TH/FR), and
`BYHOUR=2;BYMINUTE=0;BYSECOND=0`. Preserve its identity, continuation chat, prompt
and fixed issue. Resultful and the three legacy tasks stay paused. There are
four review sessions each week, one per group, at least 24 hours apart. This
uses four of the account's observed five active task slots and leaves one free.
The five-hour and weekly model allowance remain shared with other usage;
spacing runs does not reserve allowance or guarantee completion.

## Resultful within the tooling group

Read retained [Resultful memory #23](https://github.com/rtkelly13/rig/issues/23)
before assessing its two repositories; publish new combined reports only to #25.
Keep #23 open and permanent as historical memory and a forwarding pointer.
Do not create a new task or delete its native Project/history.

Establish a Resultful baseline when unassessed. Thereafter deepen it on material
changes, or when fifteen calendar weeks have elapsed since its last deep
assessment. Increasing the group frequency must not shorten that deeper-review
interval. The initial unassessed baseline is eligible on the first Tooling run.
Track last assessment and next due review in #25. Focus on result semantics,
public API compatibility, packaging and FluentAssertions integration. Quiet
periods need no repeated full audit; uninspected areas keep their old freshness.

## Applied publication verification

Before consolidation, the tooling task's native Run now worker published a
[partial report](https://github.com/rtkelly13/rig/issues/25#issuecomment-6447519232)
and updated #25's marked description. Independent readback confirmed one marker,
preserved outside text, labels and open state. The temporary request-ID block
was removed afterward. This proves the tested publication path; the report is
not a complete assessment of the expanded group or a guarantee of future delivery.
See the [plan/apply/audit guide](project-review-rotation.md) for reconciliation.
