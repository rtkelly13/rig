# Published packages and deployed artifacts

`uv run workspace packages` (alias `uv run workspace artifacts`) tracks every npm and NuGet package the estate publishes,
surfacing local manifest versions alongside both latest deployed releases and latest stable releases on public registries.

`uv run workspace packages audit` performs deep governance validation against the publishing GitHub repositories,
verifying provenance, OIDC trust configuration, and workflow health.

`uv run workspace packages dashboard` adds the release view: each package's registry version next to its
matching git tag and any `[Unreleased]` changelog entries. See [dashboard.md](dashboard.md).

The bindings are declared in `config/managed-projects.json`, under each project's
`packages` list (ADR 0020: edit the JSON, let the CLI validate).

```json
"packages": [
  {
    "registry": "npm",
    "name": "@rtkelly-labs/design-system",
    "manifest": "packages/design-system/package.json",
    "workflow": "publish-package.yml",
    "auth": "oidc"
  }
]
```

`registry` is `npm` or `nuget`. `manifest` is the file that declares the repository URL: `package.json`, or the
`.props`/`.csproj` holding `<RepositoryUrl>`. `workflow` is the file name under `.github/workflows/`. `auth` is
`oidc` (trusted publishing, the default) or `token`. An optional `environment` names the GitHub environment the
trust config requires.

A NuGet entry can also declare the trusted publishing policy that rig cannot read, so the audit can check around it.
All four fields are optional; leave them out rather than guess.

```json
{
  "registry": "nuget",
  "name": "Parquet.SourceGenerator",
  "manifest": "Directory.Build.props",
  "workflow": "release.yml",
  "auth": "oidc",
  "nuget_owner": "rtkelly-labs",
  "nuget_user": "rtk_13",
  "owners": ["rtk_13", "rtkelly-labs"],
  "scopes": ["push-new", "push-version"]
}
```

| Field | Meaning |
| --- | --- |
| `nuget_owner` | the nuget.org user or organisation that owns the policy. It must own the package |
| `nuget_user` | the profile name (not the email) that `NuGet/login` sends as `user:`. For an organisation's policy this is a member's account, so it can differ from `nuget_owner` |
| `owners` | the package's expected nuget.org owners |
| `scopes` | the policy's scopes: `push-new` (needed for a package's first publish), `push-version`, `unlist` |
| `environment` | as above. Set it only if the policy names an environment: a policy without one matches any job |

## Why it exists

Moving a repository to another owner breaks trusted publishing without saying so. The registry's trust config still
names the old owner, so every OIDC publish fails. npm reports it as a bare `E404` on `PUT`. Nothing else in CI notices
that the package has stopped shipping. `@rtkelly13/design-system` sat at 0.12.0 for three weeks after the move to
`rtkelly-labs` while `main` went to 0.16.0.

`uv run workspace packages audit --help` lists each check and when it fails.

NuGet endpoints are resolved from the V3 service index (`https://api.nuget.org/v3/index.json`), all without auth.

## What only the website can do

- **npm trust config.** It can be read and written with `npm trust` (npm ≥ 11.15), but every call needs a 2FA
  code, so `--trust` is opt-in and interactive. A trust config can be added only to a package that already exists.
- **NuGet trusted publishing policies** have no API, read or write. Manage them at
  <https://www.nuget.org/account/trustedpublishing>; `audit --trust` prints what to enter, and
  [nuget-new-package.md](nuget-new-package.md) drives the page with `agent-browser`. Only the token exchange
  that `NuGet/login` performs is an API. Its key lasts one hour. A policy matches the repository owner and
  repository by name, case-insensitively, plus the workflow file name (the called file, for a reusable workflow)
  and the environment if the policy sets one. A policy for a **public** repo locks the owner and repository IDs
  when it is created (seen in October 2026: `#<id>` shown straight after Create). For a private repo it stays
  temporary until a publish completes, within **7 days**, or is renewed. Either way, after a transfer add a new
  policy, publish, then delete the old one: the owner and repository of an existing policy can't be edited.
- **Creating an npm org** is web-only. **Deprecating a NuGet package** is web-only unless nuget.org has enabled
  the deprecation API for your account. That API (`PUT /api/v2/package/{id}/deprecations`) is undocumented and
  returns 403 otherwise.

## Runbook: move a package to a new scope

npm can't rename or move a scoped package. Publish under the new name and deprecate the old one.

1. Update the repository: `name` and `repository.url` in the manifest, plus every import and dependent package.
   Merge it. The CI publish fails once, because the new name doesn't exist yet and so has no trust config.
2. Create the name with a throwaway version, so the real version can still go out through CI with provenance.
   From an empty directory, with 2FA:
   `npm init -y --scope=<scope>`, set `name` and `version: "0.0.0-bootstrap.0"`, then
   `npm publish --access public --tag bootstrap`.
3. Run `npm trust github <new-name> --repo <owner/repo> --file <workflow>.yml --allow-publish` (npm ≥ 11.15).
   **A new trust config expires unless it completes a publish within 2 days**, so go straight on to step 4.
