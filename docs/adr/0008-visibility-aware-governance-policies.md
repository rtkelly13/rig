# ADR 0008: Intentional Visibility-Aware Policy Templating for Governance

- **Status:** Accepted
- **Applies to:** `shared_utilities.governance` / repository policy engine

## Context
Applying centralized branch protection, required status checks, and workflow governance across GitHub repositories must distinguish between public open-source repos and private repositories to avoid invalid check configurations.

## Decision
Implement a visibility-aware policy templating engine in `shared_utilities.governance`:
- Evaluates repository visibility (`public`, `private`, `internal`) during sync.
- Resolves check names dynamically based on target workflows.

## Consequences
- Prevents broken CI gates on newly created repositories.
- Centralized policy enforcement with zero manual GitHub UI configuration.

## References
- [`src/shared_utilities/repo_governance`](../../src/rig/repo_governance)
