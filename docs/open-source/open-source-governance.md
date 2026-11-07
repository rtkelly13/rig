# Open Source Governance & Triage Architecture

> [!TIP]
> For the estate-wide hub specification, foundational repository conventions, and complete bootstrap checklist, see [**Open Source Project Expectations**](./open-source-project-expectations.md).

This document describes the design, operational workflows, and CLI shape for managing **Public Open Source Software (OSS)** repositories across the personal project estate.

---

## 1. Overview & Operational Philosophy

The `repo-governance` tool in `shared-utilities` automates repository management across multiple distinct layers:
1. **Branch & CI Governance**: Branch protection rulesets, required check names (`ci`, `pr-checks`, `build`), pinned action SHAs, squash merge policy, and linear history.
2. **Open Source Health & Compliance**: Standardized software licenses (MIT default, Apache-2.0 / BSD-3 options), SPDX source headers, and community health files (`CONTRIBUTING.md`, `CODE_OF_CONDUCT.md`, `SECURITY.md`).
3. **Structured Contributions**: Schema-validated GitHub Issue Forms (`.github/ISSUE_TEMPLATE/*.yml`) and Pull Request templates (`.github/PULL_REQUEST_TEMPLATE.md`) to ensure reproducibility and consistency.
4. **Estate-Wide Triage**: Cross-repository issue and pull request triage dashboards with automated batch actions (updating behind branches, auto-merging verified clean PRs).

---

## 2. CLI Command Shape & Syntax

All commands in `repo-governance` adhere to a uniform CLI shape built with Typer and Rich:

```text
repo-governance <subcommand> [TARGET_DIR] [OPTIONS]
```

### Conventions
- **Target Argument**: Optional positional argument `[TARGET_DIR]` defaults to current working directory (`.`).
- **Estate Flag**: `--all` (`-a`) scans and operates on all repositories configured under `/Users/ryankelly/code/personal/` that match the active profile.
- **Organization Flag**: `--org` (`-o`) targets all repositories under a specified GitHub organization (e.g. `rtkelly-labs`), discovering active repos via the GitHub CLI or filtering local estate clones.
- **Safety**: Modifying commands support `--dry-run` or distinct `--audit` vs `--enforce` flags to guarantee read-only evaluation before mutation.
- **Output**: Colored terminal dashboards (Rich tables, status panels) with optional `--json` export for programmatic pipelines.

### Command Reference

| Command | Purpose | Mutates | Key Flags |
| :--- | :--- | :---: | :--- |
| **`license`** | Audit and enforce canonical licenses & SPDX headers | Yes (`--enforce`) | `--audit`, `--enforce`, `--spdx`, `--type`, `--holder`, `--org`, `--all` |
| **`sync-templates`** | Deploy issue forms, PR template, CONTRIBUTING, and CoC | Yes (`--apply`) | `--audit`, `--apply`, `--dry-run`, `--org`, `--all` |
| **`sync-labels`** | Synchronize standard GitHub label taxonomy and hex colors | Yes (`--apply`) | `--audit`, `--apply`, `--dry-run`, `--org`, `--all` |
| **`environments`** | Create/verify declared GitHub deployment environments (e.g. `release`) | Yes (`--apply`) | `--audit`, `--apply`, `--dry-run`, `--org`, `--all` |
| **`apps`** | Audit GitHub Apps, sync repo scoping, and align review tool configs | Yes (`--enforce`) | `--audit`, `--enforce`, `--review-tools`, `--coderabbit`, `--org`, `--dry-run` |
| **`issue-triage`** | Terminal dashboard for open issues across the estate or organization | No | `--all`, `--org`, `--repo`, `--label`, `--json` |
| **`pr-triage`** | Terminal dashboard for open PRs with automated batch actions | Yes (with flags) | `--all`, `--org`, `--repo`, `--update-behind`, `--merge-ready`, `--json` |

---

## 3. License Management & SPDX Compliance

### Supported Licenses
The license manager supports standard Open Source Initiative (OSI) approved licenses:
- **MIT** (Default): Permissive, lightweight, standard for personal libraries and utilities.
- **Apache-2.0**: Permissive with explicit grant of patent rights and contributor attribution.
- **BSD-3-Clause**: Permissive with non-endorsement clause.
- **Unlicense**: Public domain dedication.

