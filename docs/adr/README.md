# Architecture Decision Records (ADRs)

This directory contains the durable architectural ledger for `shared-utilities`. It records both the foundational repository-wide engineering decisions and the architecture of specific subsystems.

---

## 1. Foundational Repository Standards

| ADR | Title | Scope | Status |
| :--- | :--- | :--- | :--- |
| **[0001](0001-record-architecture-decisions.md)** | Record Architecture Decisions in `docs/adr/` | Repository-wide | Accepted |
| **[0002](0002-uv-for-python-packaging.md)** | `uv` for Python Packaging and Execution | Python Tooling | Accepted |
| **[0003](0003-pyright-strict-type-checking.md)** | Strict Standard Type Checking with Pyright | Python Modules | Accepted |
| **[0004](0004-ruff-for-linting-and-formatting.md)** | `ruff` for Sub-Second Linting and Code Formatting | Python Modules | Accepted |
| **[0005](0005-per-language-testing-stacks.md)** | Idiomatic Per-Language Testing Stacks | QA & Testing | Accepted |
| **[0006](0006-gh-cli-for-github-workflows.md)** | GitHub CLI (`gh`) for Transport, Auth, and Automation | CI/CD & Workflows | Accepted |
| **[0007](0007-pulumi-for-infrastructure-as-code.md)** | Pulumi for Declarative Infrastructure as Code | Cloud & Hosting | Accepted |
| **[0008](0008-visibility-aware-governance-policies.md)** | Intentional Visibility-Aware Policy Templating | Governance Engine | Accepted |
| **[0009](0009-deterministic-svg-render-pipeline.md)** | Deterministic SVG Logo & Graphic Render Pipeline | Graphic Assets | Accepted |
| **[0010](0010-spec-first-engineering.md)** | Spec-First Engineering and Socratic Alignment | Agent Pairing & Design | Accepted |

---

## 2. Development Environment Setup Engine (`devenv`)

| ADR | Title | Scope | Status |
| :--- | :--- | :--- | :--- |
| **[0011](0011-development-environment-setup-engine.md)** | Development Environment Setup Engine (`devenv`) | Setup Architecture | Accepted |
| **[0012](0012-strict-single-native-package-provider.md)** | Strict Single Native Package Provider per OS | Package Management | Accepted |
| **[0013](0013-dynamic-python-runtime-resolution.md)** | Dynamic Project-Aware Python Runtime Resolution via `uv` | Runtime Provisioning | Accepted |
| **[0014](0014-multi-identity-git-directory-routing.md)** | Multi-Identity Git Routing via Conditional Directory Includes | Git / SSH | Accepted |
| **[0015](0015-non-destructive-ai-assistant-configuration.md)** | Non-Destructive AI Assistant Baseline Configuration | AI Coding Agents | Accepted |
| **[0016](0016-plugin-based-skills-vendoring-and-mirroring.md)** | Plugin-Based Skills Vendoring and Global Directory Mirroring | Skills Subsystem | Accepted |

---

## 3. Estate & Virtual Monorepo Management (`workspace`)

| ADR | Title | Scope | Status |
| :--- | :--- | :--- | :--- |
| **[0017](0017-virtual-monorepo-estate-workspace-cli.md)** | Virtual Monorepo Estate Management via `workspace` CLI | Estate Orchestration | Accepted |
| **[0018](0018-brand-asset-governance.md)** | Brand Asset Governance & the Traced-Master Promotion Gate | Graphic Assets | Accepted |
| **[0019](0019-skills-split-between-harness-baseline-and-vault-library.md)** | Skills Split Between a Harness Baseline and a Vault Library | AI Agent Skills | Accepted |
| **[0020](0020-cli-projection-and-validation-engine-not-json-crud.md)** | CLI as Projection and Validation Engine, Not JSON CRUD | CLI Architecture | Accepted |
| **[0021](0021-projections-not-runtime-interposition.md)** | Projections, Not Runtime Interposition | AI Coding Agents | Accepted |
