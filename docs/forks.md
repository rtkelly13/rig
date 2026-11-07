# Managed Forks

A managed fork is a repo that tracks an upstream we do not own, carries our changes on top, and may
publish them under its own name. The first is `rtkelly-labs/parquet-dotnet`, a fork of
`aloneguid/parquet-dotnet` published to NuGet as `Lake.Parquet`.

## Who owns what

The fork itself is run by **[Oxbow](https://github.com/rtkelly-labs/oxbow)**. It keeps the fork as
upstream's history, untouched, plus a linear stack of recorded changes, one commit per change. It
rebaselines the stack onto newer upstreams, keeps the change records, stages a candidate for CI and
promotes it once CI passes. Until Oxbow's first milestone lands, parquet-dotnet runs the same model
with `eng/lake/Lake.Stack`, the tool Oxbow is being extracted from.

Rig governs only the repository around the fork: who owns it, which policy files it carries, its
settings and its ruleset. It has no sync, record or stack commands. Those belong to Oxbow, and
checking them twice would let the two disagree.

## Configuration

Forks are skipped by governance unless a profile names them (`repo_selection.manage_forks`, see
[repo-governance/policy-config.md](repo-governance/policy-config.md)). The profile is
`rtkelly_labs_forks` in `config/repo_policies.json`, and each fork's upstream is a `repo_overrides`
entry:

```json
"parquet-dotnet": {
  "expected_base_branch": "master",
  "fork": {
    "upstream": "aloneguid/parquet-dotnet",
    "upstream_branch": "master"
  }
}
```

`expected_base_branch` keeps upstream's default branch, so a rebaseline never fights over a rename.

## The rules rig enforces

| Rule | Why |
|---|---|
| **The ruleset protects the fork's own default branch** (`refs/heads/master`), with deletion protection, linear history and the fork's required checks. | The published branch is upstream plus the stack, never a merge, so linear history holds as everywhere else. |
| **It allows non-fast-forward pushes and requires no pull request there.** | The stack tool rebuilds that branch on every rebaseline and pushes it only after the required check (`lake-ci`) passed on the candidate. Those two rules would block exactly that push. |
| **Policies only add files.** The fork profile has no `license`, `pinned_action_shas` or `community_templates`. | Each of those rewrites a file upstream owns, which would conflict on every rebaseline. Our own workflows are pinned by hand instead. |
| **The fork has its own CI workflow and aggregate check** (`lake-ci` for parquet-dotnet). | Upstream's workflow is upstream's to change, and parquet-dotnet's skips docs-only PRs, which would leave a records-only change with no required check to report. |
| **Governance describes origin, never the `upstream` remote.** | `gh repo view` with no argument prefers a remote named `upstream`, so in a fork clone it reports the upstream project. `rig.gh_origin` passes origin's URL explicitly. |

The README's credit to upstream, an unchanged upstream `LICENSE` and a record for every change are
the fork's own rules. Oxbow checks them (`oxbow check`).
