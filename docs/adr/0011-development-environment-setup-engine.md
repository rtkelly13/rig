# ADR 0011: Development Environment Setup Engine (devenv)

- **Status:** Accepted
- **Applies to:** `src/shared_utilities/devenv`

## Context
Onboarding developers or configuring new machines requires installing tools, runtimes, shell themes, fonts, Git identities, and AI assistants. Manual setup is error-prone and differs widely between macOS and Windows.

## Decision
Implement a unified, cross-platform onboarding engine (`devenv`):
- An ordered list of setup steps (`SetupCommand.Steps`); each step is a name, its confirm default and the rig command lines it runs. (The original `StepRegistry` and `@step` decorator were removed in rig #30, plan item C11; the Python engine was ported to C# in #85.)
- `full`, `express`, and `agent` execution modes.
- A runner with step skipping, error recovery, and dry-run inspection.

## Consequences
- Fully automated, reproducible developer environment onboarding on Mac and Windows.
- Exposes `uv run devenv setup`, `tools`, `runtimes`, `fonts`, `git`, `ai`, and `doctor`.

## References
- [`src/Rig.Cli/Devenv/SetupCommand.cs`](../../src/Rig.Cli/Devenv/SetupCommand.cs)
