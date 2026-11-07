# Startup and the display guard

`desktop-manager jobs install` registers the sign-in restore and the display
guard alongside the machine's other scheduled jobs, and removes the standalone
"Workspace Restore" and "Workspace Display Guard" tasks older installers registered,
so nothing restores twice. See [jobs.md](jobs.md). Install it from the installed
[runtime copy](runtime.md), so it follows `runtime install` and `runtime rollback`
rather than whatever the checkout holds.

## Restore at sign-in

- The `Logon` task runs `scheduled logon`, which restores first and then runs the
  due housekeeping jobs (see [jobs.md](jobs.md)).
- A scheduled task is used because Windows skips Startup-folder items on
  fast-startup boots, lock/unlock and RDP reconnects.

## Claude sessions at sign-in

A reboot kills the Claude sessions running in the workspace terminals. The
`Logon` task from `jobs install` brings them back by running these two steps, in this
order (the `restore` and `claude-sessions` jobs in [jobs.md](jobs.md)):

```bash
desktop-manager workspace restore --defer-logon-commands [--config ...] [--enable ...]
desktop-manager claude-sessions startup [--config ...] [--enable ...]
```

- Both steps sit behind the `claude-session-restore` [rig flag](../feature-flags.md),
  off by default now that Orca resumes its own sessions. While it is off the
  `claude-sessions` job is skipped and the restore ignores `--defer-logon-commands`,
  so terminals run their own command. `rig flags enable claude-session-restore`
  turns both back on.
