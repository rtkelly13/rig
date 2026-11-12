# Estate command policy

One policy decides what an AI agent may run on this machine, and every harness is a
projection of it.

- **Source of truth**: [`config/cli-allowlists.json`](../../config/cli-allowlists.json)
- **Classifier**: [`src/Rig.Core/Policy/CommandPolicy.cs`](../../src/Rig.Core/Policy/CommandPolicy.cs)
- **Projections**: [`src/Rig.Core/Policy/NativeProjection.cs`](../../src/Rig.Core/Policy/NativeProjection.cs)

Nothing here is hand-authored per harness. A rule added to the JSON reaches Claude
Code, Antigravity, OpenCode and Codex the next time `rig policy sync` runs.
Hand-adding rules to `~/.config/opencode/opencode.jsonc` is how the policy drifted
before; those strays were folded back in by #193.

There is no runtime hook in any harness. See
[ADR 0021](../adr/0021-projections-not-runtime-interposition.md) for why.

## Commands

```bash
rig policy check -- "<command>"         # classify one command
rig policy sync                         # project into every harness config
rig policy sync --dry-run               # show the diff without writing
```

## Risk tiers

| Tier | Meaning | Claude | Antigravity | OpenCode | Codex |
|---|---|---|---|---|---|
| `safe` | Read-only inspection and validation | `Bash(...)` allow | `command(...)` allow | `allow` | `allow` |
| `local` | Mutates the local checkout (builds, format, watchers) | unlisted → prompt | unlisted → prompt | `ask` | `prompt` |
| `external` | Touches remote infrastructure or registries | unlisted → prompt | unlisted → prompt | `ask` | `prompt` |
| `denied` | Destroys work or escapes the working directory | `deny` | `deny` | `deny` | `forbidden` |

