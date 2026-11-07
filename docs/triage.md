# PR and issue triage

`rig triage` maintains an estate-wide queue in rig's existing SQLite database and a transient local Markdown projection. `rig web` serves one SPA: triage, the reference catalogue, estate, the operations pages ([web-ops.md](web-ops.md)) and the existing studios share the same document and navigation. The server uses its configured SQLite database, including explicit `StudioServer(db_path=...)` overrides.

```bash
uv run rig triage refresh
uv run rig triage verify
uv run rig web
```

The default Markdown projection is `temp/triage/queue.md` under the rig root resolved from its managed-project registry. It is regenerable, gitignored runtime output. The vault contains the durable `Areas/Development/PR and Issue Triage.md` process and `Resources/Engineering/Frontend/PR Triage and Inbox UI References.md` source index; queue contents and interaction history stay in rig.

```bash
uv run rig triage refresh --db temp/triage.db --output temp/queue.md
uv run rig triage verify temp/queue.md
```

`--config` selects another managed-project registry. Disabled and explicitly archived entries are excluded. Fetches use `gh` authentication, cursor pagination, typed decoding and timeouts. All repository fetches must succeed before any snapshot is updated. Items missing from the open list are looked up explicitly to distinguish merged/closed from a fetch omission.

## Inbox workflow

- **Inbox** contains PRs needing attention; **Issues** is the corresponding issue view.
- **Waiting** contains a local review decision for the current head. A new head or a new human GitHub discussion returns that item to attention.
- **Snoozed** contains explicitly deferred work, until manual resumption. It does not schedule a timed return.
- **Done** contains locally archived, merged, closed or out-of-scope work. Local archive never closes or merges a GitHub item. A new head or human GitHub discussion returns archived work to attention.

Opening an item records it as read. Mark unread explicitly when it needs another pass. Read state is independent of review state. Decisions, notes, archive, unread and snooze interactions append to the event ledger; refreshing a snapshot never clears it. A decision rechecks the live head before saving. Resuming or moving back to inbox starts a fresh attention pass while retaining earlier decisions.

The selected PR remains visible when it moves out of its current list, so recording a decision does not silently advance to another review. The visible triage page polls its selected open item every 30 seconds, importing paginated comments and submitted/inline reviews with source deduplication, timestamps, actors and commit attribution. Human and bot activity can be shown separately. Draft notes survive navigation and reload in session storage. Full refresh discovers new work; there is no unattended scheduler. The tool never updates branches, posts reviews or merges PRs.

## URL navigation

| Route | View |
| --- | --- |
| `/` or `/triage/inbox` | PR attention inbox |
| `/triage/issues` | Issue attention inbox |
| `/triage/waiting` | Reviewed work awaiting changes or merge |
| `/triage/snoozed` | Deferred work |
| `/triage/done` | Archived and completed work |
| `/triage/references` | Searchable UI reference catalogue |
| `/estate`, `/studio`, `/optical` | Existing rig tools in the same shell |

Append `/owner/repository/number` to a queue route to address an item, for example `/triage/inbox/rtkelly13/rig/397`. Query parameters `q`, `repo`, `status` (readiness index 0–3), `read=unread`, `activity=all` and `layout=focus` preserve filters and the reading layout. Reload and browser Back/Forward restore the route. `j`/`k` select the next/previous visible row; `/` focuses search. Shortcuts ignore typing fields. `/triage/pulls` and `/triage/history` remain compatible views; `/static/triage.html` redirects into the SPA.

## Deterministic ordering

PRs sort by case-insensitive priority labels (`P0` through `P3`, optionally prefixed `priority: `; unlabelled last), GitHub merge-state readiness (clean, behind, blocked, draft/unknown), additions plus deletions, creation time, lowercase repository and number. Issues sort separately by priority, creation time, repository and number. Inbox filters preserve this order. Read/unread and local workflow state determine attention, not an undocumented second ranking. Clean is not authorization to merge.

## UI and reference catalogue

`web/` contains the React/TypeScript SPA. It uses published `@rtkelly-labs/design-system` 0.17.0 components (`Button`, `Badge`, `Input`, `TextArea`), semantic tokens, the Midnight/Sketch theme levels and self-hosted fonts. Existing studio JavaScript and Three.js load only when a studio/estate route is opened. This keeps the existing tools available without a second application or an iframe.

