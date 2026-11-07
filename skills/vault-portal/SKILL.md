---
name: vault-portal
description: Treat the vault as a documentation portal - OKF concept notes, repository entries, starred-repo indexes, and agent-vs-human provenance. Use when adding documentation for a repo, refreshing the repo indexes, attributing a note, or asked how the vault is structured as a portal.
---

> **Where to run these.** Vault scripts live in `~/code/personal/obsidian-vault`; `cd` there first, or use
> the absolute paths below. This skill is installed globally by `devenv`, so it is
> available from any repo — which means the cwd is not guaranteed to be the vault.

# The vault as a documentation portal

The vault follows the **Open Knowledge Format** (OKF v0.2, Google Cloud) — a bundle is a
directory tree of markdown files with YAML frontmatter, cross-linked into a graph agents
can read. That is what an Obsidian vault already is, so adoption is additive: no files
moved, no schema registry, no required tooling.

Spec: https://github.com/GoogleCloudPlatform/knowledge-catalog/blob/main/okf/SPEC.md

## Provenance — the part that matters most

Every note says who wrote it, using OKF's actor convention:

```yaml
generated:
  by: human:rtkelly13          # a person
  by: claude-opus-5/1m         # an agent — <producer>/<version>
  by: process:bookmark-import  # automation
```

**Absent means unattributed, never "human".** 917 notes are legitimately unattributed —
created in 2026 with no evidence either way — and guessing would make the field worthless.
When you touch such a note, set it to what you actually know.

Add `verified:` when a person has checked agent-written content:

```yaml
verified:
  by: human:rtkelly13
  at: 2026-09-11T12:00:00Z
```

That is the signal that matters for a portal: not "an agent wrote this" but "an agent
wrote this **and nobody has checked it**".

Backfill or re-check attribution:

```bash
python3 ~/code/personal/obsidian-vault/scripts/okf-provenance.py            # dry run, shows the tally
python3 ~/code/personal/obsidian-vault/scripts/okf-provenance.py --apply
```

## OKF frontmatter

`type` is the only field OKF requires. The vault's existing values (`resource`,
`resource-hub`, `note`, `project`, `area`) are already valid — OKF keeps no central
registry of type names. Generated bundles add `Repository` and `Reference`.

| Field | Use |
|---|---|
| `type` | **required** — the kind of concept |
| `title`, `description` | display name, one-sentence summary |
| `resource` | URI of the thing the note is about |
| `tags` | list of strings |
| `sources` | where the content came from: `- resource:` plus optional `title`, `author` |
| `generated` | `{ by, at }` — see above |
| `verified` | `{ by, at }` — a person checked it |
| `status` | `draft` / `stable` / `deprecated` |
| `stale_after` | ISO date after which content should be distrusted |

`index.md` and `log.md` are reserved names: a directory listing and a dated update
history. `okf_version: 0.2` goes in a bundle root's `index.md` only.

## Repository entries

Every repository has an entry under `Vault/Projects/Repos/`, and starred repositories are
indexed by language under `Vault/Resources/Starred/`.

```bash
python3 ~/code/personal/obsidian-vault/scripts/okf-sync.py --dry-run
python3 ~/code/personal/obsidian-vault/scripts/okf-sync.py
```

Run as the first step of a triage pass, not on a schedule — see `vault-triage`. The sync
also writes `data/starred.tsv`, which `scripts/triage-rank.py` cross-references to rank
captures. Refreshing indexes and triaging captures are the same activity, so they happen
together rather than drifting apart on separate cadences.

Uses your local `gh` credentials. Nothing scheduled, no stored token.

**The sync owns the frontmatter and the block between `<!-- okf:begin -->` and
`<!-- okf:end -->`. Everything else in the file is yours and survives every re-run.**
To document a repo, write below the end marker:

```markdown
<!-- okf:end -->

## Why this exists

The bit that isn't in the README...
```

If a repo needs more than a section, make it a real note elsewhere and link to it from
below the marker — don't fight the generator for ownership of the block.

## Decisions

`docs/adr/` holds the vault's architectural ledger — 10 records covering why curation is a
skill rather than a package, why the ledger is a flat file, why provenance uses OKF's
field, and why generated content is fenced. Append-only and immutable: supersede, never
edit. Format follows `shared-utilities` ADR 0001.

If you are about to change a vault convention, check whether an ADR already settled it.

## Starred repositories

All 302 are triaged, clustered by purpose rather than language — `fsharp`, `web`, `dotnet`,
`agents`, `design`, `infra`, `tooling`, `awesome`, `languages`, `databases`, and the rest.
Clustering comes from topics and description, falling back to language.

**Star counts are stored as orders of magnitude** (`s0`–`s5`, rendered `<10` … `100k+`), not
exact numbers — see ADR 0011. A tier only moves when a repo crosses a power of ten, and when
it does, the crossing is written to `Vault/Resources/Starred/log.md` and surfaced in the
bundle index. The sync therefore produces a trend report rather than a wall of churn.

Durable metadata lives in `data/starred.tsv`: cluster, tier, language, licence, state
(`active` / `stale` / `archived`), creation date, topics. All of it is rendered into the hubs
as plain text so Obsidian's search finds a repo by topic or licence, not just by name.

Verdicts live in `data/starred_triage.tsv`. A star is already the keep decision, so triage
clusters for findability and flags the dead: `archived` upstream, or `stale` with few stars,
becomes `drop`. **A human verdict in that file is never overwritten by a re-sync.**

## What belongs here

A documentation portal indexes; it does not duplicate. Docs living in a code repo stay
there and get **linked** — they cross-reference their repo's ADRs and issue numbers, so a
copy in the vault drifts within a week. See `Resources/Engineering/Repo Docs Index`.

The test for pulling something in: does it answer a question you would ask again on a
different project? Evaluations, comparisons, benchmarks and technique guides qualify. CI,
hosting and deployment mechanics stay in their repo.