Antigravity uses `command(...)` rules only; the legacy `unsandboxed(...)` form is
being retired (#170).

Matching is `fnmatch` over the **whole command string**, and the first matching rule
wins, so a `denied` prefix always beats the `safe` family it sits inside.

### Patterns carry a trailing space deliberately

`"cat *"` matches `cat file`, not `catalog`. A bare `"cat*"` would permit `catalog`,
`find * -delete` and `sort -o out.txt` — which is why the read-only families are
written with a space and why each mutating flag under them is denied explicitly:

| Family | Denied member | Why |
|---|---|---|
| `find` | `-delete`, `-exec`, `-ok` | turns a read-only search into deletion or arbitrary execution |
| `sort` | `-o` | writes a file; stdout-only sorts stay safe |
| `git reflog` | `expire`, `delete` | destroys local recovery history |
| `agent-browser` | `eval`, `connect` | runs JavaScript in the live session; attaches CDP to any port |

Machine-specific entries, such as the absolute-path `uv` denials, stay in the local
`opencode.jsonc` and never enter estate policy: the point of the file is that it is
portable across machines.

## What is safe, and what is deliberately not

Safe is read-only and idempotent — `rig help`, `uv run pytest`, git inspection
(`status`, `diff`, `log`, `show`, `rev-parse`, `cat-file`, `for-each-ref`, …),
`gh` reads (`pr view`, `run view`, `search`, …), and the Unix text and process
inspection tools (`cat`, `grep`, `jq`, `du`, `ps`, `lsof`, …).

Merge steps are the one group of writes in the safe tier, because an agent runs them only
once the user has asked to merge, and branch protection still gates every one of them:
`gh pr ready *`, `gh pr merge * --auto --squash`, `rig gh pr land *` (the same merge, then
`rig gh pr wait`, which is read-only and safe on its own), `gh pr merge * --disable-auto` (requeues a
PR in a label-driven merge queue), `gh stack merge * --yes*` (a method flag such as `--squash`
may follow `--yes`) and `gh run rerun *`. `gh stack sync` sits beside `git push`: it rebases
each stack layer onto its parent and pushes with `--force-with-lease --atomic`, and it never
opens PRs. Its `--prune` flag deletes local branches, so it is denied like `git branch -D`. They are
estate-wide rather than per-repo: the policy projects into the user-level Claude settings,
and a repo you do not own (such as a work one) cannot carry them in its own `.claude/`.
The interior `*` is projected to Claude Code verbatim; the prefix-only surfaces (Codex,
Antigravity, OpenCode) skip those patterns and keep prompting. A skipped *deny* would otherwise
turn the allow it narrows into a hole (`gh stack sync *` without `gh stack sync *--prune*`), so
those surfaces also hold back every allow such a deny overlaps: `gh stack sync`, `find *`,
`sort *` and `agent-browser *` prompt there. On Codex and Antigravity every rule is a prefix, so
even the exact `gh stack sync` is held back; OpenCode keeps it.

Everything that mutates stays unlisted, so the harness prompts:

| Family | Tools | Why not auto-approved |
|---|---|---|
| File mutation | `mkdir`, `cp`, `mv`, `ln`, `touch`, `sed`, `xattr` | bypasses the agent's tracked edits and diff review; `sed -i` can corrupt a file irreversibly |
| Git mutation | `commit`, `add`, `checkout`, `switch`, `restore`, `merge`, `rebase`, `cherry-pick`, `rm`, `mv` | discards unstaged edits or records changes without review |
| Dependencies | `pnpm install`, `npm install`, `pnpm add` | fetches unvetted code and rewrites lockfiles |
| Execution | `python`, `node`, `dotnet run`, arbitrary binaries | arbitrary code outside a tested runner; use `uv run pytest` |
| Desktop | `open`, `code`, `osascript`, `defaults` | GUI and OS preference changes with user privileges |
| Network egress | `curl`, `dig` | exfiltration and remote script retrieval |
| System storage | `docker system`, `brew cleanup`, `diskutil`, `hdiutil` | system-wide destruction |

## Denied outright

| Pattern | Rationale |
|---|---|
| `git reset --hard*` | Discards tracked changes. |
| `git clean -fd*` | Deletes untracked files. |
| `git branch -D*` | Force-deletes a branch; `estate tidy` owns branch removal. |
| `rm -rf *` | Broad deletion. |
| `git -C *`, `pnpm --dir/-C` | Directory-escape flags. Set the working directory (`Cwd`) and run the command directly; where the tool has no working-directory parameter, use `cd <dir> && <command>`. |
| `pnpm --filter/-F` | Cross-package flags that can run scripts in workspaces you didn't target. Not projected to Claude (`exclude_systems: ["claude"]`): its auto-mode classifier vets the target, and team docs use single-package filters such as `pnpm --filter rights-app …`. |
| `gh auth refresh/logout/token*`, `gh repo delete*` | Credential and repository destruction. |
| `gh pr merge *--admin*` | Bypasses branch protection. It also wins over the auto-merge allow rules above. |
| `gh stack sync *--prune*` | Deletes local branches; `estate tidy` owns branch removal. |

## Chaining and substitution

Agents run **one command per call**. That is a rule about how commands are written,
not something we enforce by interposing on execution: every harness that matters now
parses a command into its constituent parts and evaluates each one before running
anything. OpenCode, for example, scans the command with a shell parser and asserts
permission against every parsed command, denying the call if **any** part is denied.
So `git status && rm -rf ~` is not classified by its first token — the `rm -rf` tail
matches the `deny` rule and the whole call is blocked, `--auto` or not.

The one permitted prefix is a leading `cd <dir> &&`. A shell tool with no
working-directory parameter has no other way to scope a command once the
directory-escape flags are denied. It still counts as one command: `cd` has no
effect beyond the call, and the command after it is evaluated on its own.
The same goes for suggested commands that tools print, such as fix hints and
decision feeds. They print `cd <dir> && git …`, never `git -C <dir> …`, so an
agent that follows a hint does not run into the deny rule.

Keeping the rule matters for a different reason: a chain hides intent from a reviewer,
defeats prefix-shaped approvals, and turns one unreadable call into five effects. It is
written into `AGENTS.md` and the skills for that reason.

Because `--auto` auto-approves the `ask` tier, `denied` is the only tier that still
stops an agent. That is the trade: fewer interruptions, at the cost of the prompt
being gone from `local` and `external`. Review `denied` as the security boundary, not
the long tail of `ask`.

## Changing it

1. Add or amend a rule in `config/cli-allowlists.json` with a rationale. To keep a
   rule out of particular agents, add `"exclude_systems": ["claude"]` (any of
   `claude`, `codex`, `opencode` or `antigravity`). Sync then also removes a copy an
   earlier sync wrote, so un-scoping a rule takes effect without hand-editing.
2. `rig policy sync --dry-run` to see the projection diff.
3. `rig policy sync`.
4. `dotnet test --project tests/Rig.Core.Tests -- --filter-namespace Rig.Core.Tests.Policy`.

A misshapen rule fails closed: the loader raises rather than dropping it, so a
patternless entry breaks `rig status --ci` instead of silently widening the
policy (`A_misshapen_policy_fails_closed`).
