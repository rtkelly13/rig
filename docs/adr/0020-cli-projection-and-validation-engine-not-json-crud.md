# ADR 0020: CLI as Projection and Validation Engine, Not JSON CRUD

- **Status:** Accepted
- **Applies to:** `shared-utilities`, CLI tooling architecture, personal virtual monorepo, AI coding assistants

## Context

The personal code estate (`/Users/ryankelly/code/personal`) and `shared-utilities` rely heavily on declarative configuration files as single sources of truth:
- `config/managed-projects.json`: Registry of estate repositories, remotes, domains, and stacks.
- `config/repo_policies.json`: Declarative GitHub branch protections, workflow invariants, and required checks.
- `config/cli-allowlists.json`: Estate-wide command execution allowlists.
- `config/upstream-skills.json`: Curated vendor skill repositories and mirrors.

A conventional impulse in developer platform tooling is to construct command-line CRUD interfaces around such configuration—for example, commands like `workspace add-project`, `workspace register`, `workspace adopt`, or `repo-policy update-rule`.

In an AI-augmented virtual monorepo estate, however:
1. **Agents and developers edit structured data natively**: Modern AI coding agents and human developers interact with JSON, YAML, and Markdown directly with full schema context, linting, and structural awareness.
2. **CLI CRUD creates leaky abstractions**: Wrapping JSON manipulation in CLI argument parsers introduces redundant boilerplate, brittle flag mapping, poor support for optional/nested fields, and schema drift between the CLI options and the JSON schema.
3. **The CLI's high-leverage role is operational, not transactional**: The valuable work of developer tooling is not serializing a key-value pair into a file; it is verifying reality, enforcing safety invariants, compiling projections, and orchestrating operations.

## Decision

1. **No JSON-CRUD in the CLI**:
   The CLI explicitly rejects providing commands whose primary purpose is to create, read, update, or delete entries in declarative JSON configuration files. Editing configuration is performed directly on the JSON files by agents or developers.

2. **The CLI as a Projection and Validation Engine**:
   The CLI's purpose is to surface high-leverage tools that **consume, project, and validate** declarative specifications:
   - **Validation & Auditing (`audit`)**: Compare the declared JSON contract against runtime disk and git reality. Verify git remote URLs, identify unmanaged or rogue directories, flag missing clones, detect dirty worktrees, and check for configuration drift.
   - **Projection & Synchronization (`sync`)**: Compile declarative specs into downstream operational artifacts that other tools require (e.g., rendering `managed-projects.json` into the root `personal/AGENTS.md` router, compiling `personal.code-workspace`, and synchronizing common `.gitignore` entries).
   - **Safety Enforcement (`clean`)**: Perform dangerous or stateful disk operations that require invariant validation (e.g., inspecting git dirty status and unpushed commits before allowing the removal of an unmanaged clone).
   - **Diagnostics & Status (`status`, `doctor`)**: Present clear, structured overviews and machine-readable JSON outputs for human and agent review.

3. **Single Source of Truth Flow**:
   The standard workflow for estate modifications is:
   1. The agent or developer directly edits the declarative specification (e.g., `config/managed-projects.json`).
   2. The agent runs the projection command (e.g., `uv run workspace sync`) to update generated downstream assets (`personal/AGENTS.md`, `personal.code-workspace`).
   3. The agent runs the validation command (e.g., `uv run workspace audit --strict`) to verify that the estate state satisfies all invariants.

## Consequences

- **Radical CLI Simplicity**: Avoids dozens of boilerplate CRUD subcommands and option flags that provide zero leverage over direct editing.
- **Declarative Rigor**: The JSON files remain transparent, version-controlled, PR-reviewed single sources of truth.
- **Predictable Agent Ergonomics**: Agents understand that configuration lives in `config/*.json`, and that the CLI is used for projection (`sync`), verification (`audit`), and reporting (`status`).

## References

- [ADR 0017: Virtual Monorepo Estate Management via `workspace` CLI](0017-virtual-monorepo-estate-workspace-cli.md)
- [`docs/agents/agent-consistency.md`](../agents/agent-consistency.md)
- [`config/managed-projects.json`](../../config/managed-projects.json)
