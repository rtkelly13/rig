# Runtime copies for the scheduled tasks

The background tasks (`jobs install`'s Logon, Tick and Display Guard tasks on Windows, Logon and Tick LaunchAgents on macOS) run from a pinned copy of one commit, not from a checkout. Pulling,
switching branches, a half-finished edit or a merge from another machine changes
nothing until you install a new copy.

```bash
rig runtime install            # fetch, build origin/main, check it, switch to it
rig runtime install --ref HEAD --no-fetch   # this checkout's commit instead
rig runtime status             # current, previous, commits behind origin/main
rig runtime rollback           # back to the previous copy
```

The `desktop-manager runtime` spelling is gone. Install with the C# from the commit you are installing,
or a newer one: an older checkout's `rig runtime install` still checks for `desktop-manager --help` in the
copy, and refuses any commit after its removal.

## Layout

```text
%LOCALAPPDATA%\rig\
  runtime\
    <sha12>\                     the commit's files (git archive)
      rig\                        rig, published from the commit
      installed.json             sha, ref, source repo, install time
    current                      junction to the copy in use
    state.json                   current and previous copy, source repo
  workspace-restore\logs\        restore logs, kept across updates
  chrome-tabs\                   traces, kept across updates
```

Before the repo was renamed the folder was `%LOCALAPPDATA%\shared-utilities\`. The first
`rig runtime install` into `rig\` copies everything from the old folder except `runtime\`
(`rig.db`, jobs state, logs, traces), keeping anything `rig\` already has, and leaves the old folder
alone. Then `rig jobs install` moves the scheduled tasks from `\Shared Utilities\` to `\Rig\`. Once
a restore has worked from the new copy, the old folder can be deleted.

A copy keeps the repo's layout, so the workspace plan (`desktop-manager/`) and
anything else found relative to the code is pinned with it. It has no Python environment. `rig` is
published from the commit's `src/Rig.Cli` into `rig\` with `dotnet publish`.
A `rig` started from `<copy>\rig\` (by its own name or through `current`) runs as
that copy: its jobs use the copy's bundled plan.

Copies are plain directories, not git worktrees, so they never hold a branch and
`estate` and `git worktree list` don't see them.

## Install

1. Fetch `origin` (for an `origin/*` ref) and resolve the ref to a commit. Installing
   the commit already current does nothing.
1. Extract the commit to `<sha12>.partial`, rename it to `<sha12>`
   and publish its `rig`.
1. Check it runs: `rig chrome-tabs organise --help`, `rig jobs run --help`, `rig jobs install --help`, and `workspace validate-plan` on the copy's plan.
   A failure leaves `current` where it was and removes the new copy.
1. Write `installed.json`, repoint `current`, record the old copy as previous and
   remove anything older. A copy still in use is left and removed by a later install.

Repointing `current` doesn't stop a run in progress. The repo's own code resolves
to the old copy's real path, which stays on disk as the previous copy. A library
the run first imports after the switch is found through `current`, so it comes
from the new copy.

## Pointing the tasks at `current`

Run the installers once from the copy itself. Each one detects that it is running
from a copy and registers paths through `current`, so later installs and rollbacks
move the tasks without registering them again:

```bash
"%LOCALAPPDATA%\rig\runtime\current\rig\rig.exe" jobs install
"%LOCALAPPDATA%\rig\runtime\current\rig\rig.exe" chrome-tabs install
```

`jobs install` registers the Logon, Tick and Display Guard tasks that run every
machine job (see [desktop-manager/jobs.md](jobs.md)), and removes
the standalone tasks older installers registered ("Workspace Restore", "Chrome Tabs
Organise", "Chrome Tabs Prune", "Workspace Display Guard"). `chrome-tabs install`
only writes the `chrome-tabs` command shim, pointed at the published rig in `current`.

## `rig` on PATH

Every `runtime install` also writes a `rig` command to `~/.local/bin/rig` (`rig.cmd` on Windows) that
runs `rig` from `current`. Like the tasks, it moves with later installs and rollbacks and is never
rewritten. That gives every shell the same pinned rig the web service and background jobs use,
including an agent's shell in another repo's worktree, which is where
[`rig estate wrapup report`](../estate/wrapup.md#reporting) runs.

The shim carries a `Written by desktop-manager runtime install` line. A file at that path without it
belongs to something else: install leaves it alone and says so. `~/.local/bin` must be on `PATH`, and
a shell function or alias named `rig` takes precedence over it.
