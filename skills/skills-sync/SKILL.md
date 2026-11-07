---
name: skills-sync
description: Manage, audit, and synchronize cross-CLI AI agent skills across OpenCode, Claude Code, OpenAI Codex, and Google Antigravity. Use when the user asks to sync skills, update upstream repositories, add or select skills in config/upstream-skills.json, audit skill symlinks, or prune broken links.
---

# Skills Sync: Cross-CLI AI Agent Skill System

A unified, multi-agent skill synchronization and vendoring system across the four primary AI coding CLI environments: **OpenCode**, **Claude Code**, **OpenAI Codex**, and **Google Antigravity (`agy`)**.

## Architectural Overview

This system owns the **harness baseline** — the ~33 skills that help the coding harness itself work,
mirrored globally via symlinks from `shared-utilities` while keeping version control clean.

> **It is no longer the only skill store.** The ~96 *domain* skills (.NET, Redis, Elasticsearch,
> OpenTelemetry, Postgres/MySQL, React, Tailwind, SEO, Vercel, Obsidian) live in the Obsidian vault
> as an OpenKnowledge project under `.agents/skills/`, installed into **no** editor host. They cost
> no session context and are read on demand with `mcp__open-knowledge__skills({})` then
> `skills({ name })`. Do not re-add a domain family here to make one reachable — that reintroduces
> the per-session cost this split exists to remove. See `docs/agents/skills-catalog.md`.

The boundary: **if a skill is useful on a task in any repository, it belongs in the baseline; if it
is useful only when a particular technology is in play, it belongs in the vault library.**

```
                                  ┌──────────────────────────────┐
                                  │ config/upstream-skills.json  │
                                  └──────────────┬───────────────┘
                                                 │ schema-validated by Pydantic
                                                 ▼
┌───────────────────────────┐      ┌──────────────────────────────┐
│  ~/code/external/<repo>   │─────►│  skills/vendored/<vendor>/   │ (gitignored)
│  (Upstream Git Clones)    │      └──────────────┬───────────────┘
└───────────────────────────┘                     │
                                                  │
┌───────────────────────────┐                     │
│  skills/<native-skill>/   │─────────────────────┤
│  (Estate Custom Skills)   │                     │
└───────────────────────────┘                     │
                                                  ▼
                         ┌──────────────────────────────────────────────────┐
                         │      uv run workspace skills update / sync       │
                         └────────────────────────┬─────────────────────────┘
                                                  │
         ┌────────────────────────┬───────────────┴────────┬────────────────────────┐
         ▼                        ▼                        ▼                        ▼
┌──────────────────┐    ┌──────────────────┐    ┌──────────────────┐    ┌──────────────────┐
│     OpenCode     │    │   Claude Code    │    │   OpenAI Codex   │    │Google Antigravity│
│ ~/.config/       │    │ ~/.claude/       │    │ ~/.codex/        │    │ ~/.gemini/       │
│   opencode/skills│    │   skills/        │    │   skills/        │    │   config/skills/ │
└──────────────────┘    └──────────────────┘    └──────────────────┘    └──────────────────┘
```

### Global CLI Directory Mapping

| CLI Platform | Global Skills Directory | Discovery Format |
|---|---|---|
| **OpenCode** | `~/.config/opencode/skills/` | `<skill-name>/SKILL.md` |
| **Claude Code** | `~/.claude/skills/` | `<skill-name>/SKILL.md` |
| **OpenAI Codex** | `~/.codex/skills/` | `<skill-name>/SKILL.md` |
| **Google Antigravity (`agy`)** | `~/.gemini/config/skills/` | `<skill-name>/SKILL.md` |

---

## Agent Runbook

When asked to update, audit, add, or customize skills, follow these steps:

### 1. Check System Health
Run a fast audit to check active symlinks, missing skills, and broken paths:

```bash
uv run workspace skills status
```

Expected output confirms all 4 systems report `✓ Healthy (N/N)` with zero issues.

### 2. Configure Upstream Repositories & Allowlist Mechanism
Upstream vendors and allowed skills are configured declaratively in `shared-utilities/config/upstream-skills.json`.

The system supports a **two-tier allowlist mechanism**:
1. **Vendor-Level Allowlist** (`allowlist: [...]`): Strictly controls which skill packages are extracted from each upstream clone. Only explicitly permitted skills enter `skills/vendored/`.
2. **Estate-Wide Allowlist** (Optional `config/skills-allowlist.json`): If present, restricts which skills across all sources (both estate-native and vendored) are deployed to the AI CLI environments.

#### Schema Definition (`config/upstream-skills.json`)

```json
[
  {
    "name": "unique-vendor-identifier",
    "url": "https://github.com/owner/repo.git",
    "folder_name": "repo-folder-name",
    "branch": "main",
    "skills_subdir": "skills",
    "allowlist": [
      "specific-skill-a",
      "prefix-pattern-*",
      "specific-skill-b"
    ],
    "exclude": [
      "unwanted-skill"
    ]
  }
]
```

