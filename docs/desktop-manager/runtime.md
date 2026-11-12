# Runtime copies for the scheduled tasks

The background tasks (`jobs install`'s Logon, Tick and Display Guard tasks) run from a pinned copy of one commit, not from a checkout. Pulling,
switching branches, a half-finished edit or a merge from another machine changes
nothing until you install a new copy.

```bash
uv run desktop-manager runtime install            # fetch, build origin/main, check it, switch to it
uv run desktop-manager runtime install --ref HEAD --no-fetch   # this checkout's commit instead
uv run desktop-manager runtime status             # current, previous, commits behind origin/main
uv run desktop-manager runtime rollback           # back to the previous copy
```

## Layout

```text
%LOCALAPPDATA%\shared-utilities\
  runtime\
    <sha12>\                     the commit's files (git archive) and its own .venv
      rig\                        rig and rigw, published from the commit
      installed.json             sha, ref, source repo, install time
    current                      junction to the copy in use
    state.json                   current and previous copy, source repo
  workspace-restore\logs\        restore logs, kept across updates
  chrome-tabs\                   organise.log and traces, kept across updates
```

A copy keeps the repo's layout, so the workspace plan (`desktop-manager/`) and
anything else found relative to the code is pinned with it. Its environment is
built from that commit's `uv.lock` with `uv sync --frozen --no-dev`, copying
files out of uv's cache rather than hard-linking them. `rig` and `rigw` are
published from the commit's `src/Rig.Launcher` into `rig\` with `dotnet publish`.
A `rig` started from `<copy>\rig\` (by its own name or through `current`) runs as
that copy: its jobs use the copy's bundled plan.

Copies are plain directories, not git worktrees, so they never hold a branch and
`estate` and `git worktree list` don't see them.

## Install

1. Fetch `origin` (for an `origin/*` ref) and resolve the ref to a commit. Installing
   the commit already current does nothing.
2. Extract the commit to `<sha12>.partial`, rename it to `<sha12>`, build its `.venv`
   and publish its `rig`.
3. Check it runs: `desktop-manager --help`, `chrome-tabs --help`, `rig jobs run --help`, importing the
   scheduled entry points, and `workspace validate-plan` on the copy's plan.
   A failure leaves `current` where it was and removes the new copy.
4. Write `installed.json`, repoint `current`, record the old copy as previous and
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
"%LOCALAPPDATA%\shared-utilities\runtime\current\.venv\Scripts\desktop-manager.exe" jobs install
"%LOCALAPPDATA%\shared-utilities\runtime\current\.venv\Scripts\desktop-manager.exe" chrome-tabs install
```

`jobs install` registers the Logon, Tick and Display Guard tasks that run every
machine job (see [desktop-manager/jobs.md](jobs.md)), and removes
the standalone tasks older installers registered ("Workspace Restore", "Chrome Tabs
Organise", "Chrome Tabs Prune", "Workspace Display Guard"). `chrome-tabs install`
only writes the `chrome-tabs` command shim, pointed at `current\.venv`.

## `rig` on PATH

Every `runtime install` also writes a `rig` command to `~/.local/bin/rig` (`rig.cmd` on Windows) that
runs `rig` from `current`. Like the tasks, it moves with later installs and rollbacks and is never
rewritten. That gives every shell the same pinned rig the web service and background jobs use,
including an agent's shell in another repo's worktree, which is where
[`rig estate wrapup report`](../estate/wrapup.md#reporting) runs.

The shim carries a `Written by desktop-manager runtime install` line. A file at that path without it
belongs to something else: install leaves it alone and says so. `~/.local/bin` must be on `PATH`, and
a shell function or alias named `rig` takes precedence over it.
