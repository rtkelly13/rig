# Work items

`desktop-manager work list` shows one row per **work item**, which is a (repo, branch) pair. Each row joins three sources:

- **Git:** the branch's checkouts (main checkout and linked worktrees), whether they are dirty, unpushed commits, and the upstream.
- **GitHub:** the newest pull request whose head is the branch.
- **Claude Code:** the sessions whose last working directory is in one of the branch's checkouts.

The command is read-only. It changes no repository, closes no session and touches no tab.

```
desktop-manager work list                      # rich table on stdout
desktop-manager work list --json               # stable schema, below
desktop-manager work list --state merged       # cleanup candidates
desktop-manager work list --repo data-platform --since 7d --sessions
```

| Option | Effect |
| --- | --- |
| `--repo TEXT` | Show only repos whose display name contains TEXT (case-insensitive). |
| `--state STATE` | Show only these states. Repeatable. Choices: `active`, `merged`, `closed`, `stale`, `trunk`, `unattached`. |
| `--since 7d\|24h\|2w\|ISO` | Show only items whose `last_activity` is at or after the given time. |
| `--all` | Also show quiet trunk items (see Filtering below). |
| `--json` | Print JSON to stdout. Warnings and timings still go to stderr. |
| `--sessions` | Add one table row per session under each item. |
| `--offline` | Skip GitHub entirely. Every item's `pr_lookup` is `skipped`. |
| `--refresh` | Ignore the PR cache and the clone-list cache. |
| `--no-status` | Skip `git status` for each checkout. Faster; `dirty` comes out `null`. |
| `--stale-days N` | Days without activity before an item counts as `stale`. Default 14. |
| `--session-days N` | How far back to read Claude transcripts. Default 30. |
| `--root PATH` | The code root. Default: `CODE_ROOT`, else the nearest ancestor of the rig checkout holding `AGENTS.md` and `personal/`, else `~/code`, else `<drive>/code` (`rig.roots`). |
| `--timings` | Print seconds per phase, and the wall-clock total, to stderr. |

The tool needs only git, `gh` and the files under `~/.claude`. It does not use the team workspace repo.

## Sources

### Git

The tool finds clones through `rig.estate.discovery`, which caches the clone list in `~/.estate-repos.json`. It makes three git calls per clone:

1. `git worktree list --porcelain` gives the checkouts. Prunable entries are skipped.
2. `git for-each-ref refs/heads refs/remotes/origin/HEAD` gives each branch's tip, last commit date, upstream, and ahead/behind or `[gone]`. It also gives the default branch, from `origin/HEAD`. If the clone has no `origin/HEAD`, the default branch is `main` or `master`, whichever exists.
3. `git rev-list --parents --branches --not --remotes` gives the commits that are on no remote-tracking ref. A branch's `unpushed` count is how many of those commits its tip can reach. This counts commits on no remote at all, not commits ahead of the branch's own upstream: a branch stacked on a pushed branch counts only its own commits. The listing stops after 5000 commits, which bounds the count for a clone with no remote.

After that, the tool runs `git status --porcelain` once per checkout to work out `dirty`.

### GitHub

For each clone, the tool sends one `gh api graphql` query. The query has one aliased `pullRequests(headRefName:)` lookup per branch it needs, and each lookup returns only the newest PR for that head.

The branches it looks up are every non-default branch that is checked out, plus every non-default branch that has a session. The repo's `owner/name` comes from the `origin` URL.

Answers are cached per repo in `~/.work-items-prs.json` for 10 minutes. A failed call is never cached and never stops the command:

- If `gh` fails (offline, not signed in, no access), that repo's `pr_lookup` is `unavailable` and a warning goes to stderr.
- If the origin is not on GitHub, the same thing happens.

### Claude Code sessions

Sessions are read through `desktop_manager.claude_sessions.discovery`, from three places:

- the transcripts in `~/.claude/projects` changed within `--session-days`
- the live registry in `~/.claude/sessions`
- the session-end ledger

A session is kept if either:

- it is running now, or
- it is interactive and has at least one prompt.

Print-mode sessions and sessions nobody typed into are dropped.

A session's state is decided by the first rule that matches:

| State | Rule |
| --- | --- |
| `waiting-on-user` | Running, and the registry says `idle`. |
| `live` | Running, with any other status. |
| `cleared` | The end ledger records `/clear`, and nothing was written to the transcript afterwards. |
| `ended-clean` | The transcript ends with a clean exit. |
| `died-mid-task` | The process is gone, and the last turn was mid-tool or got no answer. |
| `died-idle` | The process is gone, and the last turn was answered. |

