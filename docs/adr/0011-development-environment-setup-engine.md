# ADR 0011: Development Environment Setup Engine (devenv)

- **Status:** Accepted
- **Applies to:** `src/shared_utilities/devenv`

## Context
Onboarding developers or configuring new machines requires installing tools, runtimes, shell themes, fonts, Git identities, and AI assistants. Manual setup is error-prone and differs widely between macOS and Windows.

## Decision
Implement a unified, cross-platform onboarding engine (`devenv`):
- An ordered list of setup steps (`SETUP_STEPS` in `devenv/cli.py`); each `Step` is a name, a function and its confirm defaults. (The original `StepRegistry` and `@step` decorator were removed in rig #30, plan item C11.)
- Cross-platform context (`StepContext`) supporting `full`, `express`, and `agent` execution modes.
- Modular Rich UI runner with step skipping, error recovery, and dry-run inspection.

## Consequences
- Fully automated, reproducible developer environment onboarding on Mac and Windows.
- Exposes `uv run devenv setup`, `tools`, `runtimes`, `fonts`, `git`, `ai`, and `doctor`.

## References
- [`src/shared_utilities/devenv`](../../src/rig/devenv)
