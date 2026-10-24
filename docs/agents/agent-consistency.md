# AGENTS.md Consistency & Best Practices

Based on the official [AI Hero Guide to AGENTS.md](https://www.aihero.dev/a-complete-guide-to-agents-md), these rules define how to maintain a high-performance, context-efficient environment for AI agents.

## Core Philosophy

An `AGENTS.md` file is a **configuration layer** between the agent's base instructions and the codebase. It should prioritize high-level context and ruthless minimization to preserve the agent's reasoning capacity.

## The Rules

### 1. Respect the Instruction Budget

- **The 200-Rule**: Frontier LLMs typically follow 150–200 instructions consistently. Every line in `AGENTS.md` consumes this budget.
- **Ruthless Minimization**: If an instruction isn't relevant to *every* task, it doesn't belong in the root `AGENTS.md`.
- **Avoid "Ball of Mud"**: Do not add a new rule every time an agent makes a mistake. This leads to conflicting instructions and "token bloat."

### 2. Practice Progressive Disclosure

Use a "discoverable resource tree" instead of one massive file.

- **The Pointer Pattern**: Use `AGENTS.md` to point to specialized documentation (e.g., "For TypeScript conventions, see `docs/TYPESCRIPT.md`").
- **Just-in-Time Context**: Ensure heavy documentation (testing patterns, API schemas) is only loaded when the agent is actually performing those tasks.
- **Nested Files**: In monorepos, place specific `AGENTS.md` files in subdirectories to provide localized context only when needed.

### 3. Capabilities, Not Structure

- **Stable Concepts Only**: Document domain concepts that are unlikely to change (e.g., the difference between a "User" and an "Account").
- **Empower the Agent**: Describe the *shape* and *purpose* of the project. Avoid documenting volatile file paths.
- **Let the Agent Explore**: Let the agent use its own tools (`grep`, `ls`, `find`) to generate "just-in-time" documentation during its planning phase.

### 4. Essential Root Content

A perfect root `AGENTS.md` should ideally contain only:

1. **One-Sentence Project Description**: Acts as a role-based anchor.
1. **Package Manager**: Explicitly state the tool (e.g., `uv`, `pnpm`).
1. **Critical Commands**: Non-standard build, test, or typecheck commands.
1. **Entry Points**: Links to the next level of documentation (Progressive Disclosure).

### 5. Maintenance & Hygiene

- **No Auto-Generation**: Never use scripts to auto-generate `AGENTS.md`. They prioritize comprehensiveness over restraint.
- **No `CLAUDE.md`**: Claude Code reads `AGENTS.md` natively (v2.1.277+), and prefers `CLAUDE.md` over it when both exist. A copy, stub or symlink therefore *shadows* `AGENTS.md`. Keep `AGENTS.md` as the only instruction file.
- **No Per-Tool Instruction Files**: The same reasoning applies to every tool that shipped its own instruction file before `AGENTS.md` was a standard — `.github/copilot-instructions.md`, `.cursorrules`, `GEMINI.md`, `.clinerules`, `.windsurfrules`, and the rest. Each tool reads its own file *instead* of `AGENTS.md`, so a copy drifts and a pointer only adds indirection. The canonical list lives in [`agent_files.py`](../../src/rig/repo_governance/agent_files.py); `repo-governance agents-audit` fails a repo that still carries one, and `workspace sync` writes the same paths into `.gitignore` so a tool cannot reintroduce one behind the audit's back. Tool *feature* definitions — `.github/agents/`, `.github/prompts/`, `.claude/skills/` — are not in the list: they extend a tool rather than competing with `AGENTS.md`.
- **Ruthless Pruning**: Periodically remove redundant, vague, or obvious instructions.

### 6. Where Content Belongs

| Location | When |
|---|---|
| Root `AGENTS.md` | Relevant to **every single task** in the repo |
| A separate doc | Relevant to one domain — TypeScript, testing, CI |
| A nested `AGENTS.md` | Relevant only inside one package of a monorepo |

The root file's job is to answer "what is this and how do I run it", then point. If an
instruction would be wasted tokens on a task that does not touch its domain, it is not root
content.

Inside `docs/`, group by subject. Each subject is a folder with a `README.md` that says what is
in it and when to read each part, and `docs/README.md` links every folder and top-level guide. An
agent then reads three small files (root, index, folder README) to reach any guide, instead of
one long list. Command flags, defaults and findings belong in the CLI's `--help`, not in a guide, and
source, config and sample output stay where they live: link them rather than pasting a long block.

### 7. Named Anti-Patterns

The source guide calls these out explicitly; the last one is the easiest to commit by accident:

- Contradictory instructions accumulating over time.
- Stale file paths — the fastest-rotting content, and it *actively misleads* rather than merely
  going quiet.
- Vague or obvious instructions ("write clean code").
- Redundant information the agent already knows.
- **Overly prescriptive tone** — "always", "never", and all-caps forcing. Emphasis spent
  everywhere is emphasis spent nowhere, and it reads as noise against the model's own
  instructions.

## Append-Only Alignment Causes Drift

Learned here rather than from the source guide, and worth stating because our own tooling did
it. `repo_governance.align_repo_conventions` appends its expected conventions block to
`AGENTS.md` when a marker string is missing. A repo that has *rewritten* that section still
lacks the marker, so the block is appended anyway — and the file ends up with two conventions
sections that contradict each other. `rtkelly13/design-system` carried exactly that for months.

An alignment step that can only detect **absence** cannot detect **divergence**. Any check that
writes to `AGENTS.md` needs to compare what is there, not test for a substring.

## Agent DX (Developer Experience) vs Human DX

As documented in [Rewrite Your CLI for AI Agents](https://justin.poehnelt.com/posts/rewrite-your-cli-for-ai-agents/), CLI tools must be built or adapted to serve agents predictably.

- **Human DX** focuses on discoverability and visual forgiveness (e.g., ANSI colors, interactive "Are you sure?" prompts, formatted ASCII tables).
- **Agent DX** requires **defense-in-depth and predictable structure**:
  1. **Structured Output**: Agents struggle to reliably parse formatted tables or stylized panels. **Always use the `--json` flag** when invoking the `help` or `qv query` commands.
  1. **Non-Interactivity**: Ensure flags exist to bypass interactive prompts (e.g., `--yes`, `--headless`, `--apply`).
  1. **Hardened Errors**: Errors should be emitted in a machine-readable format when structured output is requested, rather than relying on visual (red text) cues.
