# Required checks and the merge gate

[← Repo governance](README.md)

## Required check names

A required status check is a contract between a repository's workflow files and its branch-protection
ruleset, and the two halves live in different places. The name is a job's `name:` in a workflow
(falling back to its job id); the requirement is a string in a `required_checks` list in
`config/repo_policies.json` (a repo override, or the repo's profile). Nothing in GitHub connects them.

A required check that no job reports does not degrade gracefully. The ruleset applies with
`enforcement: active`, so every pull request in that repo blocks forever on *"N of N required status
checks are expected"* — naming nothing, with no indication of which check it is waiting for.

### The rule

**Check names are lowercase `snake_case`, at most two words, and preferably a word from
`PREFERRED_CHECK_NAMES`** in `check_names.py`.

The reason is that a descriptive name cannot also be a stable contract. A name that lists what a job
does has to change when the job changes — and the string is what branch protection matches on, so
changing it breaks the gate.

Add to the lexicon rather than inventing a synonym. `tests`, `unit-tests` and `Run Tests` all mean
`test`, and three spellings of one idea is how the config stops being checkable.

### What went wrong

`design-system` required `Build, Typecheck, Storybook & Visual Regression`. Splitting its single CI
job into three parallel jobs plus an aggregate added "Unit Tests" to the middle of the aggregate's
name, so the check became `Build, Typecheck, Unit Tests, Storybook & Visual Regression`. The
required-checks map went on requiring the old spelling, and every PR failed `verify-pr-checks` on a missing check while
all seven of its checks were green. The job even carried a comment saying not to rename it; the
comment did not survive contact, because the name doubled as a job description.

It now reports one aggregate job named `ci`, which names nothing and so has nothing to go stale.
The three jobs beneath it report as `lint`, `test` and `visual` and can be renamed freely.

Writing the guard immediately surfaced two more repos in the same state, both invisible because they
matched no key at all and silently inherited the fallback names:

| Repo | Map required | Workflows declare |
| --- | --- | --- |
| `Resultful/Resultful` | *fallback* — `Lint & Typecheck`, `Tests & Coverage` | `build` |
| `rtkelly13/Parquet.TypeProvider` | *fallback* — `Lint & Typecheck`, `Tests & Coverage` | `build`, `pr-title` |

`Resultful` was keyed under `Resultfinder/Resultful`, an owner that does not exist. Both were one
`enforce --all` from being permanently unmergeable. Enforcement now uses only the check names
`config/repo_policies.json` declares, and only through the ruleset.

That map was a hard-coded `REPO_REQUIRED_CHECKS_MAP` in `github_rulesets.py` that overrode the config.
It kept stale keys (`rtkelly13/shared-utilities` after the rename to `rig`) while the config claimed
`build`/`pr-title` for `rig`, so the two disagreed silently. The config is now the only source. Unregistered repositories
still require resolved review threads, with no invented CI requirements.

## Smoke on every push, full regression before merge

`design-system` splits CI into two tiers, and the pattern fits any repo whose full suite is too slow or
costly to run on every push:

| Tier | Runs | Reports |
| --- | --- | --- |
| smoke | every push (`ci.yml` on `pull_request`) | the job `ci` |
| full | once the PR carries `ready-to-merge`, then on every push while it stays | the commit status `regression` |

**The gate must be a commit status, not a job.** GitHub reports a job skipped by its `if:` as
**Success**, and a required check that reports Success lets the PR merge. So a "skip until labelled" job
is no gate at all. A workflow that never starts is different: its check stays *Expected* and blocks. A
`pending` commit status blocks, shows yellow rather than red, and carries the reason in its description
(*"Add the ready-to-merge label to run full regression"*).

The parts of `design-system`'s `regression.yml` that a copy needs:

- **A separate workflow for label events.** `ci.yml`'s concurrency cancels in-progress PR runs. If label
  events reached it, adding any unrelated label would cancel the smoke suite. The `full` job also uses
  its own job-level concurrency group, so a label event that skips it can't cancel a run in progress.
- **Post on `pull_request.head.sha`**, not `github.sha`, which is the temporary merge commit GitHub
  creates for each `pull_request` event.
- **Check the label again before reporting a pass.** Removing the label posts pending but doesn't cancel
  the running full job, which would otherwise finish and turn the gate green.
- **Name the context in a workflow-level `env.STATUS_CONTEXT`.** `check-names` reads it to know the
  required status exists, since a status has no job name to read.
- **Any cached verdict records full runs only.** If a smoke run could record one, a later full run would
  reuse it and skip the scan it exists for.
- **Commits pushed with `GITHUB_TOKEN`** (snapshot bots) start no workflow, so they get no status.
  Remove and re-add the label to run the gate on them.
- **Fork and Dependabot PRs** get a read-only token, so they can't post the status. If a repo takes
  them, write the status from a separate trusted `workflow_run` workflow.

Order matters: merge the workflows, then add the context to the repo's `required_checks` in
`config/repo_policies.json`, then run
`enforce`. Requiring a check before any workflow posts it blocks every PR.