- The restore opens each terminal entry marked `"command_at_logon": "deferred"`
  (see [plan files](plan-files.md#launch-entries)) as a bare `pwsh` in its folder,
  without its command. Startup must run after it, with the same plan flags, once the
  restore has exited and released its lock. A restore without
  `--defer-logon-commands`, such as a manual one, runs the command as usual.
- Startup first resumes sessions that stopped within `--since` into idle tabs. It
  then types each deferred terminal's command (`Set-Location` to its folder, then
  `claude`) into any planned terminal still at a bare prompt.
- `--since` defaults to `7d`, so a weekend or a few days off does not age
  sessions out of reach. `--since 12h` sets the window explicitly.
- It never prompts. `--dry-run` prints the plan and types nothing.
- Before pressing Enter it checks the typed text appeared in the tab. At logon a
  restored window can hold the foreground while keyboard focus is elsewhere, so
  keystrokes go nowhere. Startup focuses the terminal control first, types once
  more if the text still does not show, and otherwise fails that tab with nothing
  submitted.
- When the workstation is locked, as it is after Windows Update signs in following
  an overnight restart, it types nothing and exits 4. The
  [jobs coordinator](jobs.md) runs it again on each tick until it gets past that.
- When fewer screens are live than the plan places windows on, `workspace restore`
  opens nothing, writes `workspace-restore-skipped.txt` and exits 4. Startup then
  types nothing and exits 4 too. This happens when a dock or USB-C display has not
  come back after the restart. Both run again on each tick, so the workspace and
  sessions come up within 15 minutes of the screens returning. The note opens once
  per boot, not on every retry.
- Sessions it has no room for (no idle tab and every window full, or over
  `--max-sessions`) are listed in `claude-sessions-left-over.txt`. The note opens
  in Notepad with the `claude-sessions recover` command that resumes them.

**Which sessions it resumes.** Every session active within `--since` that is not
running now and was not ended with `/clear`. `/clear` is how a session is marked
done: run it before closing a tab you will not come back to. Closing the tab, `/exit`
or a reboot all leave the session to be resumed at the next logon, because a
session closed by the reboot looks the same as one closed by hand.
`--crashed-only` narrows it to sessions that crashed or stopped mid-task (a tool
call running, or a prompt Claude had not answered).

**Repos set up for Orca are left to Orca.** [Orca](https://github.com/stablyai/orca)
resumes its own sessions, so `startup`, `recover` and `list` skip any session that
started or was last working in one of these places:

- a repo listed in `~/.workspace/config.json` under `tools.orca.repos`. Edit the file
  by hand. Keys are paths relative to the code root (`CODE_ROOT`, else `~/code` or
  `<drive>/code`), for example `"personal/shared-utilities": {}`; absolute keys are
  ignored. `uv run workspace orca check` in the workspace repo validates the list.
  `WORKSPACE_HOME` overrides the config location.
- Orca's worktree folder, `~/orca/workspaces`. This only applies once at least one
  repo is listed.

`claude-sessions list` counts them, and `list --json` lists each one under `skipped` as
`left to Orca (<folder>)`.

**Where they go.**

- Sessions are grouped by the git repo or worktree they were last working in
  (their folder outside git). Each group shares one Windows Terminal window: an
  idle tab there, else a new tab while the window has fewer than `--max-tabs`
  (default 10).
- A group's first session takes an idle tab in a window no other group has, then
  any idle tab, then a new tab in the window with the fewest tabs.
- Startup opens no new windows. Sessions with nowhere to go, and those beyond the
  newest `--max-sessions` (default 40), are left over for `claude-sessions recover`.
- A tab counts as idle when it shows a bare shell's title or a deferred terminal's
  `window_title`. Idle tabs are paired with planned terminals by that title, then
  by a title ending in the terminal's folder name (`pwsh in workspace`), then in
  order. Resumes use unpaired idle tabs first, so planned terminals keep theirs.
  The pairing is best effort because the shell prompt retitles tabs.
- Before typing into a planned terminal, startup checks its tab is still idle and
  leaves it alone if not.

**Log and exit code.** Each step is appended to `claude-sessions-startup.log` in
the restore logs directory (above) and printed. It ends with
`resumed N, started M fresh, skipped K` (plus `, failed F` when something failed).
A dry run writes no log.

| Exit | Meaning |
| --- | --- |
| 0 | Everything planned came up, or there was nothing to do. Always 0 for `--dry-run`. |
| 1 | A resume or fresh start did not come up. |
| 2 | Bad arguments. |
| 3 | Everything attempted came up, but sessions were left over; run `claude-sessions recover`. |
| 4 | The workstation was locked, so nothing was typed. |

If startup does not run after a deferred restore, the deferred terminals stay at a
bare prompt: type `claude`, or run `claude-sessions startup` by hand.

## Monitor drop-outs and sleep (display guard)

Windows moves every window off a monitor that disappears. When the monitor comes
back, it often leaves the windows where it moved them. So a monitor that drops out
on each sleep/wake cycle scrambles the workspace, even after a good restore.

`jobs install` registers the guard as the `Display Guard` scheduled task. The task
runs `workspace guard` under `pythonw.exe` for the whole session, with no console
window. While it runs, the guard:

- Snapshots every managed window's placement every few seconds while the monitor
  set is stable. It keeps one snapshot for each monitor topology it has seen.
- Treats each of these as a display change: a change in the monitor set, a
  `WM_DISPLAYCHANGE` broadcast, a resume from sleep, or a session unlock.
- Waits for the topology to hold for `--settle-seconds` (default 6s) after a
  change. Then it puts back any window that moved away from that topology's
  snapshot, including maximized windows that Windows parked on another monitor. It
  checks twice more a few seconds later, in case Windows shuffles them again.
- Pauses while `workspace restore` holds `workspace-restore.lock`, so it neither
  snapshots a half-built layout nor undoes a restore.
- Exits straight away if another guard is already running (`display-guard.lock`).

Both locks live in `%LOCALAPPDATA%\shared-utilities\workspace-restore\`, whichever
copy or checkout runs the restore, so the guard always sees a restore in progress.
It logs every topology change and every window it moves to `display-guard.log` in
the restore logs directory. To watch it without moving anything:

```bash
uv run desktop-manager workspace guard --dry-run
```

The guard only puts windows back where they were. It does not relaunch anything
that closed. If a monitor was missing when the sign-in restore ran, that restore is
not repeated when the monitor returns. Run `workspace restore` by hand.