### Audit & Enforcement Rules
1. **File Presence**: Repository root must contain a canonical `LICENSE` file matching the target license text.
2. **Copyright Holder & Year**: Copyright line must specify `Ryan Kelly` (or declared profile holder) with a valid year range (e.g. `2024-present` or `2026`).
3. **SPDX Headers**: Source files (`.ts`, `.tsx`, `.py`, `.cs`, `.fsi`) should include standard SPDX headers in their file preamble:
   ```typescript
   // SPDX-License-Identifier: MIT
   // Copyright (c) 2026 Ryan Kelly
   ```

### Usage
```bash
# Audit license file and copyright year for current repo
uv run repo-governance license --audit

# Enforce canonical MIT license with current year
uv run repo-governance license --enforce --type MIT

# Audit licenses across all personal repositories
uv run repo-governance license --all --audit
```

---

## 4. Community & Contribution Templates

Instead of unstructured freeform markdown, public repositories use schema-validated **GitHub Issue Forms** (`.github/ISSUE_TEMPLATE/*.yml`) and comprehensive community guidance.

### 1. Issue Forms (`.github/ISSUE_TEMPLATE/`)
- **`bug_report.yml`**:
  - Validates issue description.
  - Requires explicit reproduction steps.
  - Captures runtime environment (OS, Node.js/.NET/Python version, browser).
  - Code block for minimal reproducible sample.
- **`feature_request.yml`**:
  - Clear problem statement ("Is your feature request related to a problem?").
  - Proposed solution or API signature.
  - Alternatives considered.
- **`config.yml`**:
  - Disables blank issues.
  - Directs general questions to GitHub Discussions or documentation.

### 2. Pull Request Template (`.github/PULL_REQUEST_TEMPLATE.md`)
- **Summary & Rationale**: Context and technical intent.
- **Linked Issues**: Closing keywords (`Resolves #...`, `Fixes #...`).
- **Type of Change**: Aligns with Semantic Versioning (`type: bug`, `type: feature`, `breaking-change`, `type: docs`, `type: maintenance`).
- **Breaking Change Impact**: Detailed migration instructions when breaking changes are introduced.
- **AI Utilisation Disclosure**: Transparent attribution and certification of compliance with project quality/licensing rules.
- **Verification Checklist**: Linear history rebase on `origin/main`, test coverage, CI gate agreement, and documentation parity.

