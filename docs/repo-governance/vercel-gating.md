# Vercel deployments must be gated at creation, not at the build step

[← Repo governance](README.md)

**A Vercel project linked to a repo must not *create* a deployment for a push
that can never serve anything — feature branches and PRs. The only acceptable
gates are a `git.deploymentEnabled` allowlist with a `'**: false'` fallback in
the repo's own `vercel.json`, or deployment creation turned off in project
settings. An `ignoreCommand` is not a gate.**

Vercel's Hobby plan allows 100 deployments *created* per day, account-wide,
and the count is spent at creation. A deployment that the Ignored Build Step
cancels one second later has already burned the budget it took to create it.
Skipping is cheaper than building, but it is not free, and nothing in the
dashboard says otherwise — the deployment list just fills up with "Canceled"
rows that all look like working correctly.

## What went wrong

2026-09-08 through 09-11. The design-system session merged about thirty
pull requests with the ordinary traffic of a PR repo — several pushes per
branch, a re-baselining bot push per visual change. Each push to each PR
branch created one deployment per linked project (there are two), and each
was dutifully canceled by `ignoreCommand`: **83 of the 100 day's budget, 79
of them canceled in under two seconds**, and 65 of `design-system-storybook`'s
own creations in three days came from branches that were never going to serve
anything.

The account hit the limit and Vercel's check began failing pushes with
`upgradeToPro=build-rate-limit`. Underneath that, the storybook project had a
second, quieter failure: *Auto-expose system environment variables* was off,
so the `ignoreCommand` ran with `$VERCEL_GIT_COMMIT_REF` unset, compared `""`
against `"main"`, and skipped **every branch including main** — zero Ready
production deployments since September 10, while the site served 136 stories
against 170 built. It read exactly like quota starvation. It was not.

Three traps, all learned the hard way:

- **`ignoreCommand` runs after creation.** The correct knob is
  `git.deploymentEnabled`, which refuses to create the deployment at all —
  no budget spent, no `Vercel – …` check appearing on PRs.
- **A project setting can make your gating command blind.** Git metadata is
  an opt-in per project; with it off, an otherwise-correct `ignoreCommand`
  skips everything, and the failure is indistinguishable from a quota
  problem from the outside.
- **The flags this audit reads are not the only ones that can lie.** The blog
  project reports no `createDeployments` flag at all — a legacy
  absent-means-default state that reads like "gated" — and still created a
  dozen off-allowlist deployments in the window. That is why `vercel-gating`
  does not stop at configuration: the empirical rules count what was actually
  *created* off the allowed refs, and what actually reached `Ready`, whatever
  the config claims.

`uv run repo-governance vercel-gating --help` lists the rules this audit applies.

## Ownership (`vercel-projects`)

`uv run repo-governance vercel-projects` establishes which repository each
Vercel project in the team deploys from, and checks it against the `vercel`
names declared in `config/managed-projects.json`
([local ports and Vercel names](../estate/url-ownership.md)). It reports; it
never edits the config.

A project's repository comes from its git link. A project deployed from the
CLI has no link, but its newest deployment records the commit it was built
from, and the managed repository holding that commit on GitHub is its source.

| verdict | meaning |
|---|---|
| `ok` | declared under the repository Vercel reports |
| `undeclared` (error) | nobody declares it; the detail names the owner when Vercel shows one |
| `mismatch` (error) | declared under one repository, deployed from another |
| `missing` (error) | declared, but the team has no such project |
| `stale-link` (warn) | the git link names a repository's old owner (after a transfer): relink it |
| `unverified` (warn) | no git link and no commit: ownership rests on the declaration |
