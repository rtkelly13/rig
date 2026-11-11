# Skills Catalog

The repository has one skills engine, `Rig.Core.Skills`, and one command surface for it,
`rig skills`. `devenv setup` / `devenv ai` link skills by running `rig skills sync --system all`;
there is no `devenv skills` command and no second plugin registry.

## Catalog policy

`config/upstream-skills.json` controls what is vendored locally — **6 upstream families**, all
harness-focused, since the domain families (dotnet, elastic, redis, dash0, planetscale, vercel,
react-doctor, awesome-skills, visual-explainer) now live in the vault library instead. `config/skills-allowlist.json`
controls what is exposed globally to agent CLIs. This separation keeps source packages available
without loading every description into every agent's skill catalog.

### Three tiers

Every skill description in a CLI's skills directory is loaded into the preamble of every session
in every repository. That is the budget this catalog spends.

1. **Harness baseline** — `config/skills-allowlist.json`, currently **33 skills**, mirrored
   globally. Only skills that help the coding harness itself work well: planning, review, TDD,
   debugging, worktrees, writing, and estate tooling. Nothing domain-specific.
2. **Vault library** — the ~96 domain skills (.NET, EF Core, Redis, Elasticsearch, OpenTelemetry,
   Postgres/MySQL, React, Tailwind, SEO, Vercel, Obsidian) live in the Obsidian vault as an
   OpenKnowledge project under `.agents/skills/`, installed into no editor host. They cost no
   baseline context and are read on demand through the `open-knowledge` MCP `skills` tool.
3. **Dormant** — the remaining ~24 harness-adjacent skills from the vendored families
   (`wizard`, `arena`, `retro`, `prototype`, …): vendored but not exposed, reachable via the
   activation overlay below, and promotable into the baseline without re-adding a vendor.

Trimming the baseline from 125 mirrored skills to 33 cut the per-session skill preamble from
~41.8KB (~10,400 tokens) to ~8.9KB (~2,234 tokens).

A skill belongs in the baseline only if it would help on a task in *any* repository. If it is
useful only when a particular technology is in play, it belongs in the vault library.

For a targeted pass, temporarily expose hidden skills without editing the permanent allowlist:

```bash
rig skills sync --only react-best-practices --dry-run
rig skills sync --only obsidian-markdown --only obsidian-cli
rig skills sync --all --dry-run
```

`--only` is additive and does not prune the normal catalog. `--all` exposes every discovered
skill and uses normal mirroring; use `--dry-run` first when expanding the catalog. Both options
are reversible overlays recorded in `~/.config/devenv/skill-activations.json`; run
`rig skills deactivate --all` after the targeted pass to restore the curated catalog.

Equivalent explicit commands are available when the intent should be visible in automation:

```bash
rig skills activate --only react-best-practices
rig skills deactivate --only react-best-practices
rig skills activate --all
rig skills deactivate --all
```

## Family review

| Family | Retained | Excluded or deferred | Decision |
|---|---|---|---|
| Planning and delivery | `architect`, `writing-plans`, `executing-plans`, `subagent-driven-development`, `verification-before-completion`, `using-git-worktrees`, `finishing-a-development-branch` | — | Complementary lifecycle stages. |
| Review and diagnosis | `code-review`, `blast-radius`, `interrogate`, `systematic-debugging`, `crap-analysis` | `grilling`, `triage`, `retro` | Keep distinct evidence/review modes; defer conversational variants. |
| Design and domain | `codebase-design`, `domain-modeling`, `api-design`, `api-design-principles`, `principle-type-system-discipline`, `principle-boundary-discipline` | — | Different abstraction levels; API skills should be revisited if trigger collisions appear. |
| React and UI | `react-patterns`, `react-doctor`, `improve-react`, `frontend-design`, `ui-ux-pro-max`, `anti-ui-slop`, `web-design-guidelines`, `tailwind-patterns` | `react-best-practices`, `react-component-performance`, `vercel-composition-patterns` | Keep audit, architecture, and visual/UI concerns; defer overlapping performance/composition guides. |
| Observability | `observability-engineer`, `observability-service-reliability`, `observability-sre-triage`, `otel-collector`, `otel-instrumentation`, `otel-semantic-conventions`, `redis-observability`, Elastic query/index/ESQL/health skills | `grafana-dashboards` | Retained skills are either stack-specific or operationally distinct. Grafana is deferred pending use. |
| Databases | `database-performance`, `efcore-patterns`, `postgres`, `postgres-best-practices`, `mysql`, `testcontainers-integration-tests` | — | Keep runtime/database-specific guidance; reassess generic performance after usage data. |
| Redis | `redis-core`, `redis-connections`, `redis-search`, `redis-observability` | — | Distinct data modeling, client, search, and operations concerns. |
| Web and content | `agent-browser`, `defuddle`, `webapp-testing`, `visual-explainer`, `schema-markup`, SEO skills, `technical-writing`, `unslop` | `landing-page-generator` | Keep reusable research/content primitives; defer marketing-specific generation. |
| Deployment and integrations | `vercel-cli-with-tokens`, `vercel-optimize`, `stripe-integration`, `auth-implementation-patterns`, `saas-multi-tenant`, `inngest`, `mcp-builder` | `deploy-to-vercel` | Token-based Vercel management subsumes the generic deployment trigger. |

## Review rules

- Add a skill to the allowlist only when it has a distinct user intent or required domain.
- Prefer one broad skill plus narrow specialists over several broad trigger descriptions.
- Keep excluded packages vendored until a usage review proves they are unnecessary.
- Re-run catalog metrics whenever upstream vendors are updated.
