# Landing a queue of pull requests

When several PRs are ready at once, the constraint that decides everything is
**`strict: true`** on the branch-protection ruleset: a branch must be up to date
with `main` to merge.

Check it before planning:

```bash
gh api repos/OWNER/REPO/branches/main/protection \
  --jq '{checks: .required_status_checks.contexts, strict: .required_status_checks.strict}'
```

## Strict mode makes landing sequential

Every merge makes every other open PR stale. So the cycle per PR is:

```
rebase onto main  →  push  →  wait for CI  →  merge
```

There is no batching around it. Ten ready PRs is ten CI cycles, in series. Plan
for that rather than discovering it at PR three — and if it matters, the only
real lever is **fewer, larger PRs**, which trades away the review granularity
that made them small in the first place.

Two things do help:

- **Interleave repositories.** While one repo's CI runs, rebase and merge in
  another. Roughly halves wall clock when work spans two repos.
- **Order by what unblocks the most.** A PR that other PRs conflict with should
  go first; a PR that *adds a gate* should go early, because until it lands,
  every new merge adds more for it to catch later.

## Required check names are a contract

Branch protection matches a check by **name string**. A workflow that renames a
job stops satisfying the requirement, and every PR whose last run predates the
rename is unmergeable regardless of how green it looks — it is publishing a
different string.

The symptom is a PR that appears entirely green and still will not merge. The
fix is to rebase so CI re-runs under current names. See
[`repo-governance/required-checks.md`](../repo-governance/required-checks.md) for the naming rules themselves.

## The aggregate-gate trap

Where a repo funnels many jobs into one required "aggregate" check, read how
that aggregate decides. A common shape is each job exporting a step outcome:

```yaml
outputs:
  outcome: ${{ steps.lint.outcome }}
```

If the job dies *before* that step runs — a failed dependency install, an
unreachable registry — the output is never set. An aggregate that treats an
empty value as "skipped" then passes, and since the aggregate is the only
required check, **a run whose own conclusion is `failure` can hand `main` a
green light.**

Guard against it by checking the job `result` as well as the step `outcome`, and
forgiving only a genuine `skipped`:

```yaml
for check in LOCKFILE LINT TYPECHECK UNIT BUILD E2E; do
  r_var="${check}_R"; r="${!r_var}"
  if [ "$r" = "failure" ] || [ "$r" = "cancelled" ]; then
    echo "::error::${check} job ${r} before reporting an outcome"
    failed=true
  fi
done
```

Until that is in place, verify by hand: a green required check is only
trustworthy alongside `gh run view <id> --json conclusion`.

## Caches hide broken installs

If CI caches the package store keyed on the lockfile, a branch whose lockfile
matches a warm cache **never contacts a registry at all**. Only a branch that
changes dependencies actually resolves.

That is the exact inversion you do not want: the PRs most needing their install
verified are the only ones that attempt it, and a broken registry configuration
can sit undetected for months because everything else was served from cache.

## Deployment previews are rate limited

Vercel answers `Deployment rate limited — retry in 24 hours` after a heavy day
of pushes. It is usually **not** a required check, so it shows as `UNSTABLE`
without blocking a merge — but previews are gone for a day. Worth pacing
force-pushes across many branches, or accepting the loss knowingly.