The header’s **Tools** launcher opens an accessible modal for estate, studios and the reference library. Navigation defaults to a 64px icon rail with design-system hover/focus tooltips; a saved expanded preference is respected. The bounded queue defaults to 300–360px. Search stays visible beside a **Filters** button; repository, readiness and unread choices open in a design-system popover. Repository and readiness use the design-system Select listboxes, with themed options and nested Escape/focus handling. A single summary line shows the item count and applied filters, with a clear action. The closed header uses about 84px of height; ordering and refresh details are available inside the popover. At 1440px, the PR workspace is 1048px wide and begins at y=104px. Drag the divider to resize it, use Left/Right (20px steps), Home/End (260px/560px), or double-click to restore the responsive default. Pixel widths are saved separately from the older percentage preference.

**Focus view** hides the queue without unmounting it or the current reading pane. **Split view** restores it; URL parameter `layout=focus` makes focused links reloadable. Back to inbox, previous/next and item position stay available. On screens at or below 900px the selected item occupies the workspace; Back to inbox restores the queue and its filters. Drafts and scroll position survive Split/Focus changes. The selected title and primary **Open PR on GitHub** action open GitHub in a new tab. Both themes use the design system’s page surface for navigation and raised surface for the reading pane; the saved theme choice is retained.

GitHub refresh and selected-item polls now persist the PR/issue description. Old snapshots decode without a database migration and show an explicit unavailable message until fetched. The pane shows the description and conversation before the decision composer, with local observation history collapsed separately. Markdown is rendered through `react-markdown`, GFM and design-system `Prose`, with raw HTML disabled and default URL sanitization retained. Remote images are links rather than inline content.

The catalogue records nine interface references across Gmail, GitHub, Graphite, GitKraken, GitLab, Octobox and Gitify, with official evidence links, pattern rationale and limits. Diagrams are original schematic studies rather than copied product screenshots. The durable source index lives in the vault, linked from the triage process; `web/referenceCatalog.ts` is the implementation's curated set.

```bash
pnpm install --frozen-lockfile
pnpm web:typecheck
pnpm css:build
pnpm css:check
```

The compiled JS/CSS/fonts and their licenses are committed under `src/rig/image_drift/templates/app_assets/`, so the Python server does not need Node at runtime. `css:build` also compiles the gallery and C4 viewer stylesheets; `css:check` rebuilds every output into `temp/` and compares bytes. CI checks both TypeScript and committed assets.

## Repository scope and estate connectivity

The scope picker separates a repository queue from the all-repositories inbox. Repository routes are `/repos/<owner>/<repo>/triage/<view>` and `/repos/<owner>/<repo>/triage/<view>/<number>`. The existing `/triage/<view>` routes remain the all-repository version. Scope is independent of filters and grouping; repository navigation, selected-item links, next/previous and reload retain it. The estate inspector links into each repository's PR, issue and waiting queues.

The existing estate jobs, backup controls and catalogue remain available at `/estate/operations`, linked from the graph and Tools launcher. `/estate` is a React dependency DAG with package search, connection-type filters, zoom, a repository inspector and action-count overlays from the saved triage snapshot. Provider → consumer arrows use declared package relationships, with package name, requirement, scope and manifest evidence available in the inspector. Repositories without matching connections remain listed; missing local scans are explicitly labelled. **Refresh triage** updates the GitHub snapshot; **Rescan connections** updates the local dependency inventory.

Connectivity joins existing publisher bindings in `config/managed-projects.json` to tracked local `package.json`, PEP 621 `pyproject.toml`, `.csproj` and central package manifests. Current local package names supplement older registry aliases, so a renamed scope can still connect to its provider. npm runtime/development/peer/optional references, literal NuGet references and cross-repository project references are indexed. XML comments are ignored; central NuGet requirements use the nearest declaration scope. This is manifest evidence, not lockfile resolution or a complete MSBuild condition evaluation.

For a relationship without a package manifest, a project can declare `"depends_on": ["provider-project-name"]` (or an owner/repository identity) in the same registry. Unknown, self and ambiguous publisher relationships are reported instead of guessed. Current-checkout manifests are used for Rig itself, including worktree changes; other repositories use Rig's existing estate path resolution. Graph requests do not fetch remote manifests or mutate source repositories.

