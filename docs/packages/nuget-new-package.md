# Publishing a new NuGet package

How to take a .NET repo from "never published" to a package on nuget.org through trusted publishing, run by an
agent, with a person needed only to sign in once and to confirm a policy before it's created. Written from the
October 2026 first publishes of `Iceberg.Net.*` (seven packages, new policy) and Parquet.SourceGenerator
`0.1.0-alpha.1` (existing policy, first publish since the repo moved to `rtkelly-labs`).

[packages.md](README.md) covers the audit and its checks, and the npm runbooks; this is the NuGet procedure.

## What is an API and what isn't

| Step | How | Auth |
|:-----|:----|:-----|
| Is the ID free? Versions, listed state, owners, repository, commit | Public V3 (`https://api.nuget.org/v3/index.json`) | none |
| Create, read, edit or delete a trusted publishing policy | **Website only.** Driven with `agent-browser` (below) | signed-in browser |
| Exchange the GitHub OIDC token for a one-hour API key | `NuGet/login` in the workflow | OIDC |
| Push | `dotnet nuget push … --skip-duplicate` in the workflow | that key |
| Owners, API keys, deprecation, ID prefix reservation | Website (prefixes by email to account@nuget.org) | signed-in browser |

## 1. Check the repo can release

The Parquet.SourceGenerator and Iceberg.NET release workflows are the reference. Before going near nuget.org:

- **The version comes from one place.** Both repos cut a `## [x.y.z] - YYYY-MM-DD` section in `CHANGELOG.md`, and
  `scripts/ParseChangelog.cs --release` turns it into `version`, `tag` and `is_prerelease`. Validate a cut locally
  with `dotnet run scripts/ParseChangelog.cs` before opening the PR.
- **Only the publish job holds `id-token: write`**, runs `NuGet/login` with `user:` set to the nuget.org profile
  (`rtk_13`), and pushes with `--skip-duplicate`. Declare its `environment:` (`release`), then confirm the
  environment has no approval rule if the release must run unattended:
  `gh api repos/<owner>/<repo>/environments/release --jq '[.protection_rules[]?.type]'`.
- **The package metadata names the repo.** `RepositoryUrl`/`PackageProjectUrl` in `Directory.Build.props` must name
  the current owner; nuget.org records it per version, and `audit` warns when it names an old one.
- **Release-time tests must use the version being released.** Iceberg.NET's `tools/package-smoke.sh` asked for
  `0.1.0-*` while the release packed `0.0.1-alpha.1` (the job exports `VERSION`, and MSBuild reads environment
  variables as properties, so `$(Version)` follows it), and the release failed `NU1102` before publishing. Any
  consumer test inside the release should reference `$VERSION` when it is set.

## 2. Check the IDs on nuget.org

```bash
curl -s -o /dev/null -w '%{http_code}\n' https://api.nuget.org/v3-flatcontainer/<id-lowercase>/index.json  # 404 = free
curl -s "https://azuresearch-usnc.nuget.org/query?q=<prefix>&prerelease=true&take=40" | jq -r '.data[].id'  # neighbours
```

A 404 from the flat container is the reliable "does not exist". Search is for spotting someone else's packages under
the same prefix.

## 3. Declare the policy in rig

Add the NuGet fields to each package in `config/managed-projects.json`, as the Parquet.SourceGenerator and
Iceberg.NET entries do: `nuget_owner` (the policy owner; an organisation such as `rtkelly-labs` is normal),
`nuget_user` (`rtk_13`), `environment`, `owners`, `scopes`. Then `rig workspace packages audit --project <name>
--trust` prints the policy to create.

- **A first publish through an organisation's policy makes that organisation the only owner.** Every `Iceberg.Net.*`
  package came out owned by `rtkelly-labs` alone, so its `owners` is `["rtkelly-labs"]`. Packages that existed
  before were owned by `rtk_13` and had the organisation added, so Parquet.SourceGenerator lists both.
- **A policy covers only packages its owner owns.** Check on the package's Manage page, not in search (§6).

## 4. Create the policy in the browser

nuget.org has no API for policies, so `agent-browser` drives the page. Use the **dev** profile (nuget.org is
development tooling; see [agent-browser/preferences.md](../agent-browser/preferences.md)) and one named session.
agent-browser 0.31 takes commands directly (`agent-browser open …`); there is no `run` subcommand. In zsh, wrap it
in a function rather than a string variable, which zsh does not word-split:

```bash
ab() { agent-browser --session fill-nuget-trust "$@"; }
ab --profile ~/.agent-browser-profiles/dev --headed open https://www.nuget.org/account/trustedpublishing
ab get url   # .../users/account/LogOn?... means not signed in
```

**Sign-in is the person's step.** If the URL is the sign-in page, ask them to sign in as `rtk_13` in the headed
window (Microsoft account, passkey). The profile stays signed in afterwards.

**Read before writing.** Each existing policy is an `h3`, and `main`'s text reads:

```text
Iceberg.NET
 Active
Package owner: rtkelly-labs
Scopes: Push new packages and package versions
Glob Patterns and Packages: Iceberg.Net.*
Publisher: GitHubActions Repository Owner: rtkelly-labs #330611343 … Repository: Iceberg.NET #1432438034 …
 Workflow: release.yml Environment: release
```