`live` and `waiting-on-user` count as **running**.

## Attribution

A session belongs to the item for the checkout that holds its **last** working directory. If no last directory is recorded, its start directory is used instead. The branch is whatever that checkout has checked out *now*. So a session that switched branches in place counts toward the current branch.

The rules, applied in this order:

1. **Longest matching checkout path wins.** A worktree at `<clone>/temp/<name>` beats the clone that contains it.
2. **Directory no longer exists → unattached.** This covers removed worktrees. Such a session is never credited to the clone that used to contain the directory.
3. **Directory outside every known checkout, but inside a git repo → that repo.** The repo comes from `claude_sessions.folders`. It gets an item with no checkouts listed.
4. **Anything else → unattached.** The usual case is the code root itself, for example `C:\code`.

All unattached sessions go into one `unattached` item. They are not given to a per-repo trunk item, because guessing the repo would credit the session to work it may never have touched.

## Item states

An item's state is decided by the first rule that matches. `reasons` lists the facts behind it, the most important first.

| State | Rule |
| --- | --- |
| `trunk` | The branch is the repo's default branch. It is home, so it is never finished and never stale. |
| `merged` | The branch's newest PR is merged. This is a cleanup candidate. `reasons` lists anything that blocks cleanup: running sessions and uncommitted changes. Unpushed commits do not block cleanup, because a squash merge leaves the branch's own commits stranded. |
| `active` | There is an open or draft PR, a running session, uncommitted changes, or unpushed commits. |
| `closed` | The newest PR was closed without merging. `reasons` notes `upstream gone` when the remote branch is deleted. |
| `active` | There was a commit, a session or a PR update within `--stale-days`. |
| `stale` | None of the above applies. |
| `unattached` | Used only for the bucket of sessions with no repo. |

An item's `last_activity` is the latest of these:

- the branch's last commit
- its newest session
- its PR's last update

A detached HEAD shows as the branch `(detached)`. It gets no PR lookup and no ticket.

## Tickets

`ticket` is the first ticket ID in the branch name, upper-cased. Branches are matched in two passes:

1. **Upper case:** `[A-Z][A-Z0-9]+-\d+`, for example `feat/PROJ-1234-thing`.
2. **Any case, stricter shape:** 2-6 letters, a hyphen, then at least two digits, for example `proj-3583-hub-fixes`.

An upper-case match anywhere in the name wins over a lower-case one.

Some words often come before a number in branch names without being project keys. These are never treated as tickets: `pr`, `clone`, `issue`, `fix`, `step`, `part`, `rc`, `stack`, `layer`, `phase`, `wave`, `round`, `deploy` and `wt`. So `pr-894-review` and `clone-959` have no ticket.

## Filtering

The default view hides **quiet trunk** items: default branches with no sessions in the window and no uncommitted changes. They come back with `--all`, or with any `--state` filter. The summary line shows how many items are hidden.

Items are sorted by state (`active`, `merged`, `closed`, `stale`, `trunk`, `unattached`), then by most recent activity, then by key.

## JSON schema

The JSON is versioned by `schema_version`.

- Fields may be added without bumping the version.
- Renaming or removing a field bumps it.
- Times are ISO 8601 with an offset.
- `null` means unknown or not applicable.

[`src/desktop_manager/work/serialize.py`](../../src/desktop_manager/work/serialize.py) holds an annotated example of the full payload, next to the code that builds it. Run `desktop-manager work list --json` for a live one.

`items` holds only the items that pass the filters.

## Cost

The table below is from a run over about 130 clones and 450 checkouts on Windows, with a warm PR cache. Some phases overlap, so the per-phase times do not add up to the total.

| Phase | Seconds | Notes |
| --- | --- | --- |
| git refs | ~6 | `worktree list`, `for-each-ref` and one `rev-list` per clone, 16 threads |
| sessions | ~5-8 | Runs at the same time as git refs, so the two compete for disk. |
| git status | ~7 | One per checkout. Limited by disk, not threads: 32 workers are no faster than 16. Runs at the same time as the GitHub lookups. |
| pr slugs + github | ~1-3 | One GraphQL call per repo with checked-out branches. Cached for 10 minutes. |
| total | ~16 | |

`--no-status` drops the `git status` phase. `--offline` drops GitHub.
