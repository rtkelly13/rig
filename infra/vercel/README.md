# infra/vercel — Pulumi

Vercel projects and domains for the ryankelly.dev sites, using the
[`@pulumiverse/vercel`](https://www.pulumi.com/registry/packages/vercel/) provider.

Self-contained: its own `package.json`, `tsconfig.json` and lockfile, no coupling to
this repo's Python toolchain. Nothing else here imports from it.

| File | Role |
| --- | --- |
| [`sites.ts`](./sites.ts) | **The registry.** What is hosted, and at which domains |
| [`index.ts`](./index.ts) | The engine. A loop over the registry |
| [`naming.ts`](./naming.ts) | Resource names, shared by the program and the discovery script |
| [`discover.ts`](./discover.ts) | Reads the live account; drafts registry entries and a bulk-import file |
| [`ADOPTION.md`](./ADOPTION.md) | **Runbook for bringing existing Vercel resources under management** |

Adding a *new* site should be an edit to `sites.ts` and nothing else. Bringing an
**existing** Vercel project or domain under management needs `pulumi import` first —
follow [`ADOPTION.md`](./ADOPTION.md).

## Scope: identity, domains, env — not builds

Vercel reads `vercel.json` from each repo at build time and it **overrides project
settings**. Declaring `buildCommand` / `outputDirectory` / `framework` here as well
would leave two sources of truth with a silent winner, so this stack deliberately
declares none of them. Repos own how they build; this owns where they live.

## Deploy quota: gate at creation

Vercel's Hobby plan allows 100 deployments a day, account-wide, and the count is spent when a
deployment is **created**, not when it builds. So `ignoreCommand` is not a gate: it cancels a
deployment that has already used its share of the quota.

Gate in the repo's `vercel.json` with `git.deploymentEnabled`: allow `main` and any preview
slots (`slot/*`), and end with `"**": false` so every other branch creates nothing. Without the
`**` fallback, unlisted branches still create. `ignoreCommand` (in `vercel.json`, or
`ignoreCommand` on a `SiteSpec`) may still skip builds on refs the allowlist lets through, but
it never saves quota.

`uv run repo-governance vercel-gating --all` checks every linked project. The incident behind
this, and the traps, are in [docs/repo-governance/vercel-gating.md](../../docs/repo-governance/vercel-gating.md).

## `manage` vs `adopt`

Every site declares one:

- **`manage`** — Pulumi creates and owns the Vercel project. Only for projects that
  do not exist yet; declaring one that already exists fails on create.
- **`adopt`** — the project already exists and keeps whatever created it as its owner.
  Pulumi looks it up by name and attaches only domains and environment variables, so
  a `pulumi destroy` cannot take the project — or the site it serves — with it.

**Use `adopt` for anything already serving traffic.** That is the whole safety story
for growing this stack to cover existing sites: bringing the blog under management
means adding an `adopt` entry and attaching its domains, never a `manage` entry.

Either way, a domain that **already exists** in Vercel must be imported before it can
be declared — `pulumi up` would otherwise try to create it and Vercel rejects a domain
already attached to a project. Adoption skips owning the project, not importing the
domains. See [`ADOPTION.md`](./ADOPTION.md).

## Usage

```bash
cd infra/vercel
pnpm install

pulumi stack init prod           # first time only

pnpm pulumi preview
pnpm pulumi up
```

`pnpm pulumi` is `pulumi` wrapped in `op run --env-file=.env.op`, which resolves
the Vercel token from 1Password into `VERCEL_API_TOKEN` — the provider's
documented fallback for `vercel:apiToken`. The token is deliberately not in
stack config, so bare `pulumi up` fails on a missing credential rather than
using a stale copy. Arguments pass through: `pnpm pulumi preview --diff`.

See [ADOPTION.md](./ADOPTION.md) for first-time credential setup.

### Previewing one site at a time

Once this stack owns live domains, a full `pulumi preview` diff is noisy and a
mistake is easy to miss. Narrow it:

```bash
pulumi config set only design-system-storybook
pnpm pulumi preview
pulumi config rm only
```

### Configuration

| Key | Default | Notes |
| --- | --- | --- |
| `teamId` | *(unset)* | Required only on a Vercel team account |
| `only` | *(unset)* | Restrict the run to one site key |

Secrets belong in `pulumi config set … --secret`, not in `sites.ts` — it is committed.
The env values in the registry today are public URLs.

## Currently declared

| Site | Domains |
| --- | --- |
| `blog` | `ryankelly.dev`, `blog.ryankelly.dev`, `p1.blog.ryankelly.dev`..`p3.blog.ryankelly.dev` (branches: `slot/1`..`slot/3`) |
| `blog-storybook` | `blog-storybook.ryankelly.dev`, `p1.blog-storybook.ryankelly.dev`..`p3.blog-storybook.ryankelly.dev` (branches: `slot/1`..`slot/3`) |
| `design-system-storybook` | `design-system.ryankelly.dev`, `p1.design-system.ryankelly.dev`..`p3.design-system.ryankelly.dev` (branches: `slot/1`..`slot/3`) |
| `maze` | `maze.ryankelly.dev`, `p1.maze.ryankelly.dev`..`p3.maze.ryankelly.dev` |

### Before the first `pulumi up`

- **`ryankelly.dev` must already be in the Vercel account** that owns the token.
  Subdomains of a domain Vercel controls need no DNS work; if the apex is hosted
  elsewhere, add the CNAME Vercel shows for each subdomain afterwards.
- **The `preview` branch must exist** in `rtkelly-labs/design-system` and have deployed
  once, or `preview.design-system.ryankelly.dev` has nothing to resolve to:
  `git switch -c preview main && git push -u origin preview`.
- **`blog-storybook` needs one dashboard toggle** Pulumi cannot set: "Include source
  files outside of the Root Directory in the Build Step" (Settings → Build &
  Deployment). Its root is `storybook-site/`, but the Storybook build needs the whole
  blog repo.

## Adding a site

```ts
{
  key: 'my-site',              // stable — renaming replaces every resource
  projectName: 'my-site',
  ownership: 'manage',         // or 'adopt' if it already exists
  repo: 'rtkelly13/my-site',
  domains: [
    { domain: 'my-site.ryankelly.dev' },
    { domain: 'preview.my-site.ryankelly.dev', gitBranch: 'preview' },
  ],
}
```

A domain with `gitBranch` is a **branch domain**: Vercel points it at that branch's
newest deployment. Without it, the domain follows production. A branch domain maps to
exactly one branch — no custom domain can follow "whichever PR deployed last"; per-PR
deployments keep their generated `*.vercel.app` URLs.
