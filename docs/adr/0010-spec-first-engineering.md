# ADR 0010: Spec-First Engineering and Socratic Alignment

- **Status:** Accepted
- **Applies to:** all feature planning and agent pairing workflows

## Context
Jumping straight into implementation ("vibe coding") without formalizing constraints leads to scope drift, missed edge cases, and costly rework.

## Decision
Adopt a **Spec-First Engineering** process using Socratic interrogation skills (`grill-me`, `grill-with-docs`) and Domain Modeling:
1. Walk down architectural decision trees, resolving dependencies one by one before writing code.
2. Maintain a canonical glossary in `CONTEXT.md` to prevent fuzzy or overloaded domain terminology.
3. Formulate hard-to-reverse decisions into ADRs immediately when agreed upon.

## Consequences
- Eliminates ambiguity and ensures consensus between developer and AI assistants.
- Architectural decisions remain durable and transparent.

## References
- [`CONTEXT.md`](../../CONTEXT.md)