4. Rerun the failed publish: `gh run rerun <run-id> --repo <owner/repo>`. It publishes the real version through
   OIDC, which binds the trust config and moves `latest` off the placeholder.
5. Run `npm deprecate <new-name>@0.0.0-bootstrap.0 "placeholder"` and
   `npm deprecate <old-name> "Moved to <new-name>"`.
6. Update the `packages` entry here, then run `uv run workspace packages audit --project <name> --trust`.

Don't hand-publish the real version in step 2. If you do, the step 4 rerun finds it already published and skips,
the trust config waits for the next version bump (and may expire), and that version ships with no provenance.

## Runbook: set up a new npm package

The same bootstrap as a scope move, for a name that has never been published. `@rtkelly-labs/mermaid-toolkit`
and `@rtkelly-labs/design-system` both went through it in October 2026.

Before the first publish, the repository needs:

- **The version only in `package.json`.** Nothing rewrites it at build or publish time. mermaid-toolkit's
  `scripts/build.ts` set the version from a gitignored counter, so a fresh CI runner would have published
  `0.0.1` whatever the manifest said.
- **`repository.url` naming the publishing repo.** npm checks provenance against it and rejects a mismatch
  with a 422.
- **A publish workflow with `id-token: write`** that updates npm (`npm install -g npm@latest`; trusted
  publishing needs ≥ 11.5, and Node 22 bundles an older one) and publishes with `npm publish --tag <tag>`. No
  `NPM_TOKEN`.
- **Prereleases on `next` (or `dev`), never `latest`.** Make the workflow fail if a version containing `-` is
  sent to `latest`. A bare install resolves to `latest`, so install docs say `@next` until there's a stable
  release.
- **No `.npmrc` routing the package's scope elsewhere.** mermaid-toolkit had
  `@rtkelly-labs:registry=https://npm.pkg.github.com`, which sends both the publish and every install to
  GitHub Packages. The `@rtkelly13` line `enforce` writes is the only scope mapping intended.

Then, in one sitting (the trust config lapses if no publish completes within about 2 days):

1. `npm login`.
2. Publish a placeholder from an empty directory: `name`, `"version": "0.0.0-bootstrap.0"`, `repository`,
   `publishConfig.access: public`, then `npm publish --tag bootstrap`.
3. `npm trust github <name> --repo <owner/repo> --file <workflow>.yml --allow-publish --yes`.
4. Dispatch the publish workflow straight away, and check the version on the registry.

### 2FA with a passkey

Every publish and every `npm trust` call needs 2FA. A passkey approves in the browser instead of producing a
code, and that changes how the commands can be run:

- **In a normal terminal**, npm prints a link, waits while you approve, then carries on. This is the
  simple path.
- **Without a terminal** (Claude Code's `!`, CI, a pipe), npm prints the link and fails at once with `EOTP`,
  because it has nothing to wait in. `--otp` doesn't help: it takes a 6-digit authenticator code, and a
  passkey-only account has none. 1Password's `Npmjs` items hold no TOTP field.
- **To run it from `!` anyway**, wrap each call in `script -q <log> npm …` so npm gets a pseudo-terminal and
  waits. Open the link from the log as it appears (`!` shows output only when the command ends). This worked
  for the October 2026 bootstrap.

Adding a TOTP just for this isn't worth it. After the bootstrap, publishing runs in CI under OIDC with no
2FA, and a TOTP stored in 1Password would sit in the same vault as the password.

### Pitfalls

- **The first publish takes `latest`, whatever `--tag` says.** Both October 2026 placeholders were published
  with `--tag bootstrap` and still became `latest`, so a bare install fetched an empty package until a stable
  version moved the tag. Deprecate the placeholder as soon as the real release is out
  (`npm deprecate <name>@0.0.0-bootstrap.0 "placeholder; install @next"`, which needs 2FA), so a bare
  install warns instead.
- **A successful publish can take minutes to appear.** npm prints *"Your package is being processed and may
  take a few minutes to become available"*, and until then reads return E404, even when authenticated.
  Before deciding a publish failed, check the run log for `+ <name>@<version>` and the provenance line.
- **`npm view <name>` reports E404 for a package that has only prereleases.** With no version it asks for
  `<name>@*`, and `*` doesn't match prereleases. Ask for the version (`npm view <name>@0.0.0-bootstrap.0`)
  or read `https://registry.npmjs.org/<name>` directly.
- **pnpm in a clone nested inside another workspace** (a `temp/` clone inside rig) installs the outer
  workspace and reports success with no `node_modules`. Use `pnpm install --ignore-workspace`.

## Runbook: after transferring a repository

1. Run `uv run workspace packages audit`. The `repo`, `manifest` and `provenance` findings list what still names the
   old owner.
2. npm: `npm trust revoke` the old config, then `npm trust github … --repo <new-owner/repo>`.
3. NuGet: add a new policy for the new owner on nuget.org (the audit's `policy` checklist has the fields). Publish
   within 7 days, then delete the old policy.
4. Change the manifest's repository URL, then publish and re-run the audit.
