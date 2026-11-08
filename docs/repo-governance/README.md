# Repo Governance

`repo-governance` (`src/rig/repo_governance/`) audits and enforces conventions across
the personal repositories and the `rtkelly-labs` organisation. Each command's `--help` carries its flags,
findings, defaults and config keys. These guides hold only what `--help` can't: the reasoning behind
the parts that have already gone wrong once.

For the estate-wide hub covering foundational repository conventions, script containment and merge-queue
invariants, see [Open Source Project Expectations](../open-source/open-source-project-expectations.md).

## Typical flow

`audit`, then `check-names`, then `enforce --dry-run`, then `enforce`. `uv run repo-governance --help`
lists every command.

`plan` is the desired → plan → apply shape ([target architecture](../target-architecture.md)) for the
GitHub-side state: repo settings, the ruleset, environments, labels and Pages. It reads only; `--apply`
makes the planned writes and `--json` emits the plan. It exits 0 when every repo matches its policy, 1 on
drift or a refused write (the check-name guard), 2 when GitHub could not be read or a write failed. Each
resource is a `Resource` in `src/rig/repo_governance/resource.py`, reading and writing through the
`GitHubApi` port in `rig.proc`, so tests drive it with `tests/fakes/github_rest.py`.

## Guides

| Guide | When to use |
|:------|:------------|
| [required-checks.md](required-checks.md) | Naming a required check; adding a check to `required_checks` in `config/repo_policies.json`; the smoke-then-full `ready-to-merge` gate |
| [action-pinning.md](action-pinning.md) | SHA-pinning workflow actions with pinact, Dependabot and zizmor, and what replaced rig's own pinner |
| [rulesets.md](rulesets.md) | What the ruleset payload declares, and how `enforce` finds and repairs a live ruleset that has drifted |
| [policy-config.md](policy-config.md) | `config/repo_policies.json`, which repos governance touches, and shared `.gitignore` entries |
| [worktree-state.md](worktree-state.md) | A feature needs to remember something per worktree: the `temp/.rig/` standard and how the gitignore audit enforces it |
| [vercel-gating.md](vercel-gating.md) | Keeping Vercel from creating deployments for branch pushes |
| [filesystem-and-filename-governance.md](filesystem-and-filename-governance.md) | ls-lint: closed allowlists, the script language policy, and directory casing |
| [../forks.md](../forks.md) | Managed forks: what rig governs and what Oxbow owns, and why a fork's ruleset allows rebaseline pushes |
| [org-and-apps.md](org-and-apps.md) | The `rtkelly-labs` organisation, and the GitHub Apps installed across both accounts |

Publishing packages to npm and NuGet, including setting up a new package, is in
[../packages.md](../packages/README.md). `AGENTS.md` governance is in
[../agents-md-governance.md](../agents/agents-md-governance.md).

## Before changing a repo's rules

- **Workflows first, config second.** A required check that no workflow reports blocks every PR in the repo.
  Merge the workflow, add the name to the repo's `required_checks` in `config/repo_policies.json`, then run `enforce`.
- **Dry-run first.** `enforce --dry-run` lists any drift in the live ruleset before it is overwritten.
- **A skipped job is not a gate.** GitHub reports a job skipped by `if:` as Success, and a required check
  that reports Success merges. See [required-checks.md](required-checks.md).