`#<id>` means the IDs are locked. Compare with what rig declares; create only what is missing.

**Fill by accessible name.** Refs (`@e41`) change with every snapshot, so look each one up by name from a fresh
`ab snapshot -i`. Click `button "Create" [expanded=false]` to open the form, then:

| Field (accessible name) | Value | Note |
|:------------------------|:------|:-----|
| `textbox "Policy Name *"` | the repo name | |
| `combobox "Package Owner *"` | `rtkelly-labs` | Knockout-bound with empty option values: `ab select <ref> "rtkelly-labs"` selects by text. Confirm it took with `ab eval` on the `select`'s selected text. The scope controls stay disabled until an owner is chosen |
| `combobox "CI/CD Provider *"` | GitHub Actions | the default |
| `textbox "Repository Owner *"` | `rtkelly-labs` | |
| `textbox "Repository *"` | the repo | |
| `textbox "Workflow File *"` | `release.yml` | file name only |
| `textbox "Environment"` | `release` | binds the policy to the protected job |
| `checkbox "Push"` + `radio "Push new packages and package versions"` | on | selected automatically once an owner is set. A first publish needs "new packages" |
| `textbox "Glob Patterns and Packages …"` | e.g. `Iceberg.Net.*` | narrower than `*`, which allows anything the owner owns |

**Confirm, then submit.** Take a screenshot (`ab screenshot --full temp/nuget-trust/<repo>.png`), show the values,
and get the person's yes. Then click the form's own `button "Create"` (the one without `expanded=`; the section
toggle has the same name).

**Judge success by reading back, not by messages.** After a submit the page shows a fresh blank form, and its
validation text, *"Package owner should be provided."*, belongs to that blank form, not to the policy just saved. Read
the `h3` list and the new entry's text: the Iceberg.NET policy came back `Active` with both IDs locked at once.

**Expect the session to reset.** While waiting for the person's answer the session went to `about:blank` and the
filled form was gone. Before submitting, re-open the page, check the policy still doesn't exist, refill, verify, then
submit.

Edit can change the name, workflow, environment, scopes and globs, but not the owner, provider or repository. After a
repo transfer, create a new policy, publish, then delete the old one.

## 5. Release

1. Open the changelog-cut PR. Resolve review threads (CodeRabbit asks for wording changes; the rulesets require
   resolved threads), and wait for CI.
2. Dispatch a **dry run** from the branch (`gh workflow run release.yml --ref <branch> -f dry_run=true`, or
   `dry_run=true` is the default). It builds, packs and runs the consumer tests without publishing.
3. Merge. Iceberg.NET uses a merge queue: `gh pr merge` prints "The merge strategy for main is set by the merge
   queue" and enqueues it.
4. Wait for `main`'s CI on the merge commit. The release's verify job refuses a commit without a successful run
   (`ci` in Iceberg.NET, `build` in Parquet.SourceGenerator).
5. Dispatch the release from `main` with `dry_run=false`.

## 6. Verify

- **The push log** shows `Created https://www.nuget.org/api/v2/package/` and `Your package was pushed.` per package.
- **Availability lags the push** by a minute or more per package while nuget.org validates it.
  `Iceberg.Net.Puffin` returned `BlobNotFound` for about two minutes after the others appeared. Poll the flat
  container for the `.nupkg` until it returns 200; don't treat the gap as a failed push.
- **Search lags further and its `owners` are stale.** Search reindexes only on a publish. After the organisation was
  added as an owner, search still listed only `rtk_13` until `0.1.0-alpha.1` was published. Read current owners
  from the package's Manage page (`button "Owners"`, then `Remove <owner> as owner of <id>` for each).
- **The catalog records the repository and commit** per version. After Parquet.SourceGenerator `0.1.0-alpha.1` it
  named `rtkelly-labs/Parquet.SourceGenerator` at the release commit, clearing the audit's old-owner warning.

## When the SDK won't install

On 2026-10-25 every workflow using `actions/setup-dotnet` with a floating version (`global.json` `10.0.100` +
`rollForward: latestFeature`, or a `10.0.x` channel) failed for about 90 minutes. Microsoft's out-of-band security
release pointed the `aka.ms` link for the latest 10.0 SDK at `dotnetorigin.blob.core.windows.net`, which answered
`409 Public access is not permitted on this storage account`, while `builds.dotnet.microsoft.com` served the same
file. It cleared on its own at 20:31 UTC.

```bash
curl -s -I -L https://aka.ms/dotnet/10.0/dotnet-sdk-linux-x64.tar.gz | grep -i -E '^HTTP|^location'
```

A 409 there is the fault; a redirect to `builds.dotnet.microsoft.com` and a 200 is the fix. The logs show it as
`dotnet-install: Attempting to download using aka.ms link …`. An **exact** version (`dotnet-version: 10.0.403`)
skips `aka.ms` and downloads from `builds.dotnet.microsoft.com`, which is the way around it without waiting; the
cost is bumping the pin by hand.
