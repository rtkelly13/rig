# ADR 0001: Record Architecture Decisions in docs/adr/

- **Status:** Accepted
- **Applies to:** `shared-utilities` (all modules and tools)

## Context
Code explains *what* was implemented; it rarely documents *why*, what trade-offs were made, or what alternatives were rejected. Without written records, foundational design decisions are repeatedly relitigated in conversations and agent sessions. We need a low-ceremony, durable architectural ledger.

## Decision
Record lightweight Architecture Decision Records (ADRs) under `docs/adr/`, one Markdown file per decision, sequentially numbered and append-only.

An ADR is offered when a decision meets all three criteria:
1. **Hard to reverse** — the cost of changing direction later is significant.
2. **Surprising without context** — a future contributor or AI agent might wonder why the implementation diverges from common assumptions.
3. **The result of a real trade-off** — alternatives were evaluated and rejected for specific reasons.

ADRs are immutable: to change a decision, author a new ADR and mark the previous one `Superseded by NNNN`.

## Consequences
- Settled decisions are recorded once and referenced directly.
- Keeps agent planning aligned with established system invariants.
- Minimal maintenance overhead — most entries are concise and focus on rationale.

## References
- [Michael Nygard — Documenting Architecture Decisions](https://cognitect.com/blog/2011/11/15/documenting-architecture-decisions)
- [adr.github.io](https://adr.github.io/)