> [!TIP]
> For the comprehensive architectural specification, field validations, and intake lifecycle, see [**Open Source Project Expectations (Section 8)**](./open-source-project-expectations.md#8-structured-issue-forms--pull-request-templates) and tracking issue [#367](https://github.com/rtkelly13/rig/issues/367).

### 3. Community Health & Governance Files
- **`CONTRIBUTING.md`**: Outlines local development setup (`uv`, `pnpm`, `dotnet`), code style rules, testing instructions, linear history policy, and squash-merge conventions.
- **`CODE_OF_CONDUCT.md`**: Contributor Covenant v2.1 with reporting email (`ryan@ryankelly.dev`).
- **`SECURITY.md`**: Coordinated vulnerability disclosure policy.
- **`AI_POLICY.md`**: Generative AI utilization and contributor transparency policy, mandating 100% human accountability for merged code.

### Usage
```bash
# Preview template synchronization on current repo
uv run repo-governance sync-templates --dry-run

# Deploy templates across all public OSS repos
uv run repo-governance sync-templates --all --apply
```

---

## 5. Standard GitHub Label Taxonomy

To prevent label proliferation and maintain visual clarity across GitHub notifications and issue trackers, `sync-labels` reconciles a consistent taxonomy with standard colors:

| Category | Label | Color | Description |
| :--- | :--- | :--- | :--- |
| **Type** | `type: bug` | `#d73a4a` (Red) | Something is broken or behaving unexpectedly |
| | `type: feature` | `#a2eeef` (Light Blue) | New feature or capability request |
| | `type: performance`| `#ffc107` (Amber) | Performance optimization or memory improvement |
| | `type: docs` | `#0075ca` (Deep Blue) | Documentation improvements or typo fixes |
| | `type: maintenance`| `#e4e669` (Yellow) | Chore, dependency update, or refactor |
| **Status** | `status: needs-repro`| `#e99695` (Salmon) | Cannot reproduce; waiting on submitter for minimal example |
| | `status: blocked` | `#6a737d` (Grey) | Blocked by upstream dependency or external issue |
| | `status: in-progress`| `#fbca04` (Orange) | Actively being addressed by a contributor |
| **Community**| `good first issue` | `#7057ff` (Purple) | Friendly, self-contained entry point for new contributors |
| | `help wanted` | `#008672` (Teal) | Extra attention or community contribution welcome |
| **Impact** | `breaking-change` | `#b60205` (Dark Red) | Backwards-incompatible API or behavioral change |

### Usage
```bash
# Audit label differences between repo and standard taxonomy
uv run repo-governance sync-labels --audit

# Apply standard labels to all personal repos
uv run repo-governance sync-labels --all --apply
```

---

## 6. Cross-Repo Issue Triage Dashboard

The `issue-triage` command scans all open issues across public repositories and surfaces them in four actionable buckets:

```text
╭─────────────────────────────────────────────────────────────╮
│ Open Source Issue Triage: 14 Open Issues Across 6 OSS Repos │
╰─────────────────────────────────────────────────────────────╯
🚨 NEEDS TRIAGE (No labels or 0 responses) (3)
  • rtkelly13/mermaid-toolkit #37: "Parse error on state diagram with note" (opened 2d ago by @user)
  • Resultful/Resultful #14: "Async bind operator memory footprint" (opened 5d ago by @contributor)

🐛 ACTIVE BUGS (5)
  • rtkelly13/design-system #112: "DataTable sorting icon flicker in Safari" [type: bug]
  • rtkelly13/Parquet.SourceGenerator #8: "Nullable struct schema resolution" [type: bug]

🙋 COMMUNITY / GOOD FIRST ISSUE (4)
  • rtkelly13/design-system #110: "Add keyboard navigation to DocPager" [good first issue]
  • rtkelly13/mermaid-toolkit #32: "Add example for sequence diagram actor groups" [good first issue]

💤 STALE WAITING ON REPRO (2)
  • rtkelly13/blog #152: "Theme flicker on mobile Firefox" [status: needs-repro] (19 days idle)
```

### Usage
```bash
# Triage all open issues across all personal repos
uv run repo-governance issue-triage --all

# Filter issues for a specific repo or label
uv run repo-governance issue-triage --repo rtkelly13/design-system
uv run repo-governance issue-triage --label "good first issue"
```

---

## 7. Multi-Repo Pull Request Triage & Automation

The `pr-triage` engine provides an active operational control center for open pull requests. It reads each repo's `required_checks` from `config/repo_policies.json` to evaluate whether each PR is genuinely ready to merge or blocked.

### Triage Buckets
1. 🟢 **Ready to Merge**: All required status checks passed (`pass`), merge state is `CLEAN`, linear history valid.
2. 🟡 **Behind `main`**: Mergeable and passing, but behind `main` branch tip. Can be updated via `--update-behind`.
3. 🔴 **Blocked / Failing**: One or more required checks failed, merge conflicts exist, or ruleset policy prohibits merge.
4. ⏳ **In Flight**: CI jobs currently running or queued.
5. 💤 **Stale / Idle**: No activity in $> 14$ days.

### Automated Batch Actions
- **`--update-behind`**: Calls `gh pr update-branch` on all PRs in the `Behind main` bucket to synchronize them with current `main`.
- **`--merge-ready`**: Performs safe squash-and-merge with automatic branch deletion on all PRs in the `Ready to Merge` bucket.

### Usage
```bash
# View estate PR triage dashboard
uv run repo-governance pr-triage --all

# Automatically update all branches that are clean but behind main
uv run repo-governance pr-triage --all --update-behind

# Batch squash-and-merge all passing PRs
uv run repo-governance pr-triage --all --merge-ready
```

---

## 8. Profile Integration in `repo_policies.json`

Repository capabilities are declared via profiles in [`config/repo_policies.json`](../../config/repo_policies.json):

Each profile lists its `policies`, `required_checks` and `repos`; read the `profiles` block there for the current set.

---

## 9. Automated Code Review Tooling & PR Defense

For full evaluation, tool selection matrix, and progressive adoption paths for automated code review (covering **CodeRabbit**, **GitHub CodeQL**, **Socket**, and **Codecov**), see:

👉 [**Open Source Code Review Tooling: Analysis, Selection Matrix & Adoption Path**](./code-review-tooling-adoption.md)

### Quick Commands
```bash
# Audit installed review apps and permissions on rtkelly-labs
uv run repo-governance apps --org rtkelly-labs --audit

# Automatically scope repos into installed GitHub Apps
uv run repo-governance apps --org rtkelly-labs --enforce

# Deploy canonical in-repo review configs (.coderabbit.yaml, codecov.yml)
uv run repo-governance apps --org rtkelly-labs --enforce --review-tools
```

