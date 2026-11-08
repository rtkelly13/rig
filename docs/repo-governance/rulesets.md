# Rulesets

[← Repo governance](README.md)

## Which branch, and which rules

The ruleset covers the repo's `expected_base_branch` (`main` unless a profile or override says
otherwise), not a hardcoded `refs/heads/main`: a fork keeps upstream's `master`, and a ruleset on a
branch the repo does not have protects nothing. A managed fork also drops `non_fast_forward` and
`pull_request`, because its stack tool rebuilds the default branch on every rebaseline. It keeps
deletion protection, linear history and its required checks. See [../forks.md](../forks.md).

## Ruleset payloads must declare every parameter

**GitHub fills in its own default for any rule parameter the payload omits.** An undeclared
parameter is therefore not "left alone" — it is silently reset on every `enforce`. So
`build_ruleset_payload` states every parameter each rule accepts, even where the intended value
matches the current GitHub default.

### What went wrong

The `pull_request` rule accepts six parameters and the payload declared five. The missing one was
`require_extra_approval_for_unattributed_changes`, and it blocked `design-system#69` outright.

`Co-Authored-By` names an address that is not a GitHub account, so an agent-authored commit reads as
having an unattributed co-author, and that parameter demands an extra approving review. The
combination is what makes it fatal rather than inconvenient:

- `required_approving_review_count: 0` — so there is no reviewer in the loop to supply one
- the author cannot self-approve their own PR
- `bypass_actors: []` and `current_user_can_bypass: "never"` — so `--admin` has nothing to bypass with

The PR was unmergeable by anyone, with every check green, and nothing anywhere naming the cause.
`gh pr merge` reported only *"the base branch policy prohibits the merge."*

It is declared `False`: these repos gate on checks rather than review headcount, and co-author
attribution is not the control doing the work. Declaring it is the point either way — flipping it in
the GitHub UI alone would last exactly until the next `enforce --all` overwrote it, which is the same
failure mode as a check name that drifts.

`test_no_pull_request_parameter_is_left_to_a_github_default` guards the general case, so the next
parameter GitHub adds has to be stated rather than inherited.

### A ruleset that exists is not a ruleset that matches

Until #343, `align_github_ruleset` returned `already_active` as soon as a ruleset with the right name
was active, so its `PUT` path was unreachable: a repo got the payload once, on creation, and never
again. Every later change to `build_ruleset_payload` or the required-checks map stopped at the
repos that already had a ruleset, while `enforce` reported them aligned.

`design-system` ran like that for months: no `required_status_checks` (not even `ci`), no
`required_linear_history`, and `require_extra_approval_for_unattributed_changes: true` (the rule above).
It cost two things. A component that failed accessibility on `main` merged green, and the release train
held production for nine days. And the owner's own PRs could not be merged without bypassing the rule.

`ruleset_drift()` now decodes both the declared payload and the live ruleset through one typed shape and
compares them rule by rule, ignoring parameters GitHub returns but the payload never sets
(`allowed_merge_methods`, `integration_id`). `enforce --dry-run` prints the difference:

```text
Ruleset drift on rtkelly-labs/design-system (dry run — would update):
   - missing rule required_linear_history
   - missing rule required_status_checks
```

When a repo's rules change, run `enforce --dry-run` before `enforce`. Any repo whose ruleset drifted gets
corrected on its next run, and the first sight of that should be a list, not a surprise.

To apply only the ruleset, without the rest of `enforce` (repo settings, local files):

```bash
uv run python -c "from rig.repo_governance.github_rulesets import align_github_ruleset as a; \
print(a('<owner/repo>', dry_run=True))"   # then again without dry_run
```

## Review threads must be resolved before merge

The managed ruleset sets `required_review_thread_resolution: true` on its `pull_request` rule, so a
PR cannot merge while any review conversation, human or bot, is unresolved.

- **`status` checks it.** The GitHub Ruleset column reads the ruleset's own rules, not only whether it
  exists. A ruleset that is disabled, has no `pull_request` rule, or no longer requires resolved
  threads shows as `DRIFTED: …` and counts against overall alignment. Before this, a ruleset that
  merely existed passed.
- **`enforce` repairs it in place.** A drifted ruleset is updated by id with `PUT`, and that includes a
  disabled one, which used to get a duplicate created alongside it. A missing ruleset is created.
- **Unregistered repos still get it.** A repo with no registered required checks gets the ruleset
  without a `required_status_checks` rule, instead of this project's own check names, which no job of
  theirs reports and which would block every PR. So resolved threads can be required everywhere
  without guessing at check names.
- **Private repos on GitHub Free can't have it.** GitHub offers neither rulesets nor branch protection
  there. An upgrade error is reported as `N/A (Private Repo)`. An empty ruleset list only means
  `MISSING`; it does not establish the account's plan or whether creating a rule is supported.
  The CLI preflight still works when native protection is unavailable.

`status` and `audit` compare all declared rules, enforcement, branch coverage and bypass actors.
A ruleset aimed at another branch or allowing actors to bypass it is drift too. `enforce` checks
registered CI names before writing the ruleset.

`repo-governance verify-pr-checks <number-or-branch-or-URL> --repo <owner/repo>` is the merge
preflight. It asks GitHub rather than keeping its own list: `gh pr checks --required` for the checks
the ruleset requires (still running blocks), then the PR's `mergeStateStatus` (only `CLEAN`,
`HAS_HOOKS` and `UNSTABLE` pass; `BLOCKED`, `BEHIND`, `DIRTY` and `DRAFT` do not). It also reads every
page of review threads. Unresolved human, bot and outdated threads block; missing data, GraphQL
errors and broken pagination block as well. `pr-triage --merge-ready` uses the same check immediately
before each merge, including on private repositories. This is a CLI safeguard; it cannot enforce
merges through GitHub's UI when native branch protection is unavailable.

General PR timeline comments have no resolved state and are outside GitHub's review-thread gate.
Review findings that should block merging must be posted as review threads.

The October 2026 rollout verified native conversation resolution on all 18 enabled, non-archived
managed repositories, including private `rig`, `obsidian-vault` and `hearth`. This supersedes the
private-repository exclusions recorded in the original rollout. The two demonstration repositories
(`MySQLEFConcurrencyBug` and `AWSBatch-MapReduce`) received the missing rulesets too. Only the
conversation rule was added to the vault and hearth; their workflows were not assigned CI check names.

## Rulesets only, no classic branch protection

`enforce` used to write classic branch protection (`PUT .../branches/main/protection`) as well as the
ruleset, and `status`/`audit` had a Branch Protection column that read both. Only the ruleset went
through the check-name guard: the classic payload carried the same required checks, so the guard
covered one of the two paths that could block every PR. Classic protection, its column, the
`--allow-force-push` flags and its "Upgrade to GitHub Pro" special case are gone. Rulesets are the one
mechanism, and `ruleset_drift` is the one comparison.

Classic protection left in place on a repo is not removed by `enforce`. GitHub applies both, so a
leftover classic rule still binds; delete it in the repo's branch settings once the ruleset is active.
