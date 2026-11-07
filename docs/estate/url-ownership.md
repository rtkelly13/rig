# Local ports and Vercel names

Two declarations in `config/managed-projects.json` tie a URL to the project it
belongs to, without asking the page: a local app's **port range**, and the
project's **Vercel project names**. `rig.workspace.url_owners` reads both, and
`chrome-tabs group` uses them to put a tab beside its repo's PRs and issues.

## Local port ranges

Each managed project that runs local apps (a dev server, a review UI, Storybook)
claims a range of ports in `config/managed-projects.json`, and starts its apps
inside it:

```json
{
  "name": "rig",
  "repo": "git@github.com:rtkelly13/rig.git",
  "ports": { "start": 8090, "end": 8099 }
}
```

A port then names its project:

- **No collisions.** Two repos' servers never reach for the same port, so
  starting one never kills or shadows the other.
- **Browser tabs group with their repo.** `chrome-tabs group` puts a tab on
  `localhost` or `127.0.0.1` whose port is inside a project's range into that
  project's repo group, beside its PRs and issues. A port outside every range is
  left ungrouped rather than guessed. Grouping by URL needs a driver that reports
  URLs, which today is macOS.

### The rules

`rig.workspace.ports.load_port_claims` enforces them, and a test holds the
committed config to them:

- A range lies within 1024–65535, with `start` no greater than `end`.
- No two enabled projects' ranges overlap. The error names both projects.
- A project with ports has a GitHub `repo`, which is what its tabs group under.

`ports` is optional. Add it when a project gains its first local app.

### Allocating

Claim ten-port blocks above 8090, in order, and do not reuse a released block
for a different repo while old bookmarks may still point at it. Keep away from
the ports every framework defaults to (3000, 5173, 8000, 8080): defaults are
what collide.

A claim is only half the job: the repo's apps must then start inside it. Move an
app off a default into the range; never stretch a range over a default. Until
the app moves, its tabs stay ungrouped.

| project | ports | in use |
|---|---|---|
| rig | 8090–8099 | 8090 web dashboard, 8091 its preview, 8092 Asset Studio (`image-reconcile serve`), 8094 triage |
| design-system | 8100–8109 | 8100 Storybook, 8101 site dev, 8102 site start, 8103 Playwright Storybook, 8104 serve-deploy (rtkelly-labs/design-system#435) |
| blog | 8110–8119 | 8110 `next dev`, 8111 Storybook, 8112 `next start` and Playwright, 8113 docker-compose (rtkelly13/blog#190) |
| docs | 8120–8129 | 8120 `next dev`, 8121 `next start` (rtkelly-labs/docs#11) |

Blog and design-system Storybooks both used 6006, and blog and docs both used
3000, so those ports can name no repo and stay unclaimed.

## Vercel project names

Each project that deploys to Vercel lists its Vercel project names:

```json
{
  "name": "design-system",
  "vercel": ["design-system", "design-system-storybook"]
}
```

Vercel's generated URLs all start with the project name:
`<name>-<hash>-<team>.vercel.app` for a deployment and
`<name>-git-<branch>-<team>.vercel.app` for a branch. A `*.vercel.app` host
belongs to the project whose declared name is its longest prefix, so
`design-system-storybook-…` is never taken by `design-system`.

**Name aliases with the project as a prefix:** `design-system-storybook-diffra`,
not `rtk-ds-exp-diffra`. Then the alias is recognised like any generated URL,
with nothing more to declare. Older aliases keep working; they are just not
recognised. A custom preview suffix on your own domain (Vercel's Preview
Deployment Suffix) needs a Pro plan add-on, so the prefix is the convention
instead.

`rig.workspace.vercel_names` validates the names: real Vercel names, none
declared twice. Whether they are right is established from Vercel itself by
`repo-governance vercel-projects` ([audit](../repo-governance/vercel-gating.md#ownership-vercel-projects)).
