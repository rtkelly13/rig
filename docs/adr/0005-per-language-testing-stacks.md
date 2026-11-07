# ADR 0005: Idiomatic Per-Language Testing Stacks

- **Status:** Accepted
- **Applies to:** all test suites and quality gates

## Context
Each language ecosystem has idiomatic test tooling. Forcing a single cross-language framework leads to lowest-common-denominator compromises.

## Decision
Standardize on the conventional, native testing stack per ecosystem:
- **Python**: `pytest` executed via `uv run pytest`.
- **TypeScript / Frontend**: `vitest` for unit tests, `playwright` for end-to-end automation.

Tests must verify behavior through public interfaces (vertical slicing) rather than testing internal private methods or implementation details.

## Consequences
- Idiomatic, maintainable tests per ecosystem.
- No abstraction overhead.

## References
- [pytest Documentation](https://docs.pytest.org/)
- [Vitest Documentation](https://vitest.dev/)
- [Playwright Documentation](https://playwright.dev/)
