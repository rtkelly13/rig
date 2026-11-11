# Repository Domain Context & Ubiquitous Language

This document establishes the canonical domain model, ubiquitous language, and architectural boundaries for `shared-utilities`.

---

## 1. Ubiquitous Language & Core Terminology

| Term | Definition | Boundaries & Invariants |
| :--- | :--- | :--- |
| **Step** | An atomic, idempotent unit of environment setup or configuration. | Must accept a `StepContext`, respect `dry_run`, and support interactive/silent execution (ADR 0011). |
| **Catalog** | A structured JSON dataset defining available developer tools and package identifiers. | Split into curated domains (`core`, `runtimes`, `ides`, `git`, `terminal`, `ai`). Drop-in extensible via `~/.config/devenv/tools/*.json` and `./.devenv/tools/*.json`. |
| **Provider** | A concrete OS package manager adapter satisfying the `PackageManager` interface. | Strictly single native manager per OS: `HomebrewManager` (Mac/Linux) or `WingetManager` (Windows) (ADR 0012). |
| **Identity** | A Git author and SSH configuration profile bound to an email, signing key, and matching directory path. | Applied via conditional directory includes (`includeIf "gitdir:..."`) in `~/.gitconfig` (ADR 0014). |
| **Skill** | A discrete capability folder containing a YAML-frontmatter `SKILL.md` along with supporting scripts and resources. | Executed on-demand by AI coding agents (ADR 0016). |
| **Skill Plugin** | A vendored collection of skills sourced from a Git repository or local directory path. | Cached in `~/.config/devenv/vendors/<plugin-name>` and synchronized on-demand. |
| **Mirroring** | Managed reflection of active skills into global agent directories (`~/.gemini/config/skills/`, `~/.agents/skills/`, `~/.claude/skills/`, `~/.config/opencode/skills/`, `~/.codex/skills/`). | Target directories are exact mirrors; stale links are automatically pruned (ADR 0016). |
| **Governance Policy** | Declarative branch protection, required status checks, and workflow rules for GitHub repositories. | Governed by `shared_utilities.repo_governance`. Automatically accounts for public vs private repo visibility (ADR 0008). |
| **Drift Detection** | Automated validation ensuring checked-in visual assets match deterministic SVG render outputs. | Governed by `shared_utilities.image_drift`. Guarantees pixel-identical rendering across platforms (ADR 0009). |
| **Browser Profile** | Isolated user data directory and profile state for automated and agent-driven browser workflows. | Governed by `Rig.Core.Browser` (`rig browser setup` and `rig browser profiles`). Separates `dev` and `personal` state with dedicated persistent contexts. |
| **Vault Staging** | Intermediate ingestion pipeline for processing external notes (e.g. Google Keep) into markdown. | Governed by `shared_utilities.obsidian`. Converts raw data into structured Obsidian vault schemas. |
| **Infrastructure Stack** | Declarative Pulumi programs managing cloud resources, Vercel deployments, and Storybook hosting. | Managed under `infrastructure/`. Enforces IaC discipline with no manual console configuration (ADR 0007). |

---

## 2. Priority Hierarchy & Precedence

When discovering skills and configurations, `devenv` follows a **Silent Strict Priority** hierarchy:

```
1. Local Workspace (./skills/)                [Highest Priority]
2. User Personal Overrides (~/.config/devenv/skills/)
3. Registered Vendored Plugins (in declaration order) [Lowest Priority]
```

---

## 3. Engineering Quality Standards

All packages in `shared-utilities` adhere strictly to our foundational standards:
- **Package Management & Tooling**: 100% standardized on `uv` (ADR 0002).
- **Type Safety**: Strict standard typing with `pyright` (0 type suppressions, ADR 0003).
- **Formatting & Linting**: `ruff check` and `ruff format` across all files (ADR 0004).
- **Testing**: Stack-native `pytest` testing public interfaces via vertical slicing (ADR 0005).
- **Automation**: GitHub CLI (`gh`) for transport and CI/CD operations (ADR 0006).
- **Planning**: Spec-First engineering via `/grill-me` and `/domain-modeling` (ADR 0010).

---

## 4. Subsystem Architecture

```
src/shared_utilities/          # devenv is C#: src/Rig.Cli/Devenv/
├── repo_governance/    # Organization-wide GitHub policy engine
├── image_drift/        # Deterministic SVG renderer and drift detection
└── obsidian/           # Vault note processing and Google Keep staging
```
