# AGENTS.md

Personal code estate virtual monorepo (`/Users/ryankelly/code/personal`).

This repository root is a **progressive-disclosure router**. Individual project context, commands,
and coding standards live in each child repository's localized `AGENTS.md`.

## Estate Governance & Tooling

- **Tooling & Orchestration Source**: All cross-repo tooling, CLI automation, and workspace management
  reside in `shared-utilities`. Use `uv run workspace <command>` for estate management.
- **Invariants**: Nothing exists in `/Users/ryankelly/code/personal/` without the `workspace` CLI
  managing it.
- **Org Folders**: Repos owned by the `rtkelly-labs` and `Resultful` GitHub orgs live beside this
  folder in `../rtkelly-labs/` and `../Resultful/`; the table below links to them there.
- **Python Execution**: Always use `uv run` or `uv`. Never run bare `python` or `python3`.
- **Git Conventions**: Linear history with PR squash-and-merge; never commit directly to primary branches.

## Domain Map & Managed Repositories

<!-- MANAGED_PROJECTS_START -->
<!-- MANAGED_PROJECTS_END -->

## Working Rules for Agents

1. **Navigate to the target repo**: When tasked with work on an application or library, inspect that
   repo's local `AGENTS.md` and README for its specific tech stack, test runner, and guidelines.
2. **Maintain Estate Cleanliness**: Do not create arbitrary scratch folders or files in the estate root.
   Use `shared-utilities/temp/` or in-repo scratch areas.
3. **Synchronize Changes**: If adding a new repository to the estate, register it in
   `shared-utilities/config/managed-projects.json` and run `uv run workspace sync`.
4. **Brand Asset & Production Cuts Invariant**: For any managed brand asset project, production cuts
   **MUST ALWAYS** be derived strictly from `production-cuts.csv` situated within that project's
   `production-cuts/` folder. All exported production cuts must reside in `production-cuts/` to prevent
   root directory clutter, and any unlisted files are strictly ignored.
5. **Domain Skills Live in the Vault, Not in Context**: Only ~33 *harness* skills (review, TDD,
   debugging, plans, worktrees, writing) are loaded. Guidance for **.NET/EF Core, Redis,
   Elasticsearch, OpenTelemetry, Postgres/MySQL, React, Tailwind, SEO, Vercel and Obsidian** is
   **not** loaded — it lives in the vault's OpenKnowledge library, uninstalled by design. Before
   answering a question in one of those areas from memory, check the library first:

   ```
   mcp__open-knowledge__skills({ cwd: "/Users/ryankelly/code/personal/obsidian-vault" })
   mcp__open-knowledge__skills({ cwd: "…", name: "efcore-patterns" })
   ```

   List, then read the one that matches. Do not re-derive guidance a skill already covers, and do
   not re-add a domain skill to `shared-utilities` to make it reachable — see
   `shared-utilities/docs/adr/0019-skills-split-between-harness-baseline-and-vault-library.md`.
