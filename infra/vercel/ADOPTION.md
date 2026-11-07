# Adopting existing Vercel resources

A runbook for bringing Vercel projects and domains that already exist into this
Pulumi stack. It is written to be picked up and executed by a Claude Code session
running **locally**, because every step needs credentials that only exist on your
machine: a Vercel API token and a Pulumi Cloud login.

Read the whole thing before running anything. Step 5 is the one that matters.

## The rule that makes this safe

**A `pulumi preview` that proposes to delete or replace a domain is a bug in
`sites.ts`, not something to apply.** Deleting a `ProjectDomain` unhosts a live
site. If preview wants to delete one, stop and fix the registry so it matches
reality.

## Why import is necessary at all

A domain that already exists in Vercel cannot simply be declared. `pulumi up`
would try to **create** it, and Vercel rejects a domain already attached to a
project. Existing resources have to enter Pulumi state through `pulumi import`
first; only then does the declaration match reality and preview go quiet.

This applies to `adopt` sites too. Adoption skips owning the *project* — it does
not skip importing the *domains*.

## `manage` vs `adopt`

Every site in `sites.ts` declares one:

| | `manage` | `adopt` |
| --- | --- | --- |
| Vercel project | Pulumi owns it | Looked up by name, never owned |
| `pulumi destroy` | Deletes the project | Cannot touch the project |
| Import needed | Project **and** domains | Domains only |
| Use for | Projects that do not exist yet, or ones you want fully codified | Anything already serving traffic you would rather Pulumi could not delete |

When in doubt, choose `adopt`. It is the reversible option: switching `adopt` →
`manage` later is one import away, whereas an accidental `pulumi destroy` on a
managed production project is not recoverable from state.

## Steps

### 1. Credentials

```bash
cd infra/vercel
pnpm install
```

The Vercel token stays in 1Password and is never written to disk. `.env.op`
holds `op://` references rather than values, which is why it is committed while
the root `.gitignore` excludes every other `.env.*`.

Create the token at <https://vercel.com/account/tokens>, then store it:

```bash
op item create --category="API Credential" --vault=Private \
  --title=vercel-api-token credential="<token>"
```

`op run` needs the CLI talking to the desktop app: 1Password → Settings →
Developer → **Integrate with 1Password CLI**. Verify with `op whoami`; "no
account found" means the toggle is off.

Both entry points resolve the token the same way. **Run Pulumi through `pnpm
pulumi`, not bare `pulumi`** — the wrapper is what injects the credential:

```bash
pnpm discover                 # op run … -- node bin/discover.js
pnpm pulumi preview           # op run … -- pulumi preview
pnpm pulumi up
```

Arguments pass straight through, so `pnpm pulumi preview --diff` works as
expected.

The Vercel provider reads `VERCEL_API_TOKEN` from the environment (it is the
documented fallback for `vercel:apiToken`), which is why the token never has to
be written into stack config. There is deliberately no
`pulumi config set vercel:apiToken`: a bare `pulumi up` then fails with a
missing-credential error rather than silently using a stale copy, and the only
place the token exists is 1Password.

If 1Password is unavailable, `pnpm discover:env` reads a plain
`VERCEL_API_TOKEN` from the environment instead.

The remaining setup is Pulumi's own, and needs no Vercel credential:

```bash
pulumi login                          # Pulumi Cloud
pulumi stack select prod || pulumi stack init prod
pulumi config set teamId team_…       # only if the projects live under a team
```

`teamId` is not a secret — it is plain stack config, committed in
`Pulumi.prod.yaml`.

### 2. Discover what exists

```bash
pnpm discover
```

Read-only — every request is a GET, and it writes nothing to Vercel or Pulumi.
It produces two files in `out/` (gitignored):

- `sites.discovered.ts` — draft registry entries for every live project
- `import.json` — a Pulumi bulk-import file

### 3. Merge the sites you want into `sites.ts`

Copy entries from `out/sites.discovered.ts` into `SITES` in `sites.ts`. For each:

- Pick `ownership` deliberately (see the table above). The draft says `adopt`
  for everything because everything discovered already exists.
- Give it a **`key` you are happy to keep forever**. Renaming a key later
  replaces every resource for that site, and for a domain that means a window
  where it does not resolve.
- Delete domains you do not want this stack to manage. A live domain left out of
  `sites.ts` is simply left alone — the script reports these rather than
  silently ignoring them.

Entries already in `sites.ts` are marked in the draft so you can compare against
what is actually live.

### 4. Regenerate the import file

```bash
pnpm discover
```

**This step is not optional.** `import.json` is computed from what `sites.ts`
declares, so the run in step 2 knew nothing about the sites you just merged. It
deliberately emits nothing for a project absent from the registry, no `Project`
entry for an `adopt` site, and no domain the registry has not opted into.

### 5. Import, then verify the preview is empty

```bash
pnpm pulumi import -f out/import.json --generate-code=false
pnpm pulumi preview
```

`--generate-code=false` because the program already declares these resources;
letting Pulumi generate code would duplicate them.

**Preview must show no changes for anything you just imported.** If it wants to:

- **delete a domain** — the registry is missing a domain that exists live, or a
  `key` does not match. Fix `sites.ts`. Do not apply.
- **create a domain that already exists** — the import did not land, usually a
  resource-name mismatch. Names come from `naming.ts`; do not hand-edit them in
  `import.json`.
- **update a project** — `sites.ts` disagrees with the live project's settings
  (root directory, production branch, node version). Make the registry match
  reality, unless you are intentionally changing it.

Work one site at a time if the diff is hard to read:

```bash
pulumi config set only <site-key>     # local config, no Vercel call
pnpm pulumi preview
pulumi config rm only
```

### 6. Apply

```bash
pnpm pulumi up
```

Only after a clean preview. Then commit `sites.ts` — `out/` stays gitignored.

## Notes

- **Build settings are out of scope by design.** Vercel reads `vercel.json` from
  each repo at build time and it overrides project settings, so this stack
  declares identity, domains and env vars only. If an imported project shows a
  build-command diff, the fix is to leave it out of the registry, not to codify
  it here.
- **Secrets do not belong in `sites.ts`** — it is committed. Use
  `pulumi config set … --secret`.
- **Environment variables are not discovered.** Vercel's API returns them per
  project and some are secret; pulling them into a committed file is a leak
  risk. Add the ones you want by hand, and import them with
  `vercel:index/projectEnvironmentVariable:ProjectEnvironmentVariable` using the
  ID `[team_id/]prj_xxx/<env_var_id>`.
- **`pnpm discover` is safe to re-run** at any time, and re-running it after
  editing `sites.ts` is the intended way to check for drift.
