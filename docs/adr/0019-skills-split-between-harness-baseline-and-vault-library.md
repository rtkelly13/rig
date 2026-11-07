# ADR 0019: Skills Split Between a Harness Baseline and a Vault Library

- **Status:** Accepted
- **Applies to:** `shared_utilities.skills`, the vault's OpenKnowledge project, and AI coding agents
- **Supersedes:** parts of [ADR 0016](0016-plugin-based-skills-vendoring-and-mirroring.md) — specifically its "Total Directory Mirroring" clause and its assumption that `shared-utilities` is the only skill store

## Context

ADR 0016 established mirroring: every allowlisted skill is symlinked into each agent's global skills directory. That worked while the catalog was small. It stopped working as the catalog grew.

Every skill in a CLI's skills directory contributes its `name` and `description` to the preamble of **every session in every repository**. Measured on 2026-09-21:

```
125 mirrored skills → 41,774 bytes of frontmatter → ~10,443 tokens, per session
```

Redis, SEO, and Elasticsearch guidance was being loaded while editing a C# source generator, and .NET nullable-reference-type guidance while writing a blog post. The cost is paid on every session regardless of relevance, and it is invisible — nothing in the workflow surfaces it.

Two contributing faults were found. The allowlist had been tightened to 77 on 2026-09-15 but nothing pruned the 125 existing links, so config and disk had silently diverged. And the allowlist itself mixed harness-workflow skills with domain reference material, so even an honest sync would have left the catalog too broad.

A third problem was latent and more serious: mirroring pruned by deleting *anything* it did not recognise, including real directories via `rmtree`. Claude Code keeps its own plugin-sync bucket at `~/.claude/skills/synced/` (206 files). A single `workspace skills sync --prune` would have destroyed it.

## Decision

1. **Three tiers, not one catalog.**
   - *Harness baseline* — `config/skills-allowlist.json`, 33 skills, mirrored globally. Only skills that help the coding harness itself: planning, review, TDD, debugging, worktrees, writing, estate tooling.
   - *Vault library* — the ~96 domain skills live in `obsidian-vault/.agents/skills/` as an OpenKnowledge project, installed into **no** editor host, read on demand via the `open-knowledge` MCP `skills` tool.
   - *Dormant* — harness-adjacent skills vendored but not exposed, reachable through the existing activation overlay.

2. **The admission test.** A skill belongs in the baseline only if it would help on a task in *any* repository. If it is useful only when a particular technology is in play, it belongs in the vault library.

3. **Mirroring prunes only what it owns.** `is_prunable_entry` restricts pruning to symlinks. Real files and directories in an agent's skills directory belong to that agent and survive. Symlink pruning is unchanged, so ADR 0016's mirror guarantee — stale links are removed — still holds. The audit follows the same rule: a non-symlink is a collision only when it shadows a catalog skill name.

4. **`upstream-skills.json` carries only harness families.** The nine domain vendors were dropped; their content lives in the vault with per-skill provenance in `.ok/skills-lock.json`.

## Consequences

- Per-session skill preamble falls from ~10,443 tokens to ~2,234 — about **8,200 tokens recovered per session**, in every repository.
- **The vault library is only reached if an agent chooses to call `skills({})`.** Nothing in OpenKnowledge prompts that. The vault's own captured evidence (`agents-md-outperforms-skills-in-our-agent-evals.md`) records agents failing to invoke a skill in 56% of cases, so the pointer belongs in always-loaded `CLAUDE.md`/`AGENTS.md` context rather than in a skill. This is the weakest link in the design and should be re-tested rather than assumed.
- OpenKnowledge reduces no context by itself — an installed skill is byte-identical to a hand-made one. The only lever it provides is *placement*: leaving skills uninstalled. Its value here is storage, provenance and versioning, not loading.
- Refreshing an imported skill from upstream (`reimport`, `autoUpdate`) exists but is web-editor-only, not on the MCP or CLI surface. From an agent's seat an import is a one-time fork.
- Promoting a domain skill back into the baseline is deliberate friction: re-add the vendor *and* the allowlist entry. That friction is the point.

## References

- [ADR 0016](0016-plugin-based-skills-vendoring-and-mirroring.md) — the mirroring model this amends
- `docs/agents/skills-catalog.md` — the tier policy and admission test
- `rtkelly13/shared-utilities#152`, `rtkelly13/obsidian-vault#11`
- Vault ADR 0002 (*curation is a skill, not a package*) — why the `vault-*` skills moved to the vault they describe
