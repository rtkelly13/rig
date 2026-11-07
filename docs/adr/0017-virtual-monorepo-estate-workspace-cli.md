# ADR 0017: Virtual Monorepo Estate Management via `workspace` CLI

- **Status:** Accepted
- **Applies to:** Personal code estate (`/Users/ryankelly/code/personal`), `shared-utilities`, and AI coding assistants

## Context
The personal development environment consists of multiple autonomous Git repositories residing under a single code root (`/Users/ryankelly/code/personal/`). The monorepo workspace is opened directly from the `personal` directory itself (rather than using nested internal symlinks into `shared-utilities/projects/`).

As the estate expands, several coordination challenges emerge:
1. **Agent Context Fragmentation**: Coding agents opened at `/Users/ryankelly/code/personal/` lack a top-level routing index, domain map, and guidelines for navigating across peer repositories and utilizing the centralized shared utilities.
2. **Unmanaged Estate Drift**: Clones, temporary scratch directories, and unmanaged files accumulate in the `personal/` root without central attribution, policy enforcement, or cleanup.
3. **Absence of a Strict Directory Authority**: There is currently no active control plane ensuring that only deliberate, tracked assets exist within `/Users/ryankelly/code/personal/`.

## Decision
1. **Authoritative `workspace` CLI in `shared-utilities`**:
   Introduce a dedicated `workspace` CLI entry point in `shared-utilities` (invocable via `uv run workspace`) responsible for managing and enforcing invariants across the parent `personal` folder:
   - **`audit`**: Validates the contents of the `personal/` root against registered estate policy. Flags any unmanaged directory, untracked file, or configuration drift.
   - **`sync`**: Synchronizes estate-level configuration and root guidance files (such as `personal/AGENTS.md`, `CLAUDE.md`, and `personal.code-workspace`).
   - **`status`**: Summarizes the health, git status, and managed inventory of the entire personal virtual monorepo estate.
   - **`clean`**: Enforces the invariant that **nothing exists in the `personal/` folder without the CLI managing it**, alerting on or safely pruning unmanaged debris.

2. **No Peer Symlinks**:
   Because the workspace is opened directly from the `personal/` root directory, internal symlink machinery (such as linking siblings into `shared-utilities/projects/`) is unnecessary and omitted. Peer repositories remain standard sibling directories managed directly in place.

3. **Root Orchestration Contract (`personal/AGENTS.md`)**:
   Maintain a root `AGENTS.md` and symlinked `CLAUDE.md` in `/Users/ryankelly/code/personal/`, generated and synchronized by the `workspace` CLI. In accordance with the 200-instruction budget and progressive disclosure principles (`docs/agents/agent-consistency.md`), this file serves as a high-level router:
   - Identifies the folder as a virtual monorepo estate.
   - Explicitly designates `shared-utilities` as the source of estate tooling, CLI commands, and governance scripts.
   - Maps each peer repository to its primary domain, pointing agents directly to the localized `AGENTS.md` in each repo when working within that scope.
   - Outlines cross-cutting estate rules (e.g. linear history, PR workflow, running commands via `uv run`).

4. **Single Source of Truth**:
   Configuration under `config/managed-projects.json` (and `config/repo_policies.json`) serves as the authoritative registry of known estate repositories, their purposes, and estate-level policies.

5. **Org Folders (amendment, 2026-10)**:
   Repos owned by one of the estate's own GitHub orgs live in a sibling folder named for the org
   (`~/code/rtkelly-labs/<name>`, `~/code/Resultful/<name>`), so the folder tree mirrors GitHub
   ownership. Repos owned by `rtkelly13`, and anything else (forks, reference clones), stay in
   `personal/`. The org list and the path rule live in `src/shared_utilities/workspace/layout.py`;
   `audit`, `sync`, `doctor`, `packages audit` and the governance `--all` sweeps all resolve paths
   through it. `personal/AGENTS.md` and `personal.code-workspace` link into the org folders with
   `../<org>/<name>` paths.

## Consequences
- The `personal` directory functions as an intentional, disciplined virtual monorepo for both developers and AI agents without complex symlink indirection.
- Guarantees zero unmanaged drift or clutter in `/Users/ryankelly/code/personal/`.
- Agents opening the `personal` workspace immediately receive structured, progressive context routing across all projects.

## References
- [`docs/agents/agent-consistency.md`](../agents/agent-consistency.md)
- [`docs/estate/README.md`](../estate/README.md)
- [`docs/repo-governance/`](../repo-governance/README.md)
- [`config/managed-projects.json`](../../config/managed-projects.json)
