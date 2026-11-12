# Governing `AGENTS.md` and the docs tree

**Parent:** [`repo-governance/`](../repo-governance/README.md) ·
**Rules:** [`agent-consistency.md`](./agent-consistency.md) ·
**Status:** deterministic half implemented — `repo-governance agents-audit`

`align_repo_conventions` already writes to `AGENTS.md`. It appends its expected conventions
block when a marker substring is missing, which means it can detect **absence** and not
**divergence** — and a repo that rewrote the section gets a second, contradictory copy appended.
This is a proposal to replace that with a real audit, and to be honest about which of the
`agent-consistency.md` rules a machine can decide and which it cannot.

## 1. The estate today

Measured 9 Sep 2026, root `AGENTS.md` per repo:

| repo | lines | bytes | |
|---|---|---|---|
| `design-system` | 834 → **74** | 50,750 → **4,847** | split into 8 topic docs, this session |
| `ProceduralGeneration3DMazes` | 307 | 13,266 | next-largest; unreviewed |
| `shared-utilities` | 177 | 7,966 | sync source for 3 repos |
| `the-vault-system` | 174 | 7,539 | synced |
| `the-vault-system-rewrite` | 108 | 5,381 | synced |
| `blog` | 102 | 6,085 | |
| 11 others | 12–40 | ~1–2 KB | |

Two things fall out. The median is ~18 lines, so the outliers are genuinely outliers rather than
a house style. And `CLAUDE.md` follows three different conventions across five repos:
`the-vault-system` and `the-vault-system-rewrite` symlink it, `blog` and `shared-utilities` keep
a small pointer copy, `design-system` has none.

## 2. What a machine can decide

Eight checks, all config-driven per § *Policy lives in config, not in Python*. Each one either
passes or names the line that failed.

| Check | Rule it enforces | Failure mode it catches |
|---|---|---|
| **Root size budget** | instruction budget | 834-line root files. Config: `agents_md.max_lines` per repo, default 120 |
| **Pointer integrity** | progressive disclosure | Every relative link in `AGENTS.md` and `docs/**/*.md` resolves. Progressive disclosure is worthless if the pointers rot, and this is the one check that makes the pattern safe to adopt |
| **No duplicate headings** | contradictory instructions | Two `## 🛑 Repository Conventions` sections. Would have caught the drift above the day it happened |
| **Conventions block equality** | — | Compare the governance-owned block to what is there, rather than testing for a substring. Report a diff; never append a second copy |
| **`CLAUDE.md` shape** | single source of truth | Must be **absent**. Claude Code reads `AGENTS.md` natively from v2.1.277, and when both exist the default mode prefers `CLAUDE.md` — so any copy, pointer stub or symlink shadows `AGENTS.md` rather than supplementing it |
| **No bespoke agent file** | single source of truth | No per-tool instruction file — `.github/copilot-instructions.md`, `.github/instructions/`, `.cursorrules`, `GEMINI.md`, `.clinerules`, `.windsurfrules` and the rest. Each tool reads its own file *instead of* `AGENTS.md`, so the failure is identical to a `CLAUDE.md` copy. The list lives in [`BespokeAgentFiles.cs`](../../src/Rig.Core/Governance/BespokeAgentFiles.cs) and is read by both the audit and the `.gitignore` writer, so the two cannot drift. Tool *feature* definitions (`.github/agents/`, `.github/prompts/`, `.claude/skills/`) are deliberately excluded. Config: `agents_md_hygiene.bespoke_agent_files`, empty to disable |
| **Path liveness** | staleness poison | Path-shaped strings that no longer exist. Requires a directory separator to qualify, so bare suffixes like `.test.ts` are not treated as paths — a naive version of this check produced 10 false positives out of 14 |
| **Required root content** | essential root content | A one-line description, a package-manager statement, and a commands section are present |
| **No generation markers** | no auto-generation | `<!-- generated`, `Auto-generated`, or an `init`-tool banner |
| **Docs index** (`docs-index`, warn) | progressive disclosure, second hop | Past `agents_md.docs_index_min_files` (default 5) markdown files, `docs/README.md` (or a docs-site `docs/index.md`) must exist and link each top-level doc and folder. A link beneath a folder with no index counts for that folder |
| **Folder index** (`folder-index`, warn) | progressive disclosure, third hop | Every file under `docs/` is linked from an index on its path: its folder's `README.md`/`index.md`, or any index above it. A folder needs its own index only when something in it is linked from none of them |

## 3. What a machine cannot decide

These are the rules that matter most and none of them is checkable. Attempting to gate them
produces a check that is either trivially passed or constantly wrong:

- Whether an instruction belongs in root or in a topic doc.
- Whether an instruction is vague, obvious, or redundant with what the model already knows.
- Whether two instructions contradict each other semantically.
- Whether the tone is overly prescriptive.
- Whether an attached screenshot is the *right* screenshot — the reason
  `design-system`'s evidence rule is a convention rather than a gate.

For these, governance should **produce a report and let an agent act on it**, not fail a build.
Concretely: `repo-governance agents-audit --json` emits per-repo findings, and a
`/agents-audit` command feeds that to an agent with `agent-consistency.md` as the rubric. The
deterministic checks gate; the judgement calls arrive as a worklist.

There is a middle tier worth reporting without gating, because the numbers are cheap and the
threshold is a judgement:

- **Estimated instruction count** — numbered rules plus imperative bullets, against the 150–200
  budget.
- **Forcing-language density** — occurrences of `MUST`, `NEVER`, `ALWAYS` and all-caps runs per
  100 lines.
- **Section size outliers** — any single section over a third of the file.

## 4. Config shape

