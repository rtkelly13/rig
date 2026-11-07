# ADR 0021: Projections, Not Runtime Interposition

- **Status:** Accepted
- **Applies to:** `shared-utilities`, `shared_utilities.policy`, `config/cli-allowlists.json`, AI coding assistants
- **Supersedes:** the `estate-guard` OpenCode plugin and `workspace policy exec` (PR #278)

## Context

The command policy needs to decide what an agent may run. Between October 2025 and
October 2026 that was implemented three ways, each one closer to the harness's
internals:

1. **Projection.** `config/cli-allowlists.json` is compiled into each harness's native
   permission file — `permissions.allow`/`deny` in Claude and Antigravity,
   `permission.bash` in OpenCode, `prefix_rule` blocks in Codex.
2. **A wrapper.** `workspace policy exec -- <argv>` classified a command and then
   spawned it, so anything outside the wrapper was ungoverned.
3. **A plugin.** `estate-guard.ts` hooked OpenCode's `execute.before` to reject chained
   commands and a hard-coded list of denied prefixes.

The plugin is where the cost became visible. It was written against the OpenCode **v1**
plugin API, and when OpenCode moved to **v2** (`Plugin.define` + `ctx.tool.hook`) the
plugin stopped loading. There was no error and no prompt — the guard was simply absent
while `--auto` auto-approved everything the projections did not explicitly deny. PR #278
exists solely to port it. That is a maintenance tax with a silent failure mode, paid on
a file we do not own, in a language that is not the policy's language.

The plugin also turned out to be **duplicating** the harness. OpenCode's shell tool
already scans a command string with a shell parser and asserts permission against
*every parsed command*, denying the call if any one of them is denied
(`tool/plugin/shell.ts` → `permission.assert({ resources: parsed.commands.map(c => c.resource) })`,
`permission.ts` → `denied()` returns deny if any resource evaluates to deny). So
`git status && rm -rf ~` was never classified by its first token — the tail matched
`rm -rf *` and the whole call was blocked, in auto mode, without our help.

The wrapper had the same shape of problem: it governed only the calls routed through
it, which was nothing. No skill, workflow, launcher, or scheduled task invoked
`policy exec`; it was documented in ADR 0015 and called by no one.

## Decision

1. **The policy is enforced by projection only.** `config/cli-allowlists.json` compiles
   into each harness's native permission configuration. That is the whole enforcement
   surface.
2. **No runtime hooks, wrappers, or interceptors.** We do not install code that runs
   inside a harness, and we do not provide a CLI that classifies a command and then
   delegates execution. `workspace policy check` remains, as a read-only classifier for
   tests and review; it does not stand in the execution path.
3. **Trust the harness's own evaluation.** Chained commands, parsed subcommands, and
   deny precedence are the harness's job. Where a harness cannot express a rule, that is
   a harness bug or an accepted gap, not a reason to interpose.
4. **A rule that cannot be projected does not exist.** If a rule only holds when our
   code runs, it is not a policy — it is a hope.

## Consequences

- **Providers move; projections bend.** A permission file is declarative and versioned
  by the provider. When its shape changes, `workspace policy sync` fails loudly instead
  of the guard going quiet.
- **One language for policy.** Rules are JSON with a rationale, reviewed in a PR. No
  TypeScript, no second classifier with its own edge cases.
- **The trade is explicit.** Under `opencode --auto`, `denied` is the only tier that
  still stops an agent. That makes `denied` the security boundary and it should be
  reviewed as one. Anything left in `ask` is, in practice, approved.
- **Agents may chain again.** Nothing blocks it now, and the harness will catch a denied
  member of the chain. We still ask for one command per call in `AGENTS.md` for
  legibility, but that is a convention, not enforcement.
- **A dead command surface was removed.** `workspace policy exec` and
  `shared_utilities.policy.runner` are gone.

## References

- [ADR 0020: CLI as Projection and Validation Engine, Not JSON CRUD](0020-cli-projection-and-validation-engine-not-json-crud.md)
- [ADR 0015: Non-Destructive AI Assistant Baseline Configuration](0015-non-destructive-ai-assistant-configuration.md)
- [`docs/agents/command-policy.md`](../agents/command-policy.md)
- [`config/cli-allowlists.json`](../../config/cli-allowlists.json)
- OpenCode: `packages/core/src/tool/plugin/shell.ts`, `packages/core/src/permission.ts`