#### Field Specifications:
- `name` *(string, required)*: Unique identifier used for the vendored folder (`skills/vendored/<name>/`).
- `url` *(string, required)*: Git clone URL (HTTPS or SSH).
- `folder_name` *(string, optional)*: Directory name in `~/code/external/`. Defaults to `name` if omitted.
- `branch` *(string, optional)*: Branch to track (default: `"main"`).
- `skills_subdir` *(string, optional)*: Subdirectory containing skill packages (default: `"skills"`; use `"plugins"` for `visual-explainer`, `"agentfiles/shared/skills"` for `agent-chisels`).
- `allowlist` *(array of strings, optional)*: Whitelist of skill folder names or fnmatch globs to extract. Takes precedence over `include`.
- `include` *(array of strings, optional)*: Backwards-compatible alias for `allowlist`.
- `exclude` *(array of strings, optional)*: Blacklist of skill folder names or globs to ignore even if matched by the allowlist.

### 3. Evaluate & Benchmark Skills (`evaluate-skills` Metaskill)
The vendored `evaluate-skills` metaskill (from `agent-chisels`) provides systematic quality assurance for AI agent skills across 10 dimensions:

- **Dimension 1: Size & Length** (body under 500 lines, descriptive titles)
- **Dimension 2: Token Economy** (challenge each paragraph; avoid teaching what LLMs already know)
- **Dimension 3: Degrees of Freedom** (high freedom for creative tasks, low freedom for fragile/destructive operations)
- **Dimension 4: Scope Definition** (single focused capability; avoid scope creep)
- **Dimension 5: Description Quality** (third-person voice, discoverable search terms, when-to-use triggers)
- **Dimension 6: Structure & Organization** (progressive disclosure, clear steps, constraints)
- **Dimension 7: Examples** (concrete realistic examples, edge cases)
- **Dimension 8: Anti-Pattern Detection** (no Windows paths, no magic numbers, no missing validation loops)
- **Dimension 9: Prompt Engineering Quality** (imperative language, explicit rules)
- **Dimension 10: Completeness** (prerequisites, limitations, clear error handling)

To evaluate any skill in your catalog:
```
Ask the agent: "evaluate the skill <skill-name>"
```
The agent will parse the skill's `SKILL.md`, score each dimension, identify anti-patterns, and provide an actionable improvement report.

### 4. Apply Changes & Synchronize
Run the update command:

```bash
uv run workspace skills update
```

This command automatically:
1. Validates `config/upstream-skills.json` against the Pydantic schema.
2. Clones new upstream repos into `~/code/external/` or runs `git pull --ff-only` on existing clones.
3. Extracts only matching skills into `skills/vendored/<vendor>/<skill>/`.
4. Purges any stale skills from `skills/vendored/` that are no longer in the allowlist.
5. Synchronizes symlinks across all 4 CLIs with `--prune` enabled to delete orphaned links.

To update a single vendor without pulling all repos:
```bash
uv run workspace skills update --vendor <vendor-name>
```

### 5. Choosing Which Skills Are Linked
The estate allowlist, `config/skills-allowlist.json`, decides which skills are linked. It takes skill names or glob patterns. The system is declarative: edit the file, then reconcile.

1. Remove a skill (or narrow a pattern) in `config/skills-allowlist.json`.
2. Run `uv run workspace skills sync`. Skills that are no longer allowed are pruned from every CLI environment.
3. For a temporary, reversible overlay of hidden skills, use `uv run workspace skills activate <name>` and `uv run workspace skills deactivate --all`.

### 6. Grounded & Vendored Skills Estate
All skills in the estate must be grounded in maintained upstream repositories (configured in `config/upstream-skills.json`). Upstream repositories are synced into `~/code/external/` and extracted cleanly to `skills/vendored/`.

---

## Command Reference

| Command | Purpose |
|---|---|
| `uv run workspace skills status` | Fast audit and verification of all 4 CLI directories with active, broken, and disabled counts per config. |
| `uv run workspace skills list` | List all discovered skills with their Enabled/Disabled status and descriptions. |
| `uv run workspace skills list --status <enabled\|disabled>` | Filter catalog listing by enabled or disabled status. |
| `uv run workspace skills sync` | Reconcile declared active skills into CLI target directories (pruning disabled skills). |
| `uv run workspace skills sync --dry-run` | Preview symlink additions and pruning without making changes. |
| `uv run workspace skills sync --prune` | Synchronize and remove dangling or stale symlinks. |
| `uv run workspace skills update` | Pull upstream git clones, extract selected skills, and re-sync symlinks. |
| `uv run workspace skills update --vendor <name>` | Update only a specific vendor. |
| `uv run workspace skills prune` | Purge broken symlinks pointing to non-existent targets. |

---

## Invariants & Rules

1. **Clean Monorepo**: Upstream clones live in `~/code/external/`. Never clone third-party repositories directly into the monorepo root or estate folders.
2. **Gitignored Vendoring**: `skills/vendored/` is added to `.gitignore` so third-party skill code never pollutes git history.
3. **Execution Environment**: Always invoke with `uv run workspace ...`. Never invoke bare python.
4. **Symlink Verification**: Before finishing any maintenance task, verify `uv run workspace skills status` reports `✓ Healthy` with 0 issues.
5. **Declarative Skill Management**: Configuration is declared in `config/skills-allowlist.json` and `config/upstream-skills.json`. The CLI verifies and reconciles disk symlinks against the declared JSON files rather than providing mutating write commands. All skills must be grounded in maintained upstream repositories.