This is one named policy in `config/repo_policies.json`, not a new config surface. It joins the
existing `policies` list on a profile — alongside `squash_merge_only` and `pinned_action_shas` —
with its settings under `defaults`, the way `gitignore_entries` already sits there:

```jsonc
// defaults
"agents_md_hygiene": {
  "max_lines": 120,
  "claude_md": "symlink",          // symlink | absent | any
  "require": ["description", "package_manager", "commands"],
  "check_paths": true,
  "report_only": ["instruction_count", "forcing_language", "section_outliers"]
}
```

```jsonc
// profiles.private_workspace
"policies": ["squash_merge_only", "pinned_action_shas", "agents_md_hygiene"]
```

Per-repo departures go where the other per-repo values go rather than in a nested `overrides`
block — `shared-utilities` is the sync source for three repos and carries the shared block, so
it earns a higher ceiling than a leaf repo does.

Both profiles want this policy, but `public_open_source` wants it at a stricter budget: a public
repo's `AGENTS.md` is read by contributors as well as agents.

## 5. Using it

```bash
repo-governance agents-audit                 # this repo
repo-governance agents-audit --all           # every managed repo
repo-governance agents-audit --remote        # every default branch on GitHub, cloned or not
repo-governance agents-audit --all --json    # for an agent; stdout is the payload
repo-governance agents-audit --strict        # fail on warnings too
repo-governance agents-audit --write-waivers # stub out a dismissal per finding
```

Errors fail the command; warnings only fail under `--strict`, so an unaligned repo does not
block CI on day one. `--json` writes the payload to stdout and the repo-selection narration to
stderr, per § *Agent DX* in [`agent-consistency.md`](./agent-consistency.md) — a stream for a
machine carries no Rich formatting.

### Dismissing a finding

A repo waives one by committing `.agents-audit.json`:

```json
{
  "waivers": [
    {
      "check": "docs-orphan",
      "path": "docs/surface-readiness.md",
      "reason": "Standing analysis, linked from the issue tracker rather than the docs tree.",
      "expires": "2026-12-01"
    }
  ]
}
```

The key is `check:path`, deliberately excluding the message — a waiver keyed on wording stops
applying the moment a check is reworded, which is how a suppression list silently reopens.

Three properties keep the file from becoming its own ball of mud, and each is a finding in its
own right:

| | |
|---|---|
| `waiver-unreasoned` | **error** — an empty `reason` does not suppress. A dismissal nobody can review is not a decision |
| `waiver-expired` | **warn** — past `expires`, it stops suppressing and says so |
| `waiver-stale` | **info** — it suppresses nothing, because the finding is fixed. Delete it |

`--write-waivers` emits the stubs with an empty reason, so the loop is: run it, get the
skeleton, and the audit stays red until each entry is justified.

### Estate baseline, 9 Sep 2026

15 repos audited, 1 clean. 80 findings — 20 errors, 60 warnings:

| count | check |
|---|---|
| 53 | `docs-orphan` |
| 9 | `required-content` |
| 6 | `duplicate-heading` |
| 5 | `path-liveness` |
| 3 | `root-size` |
| 2 | `pointer-integrity` |
| 2 | `claude-md-shape` |

`docs-orphan` dominates because most repos have never linked their docs tree from the root
file at all — which is the finding, not noise: an unlinked doc is one no agent loads.

Six `duplicate-heading` findings are the append bug in § *Append-only alignment* — including
one in **this repo**, at `AGENTS.md:170`. The tool did it to its own source of truth.

## 6. Sequencing

**Done** — all eight deterministic checks, the waiver mechanism, and `--json` for the agent
rubric. The audit runs from C# (`src/Rig.Core/Governance/AgentsMdAudit.cs`), tested in
`tests/Rig.Core.Tests/Governance/`.

Still outstanding, in order:

1. **Fix the append bug.** `align_repo_conventions` should diff, not append. The audit now
   *reports* the damage as `duplicate-heading`, but the tool that causes it is unchanged, so a
   fixed repo can be re-broken. One-line condition change plus a diff report.
1. **Clear the 20 errors**, which are mostly `required-content` — nine repos have no commands
   section, so an agent cannot learn how to build them from the root file.
1. **`CLAUDE.md` normalisation** — **done.** The policy default is now `absent`. Claude Code
   v2.1.277 reads `AGENTS.md` natively, and on the default `claude-md-or-agents-md` mode a
   present `CLAUDE.md` *wins*, so the pointer stubs in `blog` and `shared-utilities` were
   shadowing 6.2KB and 9.9KB of `AGENTS.md` respectively rather than redirecting to it. All
   three (both stubs and the estate-root symlink) were removed, and `sync_estate_router` no
   longer generates one.
1. **Bespoke agent files** — **done.** The same rule generalised to every tool that shipped its
   own instruction file. One list drives both halves: `agents-audit` fails a repo that carries
   one, and `workspace sync` writes the paths into `.gitignore`. `devenv` no longer creates a
   `CLAUDE.md` symlink at the estate root.
1. **Remote sweep** — **done.** `agents-audit --remote` reads each default-branch tree over the
   GitHub API. `--all` only walks local clones, which is how `the-vault-system` kept a
   `CLAUDE.md` through the first sweep: it has no local clone. Archived repos are reported but
   do not fail the command — removing a file there means unarchive, commit, re-archive, and
   `the-vault-system`'s `CLAUDE.md` is a symlink to `AGENTS.md`, so it resolves correctly and
   harms nothing while the repo stays frozen. An owner it cannot list or a tree it cannot read
   fails the command and is named (`remote_unread` in `--json`), never counted as clean.
1. **Wire it into CI**, once the errors are cleared, so it gates rather than reports.
1. **Land the agent rubric** — a `/agents-audit` command that feeds `--json` plus
   `agent-consistency.md` to an agent for the judgement calls in §3.
