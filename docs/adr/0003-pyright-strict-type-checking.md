# ADR 0003: Strict Standard Type Checking with Pyright

- **Status:** Superseded by [0022](0022-csharp-is-rigs-primary-language.md), except for `image_drift`
- **Applies to:** all Python codebases and modules

## Context
Untyped or loosely typed Python codebases suffer from hidden runtime type errors, brittle refactoring, and degraded AI agent navigation.

## Decision
Adopt **`pyright`** in strict standard mode across all Python modules.
- Require complete type annotations across all function signatures, class members, and Pydantic models.
- Ban implicit `Any` types, unvetted casts, and type suppressions (`# type: ignore`) unless strictly required for external untyped libraries.
- Enforce type checking in CI and pre-commit checks (`uv run pyright`).

## Consequences
- Type errors are caught at build/test time rather than at runtime.
- Deep architectural clarity and reliable autocompletion across IDEs and AI coding agents.

## References
- [Pyright Documentation](https://microsoft.github.io/pyright/)
