# ADR 0013: Dynamic Project-Aware Python Runtime Resolution via uv

- **Status:** Accepted
- **Applies to:** `devenv` runtimes setup step

## Context
Development environments span repositories with differing Python version constraints (e.g. `>=3.12` vs `>=3.13`). Hardcoding a single static Python version globally causes incompatibility when onboarding across diverse codebases.

## Decision
When configuring Python runtimes in `steps/runtimes.py`, `devenv` dynamically inspects the local working directory for:
1. `.python-version` file
2. `pyproject.toml` (`requires-python` constraint)

If a local requirement is detected, `uv python install <version>` installs that exact target version. If no local constraint is found, it defaults globally to the latest stable LTS (**Python 3.13**).

## Consequences
- Guarantees repository requirements are satisfied immediately without manual toolchain switching.
- Leverages `uv`'s fast, isolated Python binary management.

## References
- [`src/Rig.Core/Devenv/Runtimes.cs`](../../src/Rig.Core/Devenv/Runtimes.cs)
