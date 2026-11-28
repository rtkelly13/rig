# Managed Forks

A managed fork is a repo that tracks an upstream we do not own, carries our changes on top, and may
publish them under its own name. The first is `rtkelly-labs/Lake.Parquet`, a fork of
`aloneguid/parquet-dotnet` published to NuGet as `Lake.Parquet`.

## Who owns what

The fork itself is run by **[Oxbow](https://github.com/rtkelly-labs/oxbow)**. It keeps the fork as
upstream's history, untouched, plus a linear stack of recorded changes, one commit per change. It
rebaselines the stack onto newer upstreams, keeps the change records, stages a candidate for CI and
promotes it once CI passes. Until Oxbow's first milestone lands, Lake.Parquet runs the same model
with `eng/lake/Lake.Stack`, the tool Oxbow is being extracted from.

Rig governs the repository around the fork: who owns it, which policy files it carries, its settings
and its ruleset. It also assesses a project before anyone forks it (below). It has no sync, record or
stack commands. Those belong to Oxbow, and
checking them twice would let the two disagree.

## Before forking: `rig fork assess`

`rig fork assess [path]` reads one revision of a clone and reports what forking it asks of you: its
licence and obligations, copyright holders, files under other licences, vendored code, the package
names and brand assets you must not reuse, upstream's contribution policies (AI use, CLA, DCO), who
wrote it and how concentrated that is, and refs holding work the revision does not. Each finding
carries a severity and the responsibility it puts on you. It exits 1 when a finding blocks the fork
(no licence, an unrecognised one, a shallow clone) and 2 on an error.

```bash
git clone --no-single-branch https://github.com/owner/project.git && cd project
rig fork assess --rev "$(git rev-parse HEAD)" > assessment.txt      # the report
rig fork assess --rev <sha> --json --blame --mailmap merged.mailmap   # plus line ownership, as JSON
rig fork assess --rev <sha> --as-of 2026-11-24                       # also: has it gone quiet since?
```

The report is deterministic: it reads files from the revision, never the working tree, takes "now"
from the revision's commit date, and sorts everything ordinally. Only `--refs` (default: every branch;
use `refs/remotes/upstream` when the clone is already a fork) reads live state, so pin it to keep two
runs identical. `--as-of` is the one date not read from the revision: pass today's date and a project
with no commits for `stale_after_days` before it is reported as `activity.stale`. It is an argument,
never the clock, so a recorded report still reproduces. What it looks for and what each finding asks
live in [`config/fork-assessment.json`](../config/fork-assessment.json); the code holds no policy.

Keep the report with the fork's first change record, then hand over to `oxbow init`. A fork run by
Oxbow keeps each change as one recorded commit on top of upstream, so it can be folded back upstream
later (rewritten to upstream's conventions first, and proposed by a person), even when the fork never
intends to diverge for good.

### Limitations

The report states facts a repository holds. These it cannot see, or sees only approximately:

| Limitation | What to do instead |
|---|---|
| **Staleness without `--as-of`.** "Now" is the revision's own date, so a project last touched years ago looks as active as one touched today. | Always pass `--as-of` with today's date when deciding; leave it out only to reproduce an old report. |
| **Anything outside the repository**: registered trademarks, CLAs signed elsewhere, employment and sponsor agreements, issues, pull requests and discussions, who depends on the package. | Search the trademark registers and the package registry; read upstream's issues and discussions while they exist. |
| **Upstream's responsiveness.** Commit counts say who wrote the code, not whether a maintainer reviews outside pull requests or welcomes the change you have in mind. | Read recent pull requests from outsiders before choosing between contributing and forking. |
| **Licences it has no fingerprint for**: new commercial licences, dual licences, and licence files that only point elsewhere ("licensed under Apache 2.0: <url>") are reported unrecognised. | Read them; when they permit forking, add a fingerprint to the configuration. |
| **Files named like licences.** Any root file matching `licence_files`, in any case, is read as one. | Check the evidence paths of `licence.unrecognised`. |
| **Identity merging** is by shared email, or by a normalised name committed under no more than `identity.max_emails_per_name` emails. The tool lists no names: a placeholder, bot or agent name used under more emails merges nothing, but under fewer it merges the people behind it, and two people who share a name merge too. An agent identity (such as a coding agent committing for several people) counts as one contributor. | Pass `--mailmap` with the merges you know, and read the top contributors list. |
| **Whether the work you plan is worth it.** It does not measure a technical gap, such as how far a library is from Native AOT. | Measure that separately, by building with the relevant analyzers. |

## Configuration

Forks are skipped by governance unless a profile names them (`repo_selection.manage_forks`, see
[repo-governance/policy-config.md](repo-governance/policy-config.md)). The profile is
`rtkelly_labs_forks` in `config/repo_policies.json`, and each fork's upstream is a `repo_overrides`
entry:

```json
"Lake.Parquet": {
  "fork": {
    "upstream": "aloneguid/parquet-dotnet",
    "upstream_branch": "master"
  }
}
```

The fork's own branch is `main`, the default `expected_base_branch`, as in Oxbow's ref layout; `upstream_branch`
names upstream's, which keeps upstream's name. The key is the repo's name on GitHub, from origin's URL.

## The rules rig enforces

| Rule | Why |
|---|---|
| **The ruleset protects the fork's own default branch** (`refs/heads/main`), with deletion protection, linear history and the fork's required checks. | The published branch is upstream plus the stack, never a merge, so linear history holds as everywhere else. |
| **It allows non-fast-forward pushes and requires no pull request there.** | The stack tool rebuilds that branch on every rebaseline and pushes it only after the required check (`lake-ci`) passed on the candidate. Those two rules would block exactly that push. |
| **Policies only add files.** The fork profile has no `license`, `pinned_action_shas` or `community_templates`. | Each of those rewrites a file upstream owns, which would conflict on every rebaseline. Our own workflows are pinned by hand instead. |
| **The fork has its own CI workflow and aggregate check** (`lake-ci` for Lake.Parquet). | Upstream's workflow is upstream's to change, and Lake.Parquet's upstream workflow skips docs-only PRs, which would leave a records-only change with no required check to report. |
| **Governance describes origin, never the `upstream` remote.** | `gh repo view` with no argument prefers a remote named `upstream`, so in a fork clone it reports the upstream project. Rig passes origin's URL explicitly. |

The README's credit to upstream, an unchanged upstream `LICENSE` and a record for every change are
the fork's own rules. Oxbow checks them (`oxbow check`).
