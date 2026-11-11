# The decision feed

**Up:** [`estate`](README.md) · [Worktrees](./worktrees.md) · [Shelving](./shelving.md) · [Caches](./caches.md) · [Orca](./orca.md) · [Internals](./internals.md) · [Lessons](./lessons.md)

`estate decisions`: what the cleanup jobs leave for a human, and the feed file the web console reads.

`estate decisions` lists what the cleanup jobs leave for a human, one entry per
path, in the first kind that applies:

| kind | means | command |
|---|---|---|
| `dubious-ownership` | git refuses to read the checkout: another account owns it | `git config --global --add safe.directory <path>` (or `takeown`) |
| `unpushed-worktree` | an idle worktree holds uncommitted files or commits on no remote | `estate shelve <path>`: a preview; never a push to the PR branch |
| `closed-pr-worktree` | an idle worktree's branch had its newest PR closed unmerged | `git worktree remove` |
| `no-pr-worktree` | an idle worktree's branch never had a PR, or is detached | `git worktree remove` |
| `locked-path` | a worktree directory git no longer tracks, which an earlier delete left behind | `cmd /c rmdir /s /q` when only links and empty directories remain (it never enters a junction); otherwise `dir /s /b /a-d`, and the reason names the files left |

Idle uses the same git-bookkeeping test as `trim` (7 days), so live work never
appears. Locked worktrees are not judged on PR state, and merged or open PRs are
not decisions. PR state comes from `gh` and is fetched only for clones with an
idle, clean, pushed worktree; when `gh` cannot reach the clone's GitHub origin,
those worktrees are left out rather than reported as having no PR. A
`locked-path` is a directory under `.worktrees/` or `temp/worktrees/` with no
live `.git` pointer, or one directly under `temp/` whose `.git` file points at an
admin directory that is gone. Sizes never follow symlinks or junctions, so a
leftover workspace checkout does not count the clones linked under its
`projects/`.

`--root` repeats; without it the personal folder, its org folders and this
machine's work folders (`tools.estate.work_dirs` in `~/.workspace/config.json`) are scanned,
where they exist. `--json` prints the feed and `--write PATH`
writes it. Each entry is a `Decision`:

```json
{
  "kind": "no-pr-worktree",
  "repo": "shared-utilities",
  "path": "C:\\code\\personal\\shared-utilities\\.worktrees\\spike",
  "reason": "`spike` has never had a PR; untouched for 7+ days",
  "size_bytes": 1048576,
  "suggested_action": "Open a PR if the work is wanted; otherwise remove the worktree",
  "command": "cd C:\\code\\personal\\shared-utilities && git worktree remove ..."
}
```

The scheduled `estate-decisions` job runs daily after `worktree-reclaim`, over
the code root plus any configured work folders outside it, and writes the
feed to **`%LOCALAPPDATA%\shared-utilities\jobs\decisions.json`**, beside the
coordinator's own state. The file is a JSON array of `Decision` objects,
replaced in one step; its modified time is when it was collected.
The command runs on the C# CLI (`src/Rig.Core/Estate/Decisions.cs`), and
`rig.estate.decisions.read_feed` reads the feed back for the Python web console.

`rig web` serves it as `GET /api/ops/decisions` (`rig.estate.web`): the
decisions, the file's modified time as `written` (UTC), and a `note` when the
feed is missing, malformed or empty. A missing feed is not an error.