The transient ledger lives in `temp/estate/connectivity.duckdb`, with a typed response cache alongside it. Reads reuse that cache for one minute; explicit rescans rebuild it. `/api/estate/graph` and `/api/estate/graph/refresh` expose the graph. The existing `/api/estate` catalogue remains available. Strongly connected repositories are condensed into cycle nodes to produce a true DAG; original edges remain available in the inspector. The layout uses the design system's deterministic graph geometry and semantic role tokens, with no fabricated runtime metrics.

## Action signals and groups

The queue defaults to native PR stacks and explicitly labelled branch chains. Native membership/position comes from [GitHub's stack fields](https://docs.github.com/en/graphql/reference/pulls#pullrequeststack); a branch chain is inferred only from an unambiguous head/base match in one repository. Forks, ambiguous heads and cycles are not advertised as inferred stacks. A group occupies the rank of its first member; native members run bottom-to-top. Flat mode retains the original deterministic order. Repository and next-action grouping are selectable through the URL-backed Filters popover. Collapsed members are excluded from next/previous and j/k traversal, while the selected reading pane remains stable.

| Signal | Evidence and action |
| --- | --- |
| Failed/running/passed checks | Head commit rollup; individual job links load for the selected item. Failed checks use danger, pending uses info, confirmed success uses success. |
| Review requested / changes requested / approved | Viewer-specific review requests and aggregate review decision; local triage review still applies to the exact head. |
| Unresolved discussion | Review-thread resolution flags; imported human replies after a local decision/note return attention. Comment counts are counts, not claims that every comment needs a reply. |
| Draft / conflicts / dependencies | Draft and mergeability fields; native stack position, members and base branch. No group action merges or posts reviews. |
| Issues | Unassigned ownership, missing priority, discussion, labels, milestones, closing PR links and resolution reason. |
| Agent verdict | What the agent session on the PR's head branch last answered to [`estate wrapup`](estate/wrapup.md): **Agent: done** (success; quiet as *done, older commit* once the PR has commits after the answer), **Agent: &lt;what's left for you&gt;** (warning; the full text and worktree on hover), or **Agent asked, no reply** (quiet). Read from each managed repo's worktrees' `temp/.rig/wrapup.json`, matched by repo and branch, rescanned at most every 30 seconds. Fork PRs never match. The chip always shows in the row, and each verdict is a *Needs action* filter. It reports the agent's view only: it never changes the ordering, and a done verdict does not authorize a merge. |

The selected pane exposes stack members, job details, ownership and related work in expandable sections. Colour always accompanies an icon and a written label. Failed checks and merge state are separate facts; a clean merge state does not imply successful CI. Approved or passing badges do not authorize a merge. Missing metadata is shown as not fetched, never as success or confirmed missing ownership.

Bulk snapshots use small cursor-paginated pages and aggregate check state; selected-item checks load up to 100 jobs and 100 native stack members. Assignees/review requests, review threads and linked-work lists have explicit bounds. Partial lists and lower-bound unresolved counts are labelled; GitHub remains the complete detail source. Projects, due dates, reaction interpretation and branch-protection policy evaluation are not guessed from titles or labels.

The description is a separate collapsible card. The PR/issue feed presents discussion and reviews chronologically with authors, event labels and timestamps; local technical observations remain separately collapsed. Markdown destinations are sanitized and resolved against GitHub. Inbox chrome clips overflow and cannot scroll; only the queue and reading panels own vertical scroll, with contained overscroll.

## Markdown contract

The generated block is delimited by `<!-- rig-triage:begin v1 -->` and `<!-- rig-triage:end -->`. Refresh preserves surrounding prose. Identical snapshots generate identical bytes, and unchanged exports do not touch the mtime. Closed, snoozed and archived items remain in SQLite history rather than the active Markdown queue. Waiting items remain in this all-open projection, while the UI separates attention and waiting views.

`verify` is offline and read-only. It checks markers, headers, column count, identities, matching URLs, priorities, readiness, timestamps, head SHAs, duplicates, ranks and deterministic order. It never opens the database or repairs a file. It does not assert live freshness or business correctness. If exporting fails after a committed interaction, the event remains saved and the UI reports the error; fix the path or markers and refresh to regenerate.

Database schema migration 2 is additive. Existing backup tooling backs up the triage ledger with the rest of rig's SQLite database.
