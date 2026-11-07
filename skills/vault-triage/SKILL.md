---
name: vault-triage
description: Turn captured links in the ledger into curated vault notes - deciding keep, hub or drop, and writing notes that follow the vault's conventions. Use when promoting captures, filing resources, creating hub notes, or asked to work the backlog.
---

> **Where to run these.** Vault scripts live in `~/code/personal/obsidian-vault`; `cd` there first, or use
> the absolute paths below. This skill is installed globally by `devenv`, so it is
> available from any repo — which means the cwd is not guaranteed to be the vault.

# Triage

Capture was never the bottleneck. **Judgement is.** This skill is about deciding, and
about not faking the decision when it hasn't been made.

## Start by refreshing the signals

Triage begins with two commands, not with reading links:

```bash
python3 ~/code/personal/obsidian-vault/scripts/okf-sync.py      # refresh repo indexes + data/starred.tsv
python3 ~/code/personal/obsidian-vault/scripts/triage-rank.py --apply
```

`triage-rank` marks every ledger row that is **also starred on GitHub**. A link dropped
in Slack *and* starred carries two independent signals of intent.

**A starred link is never a drop.** On the first backfill this caught four links marked
`drop` for being "outside your domains" that had in fact been starred — the star is
evidence of a decision already made, and it outranks a guess about relevance. Where a
star contradicts a verdict, the star wins and the note says so.

A starred link sitting at `hub` is a promotion candidate worth a second look, not an
automatic keep.

## Verdicts

Record every ledger row in `data/slack_backfill_triage.tsv` as
`url<TAB>cluster<TAB>verdict<TAB>destination`:

- `keep` — earns its own note
- `hub` — a line in some hub's `## Watchlist`, not a note of its own
- `drop` — with a reason in the destination column

**Record the drops.** The reason is the one thing no tool can reconstruct, and it stops
a rejected link resurfacing next quarter. A ledger of only the keeps is half a memory.

## The rule that matters

Write prose only where the content is actually known. If the page hasn't been read, set
`status: unread` and leave the slots empty.

The retired pipeline pasted raw crawl output under `## Summary` — mermaid source and all.
Those 70 notes are the worst in the vault, identifiable by `staging_id:` in frontmatter,
and each still needs a human pass. **An empty slot is honest; a scraped one is debt.**
Offer to fetch and read a page rather than inventing a summary for it.

## Tiering social captures

A social URL is rarely the resource. Classify before filing, because the tier decides
*what gets the note*:

| Tier | Signal | What becomes the note |
|---|---|---|
| **T1 — essay** | `is_note_tweet`, or text > 280 chars with no outbound url facet | The tweet. It is the content. |
| **T2 — pointer** | ≥1 facet with `type: "url"` | **The target**, not the tweet. The tweet is provenance — record it in `source_tweet`. |
| **T3 — visual** | media facets only, short text | Nothing. A line in a hub `## Watchlist` — design inspiration is browsed, not read. |
| **T0 — deleted** | resolver returns 404 | Nothing. Record as `deleted` so it is not re-fetched. |

A single T2 tweet often carries **several** targets — a thread of icon sites, a bundle of
learning repos. Each target is its own ledger row; the tweet appears once as provenance.
Dedupe targets against the vault *and* against each other before filing, since the same
repo gets tweeted repeatedly.

Typical split on real data: roughly 40% T1, 35% T2, 25% T3.

## Grouping

When several links were captured in one sitting against one question, they are **one hub
note**, not N separate notes. Seven data-table links dropped within an hour on 2026-08-14
are a single research question, and that grouping is itself the judgement worth recording.
Look at capture timestamps before filing.

## Taxonomy

Notes live under `Vault/Resources/<Category>/`. Where a category exists at both
`Resources/X` and `Resources/Engineering/X`, **follow the note counts, not the names** —
several top-level shells (`Resources/AI`, `Resources/Architecture`) are empty and the
real content is nested.

| Cluster | Destination |
|---|---|
| Agents, MCP, skills, agent memory | `Resources/Engineering/AI/Agents/` |
| ML/LLM internals, models | `Resources/Engineering/AI/` |
| Storage engines, indexing, MVCC | `Resources/Engineering/Databases/` |
| Queueing, consensus, scaling | `Resources/Engineering/Distributed-Systems/` |
| Authorization, system design | `Resources/Engineering/Architecture/` |
| Practice, refactoring, process | `Resources/Engineering/Engineering-Process/` |
| .NET, NuGet, F# | `Resources/Engineering/DotNet/` |
| UI, typography, design systems | `Resources/Design/` |
| CLIs, editors, dev utilities | `Resources/Tools/` |
| Management, communication | `Resources/Leadership/` |
| Seniority, role progression | `Resources/Career/` |

The taxonomy moves. Captures drifted heavily toward design through 2026 and earned a new
`Resources/Design` category — propose a new one when a cluster outgrows its home rather
than forcing links into a folder that no longer fits.

## Note shapes

```markdown
---
type: resource
status: active        # `unread` when the page has not been read
tags:
- engineering
source: <url>
created: YYYY-MM-DD
updated: YYYY-MM-DD
curation_source: slack_backfill
---

# Title

## Why this matters
- 

## Key points
- 

## Links
- <url>
```

Hub note — `type: resource-hub`, with `## Subtopics`, `## Keepers` (wikilinks) and
`## Watchlist` (bare URLs, unassessed). Wikilinks are full paths from the vault root:
`[[Resources/Design/Data-Tables]]`.

## Mechanics

Create notes with **absolute paths**. A relative `../` from inside `Resources/` resolves
to the vault root and silently creates a stray top-level folder — the same path-join bug
that produced `Resources/EngineeringEngineering`.

Run `vault-health` after any batch promotion.
