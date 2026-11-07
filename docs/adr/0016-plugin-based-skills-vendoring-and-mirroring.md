# ADR 0016: Plugin-Based Skills Vendoring and Global Directory Mirroring

- **Status:** Accepted — amended by [ADR 0019](0019-skills-split-between-harness-baseline-and-vault-library.md)
- **Applies to:** `devenv` custom skills subsystem and AI coding agents

> ADR 0019 narrows clause 3 (*Total Directory Mirroring*): pruning now removes only symlinks,
> never real files or directories, which belong to the agent. It also ends the assumption that
> `shared-utilities` is the only skill store — domain skills live in the vault library.
>
> The `devenv skills` facade, its `devenv skills vendor` commands and the `skill_plugins` registry in
> `~/.config/devenv/config.json` were removed (rig #30, plan item C11). Clause 4 is now served by
> `uv run workspace skills update` / `sync`; vendoring is configured in `config/upstream-skills.json`.

## Context
AI coding agents (Google Antigravity, Claude Code, OpenAI Codex, OpenCode) require modular engineering and productivity skills (`SKILL.md`) to guide disciplined planning, testing, and implementation workflows. Relying on live remote fetches or fragile ad-hoc file copies creates runtime fragility, context drift, and offline failure.

## Decision
1. **Hybrid Storage**: Skills are discovered from local workspace `./skills/`, user overrides `~/.config/devenv/skills/`, and vendored plugins cached in `~/.config/devenv/vendors/<plugin-name>`.
2. **Silent Strict Priority**: Conflict resolution follows `Workspace` > `User Overrides` > `Vendored Plugins in declaration order`. The first matching skill name silently takes precedence.
3. **Total Directory Mirroring**: Target agent directories (`~/.gemini/config/skills/`, `~/.agents/skills/`, `~/.claude/skills/`, `~/.config/opencode/skills/`, `~/.codex/skills/`) are managed as exact mirrors. Stale or deleted upstream skills are automatically pruned on install or sync.
4. **On-Demand Lifecycle**: Plugins are synchronized explicitly via `uv run devenv skills vendor sync` or unified `uv run devenv update`.

## Consequences
- AI assistants have instantaneous global access to all skills without per-project duplication.
- Repositories can override any global skill simply by creating a local `./skills/<name>` directory.
- Completely vendor/author agnostic CLI interface.

## References
- [`src/shared_utilities/devenv/steps/ai_tools/skills.py`](../../src/rig/devenv/steps/ai_tools/skills.py)
