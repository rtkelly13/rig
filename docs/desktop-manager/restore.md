# Restoring the workspace

## Restore modes

```bash
uv run desktop-manager workspace restore                  # reuse matching windows, launch missing ones
uv run desktop-manager workspace restore --dry-run        # show what would happen
uv run desktop-manager workspace restore --close          # ask visible windows to close first
uv run desktop-manager workspace restore --strict         # refuse a plan that leaves screen space uncovered
uv run desktop-manager workspace restore --relaunch       # no reuse; relaunch everything
uv run desktop-manager workspace restore --desktop Work   # restore one desktop in place
```

- By default, restore reuses matching open windows. It moves them to the planned
  desktop and screen, then launches whatever is missing.
- `--desktop <name>` keeps that desktop's real virtual-desktop position and reuses
  the windows already on it instead of relaunching them.
- Restore creates missing desktops and launches each desktop's entries in order.
  A launch waits for a window that was not open before it, so two entries for the
  same executable launch in list order and each gets its own window.
- `window_pid_file` names a JSON file with a `pid` key. Reuse only claims a window
  owned by that pid, and a missing file means the app is not running, so restore
  launches it. The Work desktop's two Orca entries use each profile's
  `orca-runtime.json`, since both windows are `Orca.exe` titled "Orca".
- Terminal entries launch through `wt.exe` with an explicit starting directory and
  an encoded PowerShell command, which avoids extra tabs and quoting problems at sign-in.
- `--defer-logon-commands` is for the sign-in run only. It opens terminals marked
  `"command_at_logon": "deferred"` as bare shells, and `claude-sessions startup` must
  run next to fill them (see [Claude sessions at sign-in](startup-and-display-guard.md#claude-sessions-at-sign-in)).
  Without the flag, those terminals run their command as usual.
- While a restore runs it holds `workspace-restore.lock` in
  `%LOCALAPPDATA%\shared-utilities\workspace-restore\`. A second restore exits
  instead of fighting it, and the [display guard](startup-and-display-guard.md)
  pauses until the lock is released.
- The run log times each phase: settling app updates, planning, creating desktops,
  each desktop, the second pass and the return to the starting desktop. A phase that
  runs over 15 seconds (90 for a desktop) is logged as `Slow step:` when it ends. A
  closing `Step timings:` line lists every phase.
- Before inventorying windows, restore applies or waits out a staged Warp update.
  A plan that launches no Warp window, for example with `warp-terminals` off, skips
  the check.
- The same phase installs a staged Orca update while no Orca is running, so the
  restore then launches both Orca profiles on the new build. With Orca running it
  stays staged for `workspace update-orca` (see [Orca updates](../orca.md#updates)).

## One app or one window

```bash
uv run desktop-manager workspace restart-app --name Fork
uv run desktop-manager workspace test-item --name "Chrome - Default Profile"
```

- `restart-app` force-closes every window of an app type, across all desktops, and
  re-opens each matching plan entry in its planned placement. `--graceful` asks for
  a polite close before force-killing survivors. `--desktop <name>` limits it to one
  desktop, and `--dry-run` previews it.
- `test-item` launches and places a single entry, for tuning one placement.

## Aligning open windows

```bash
uv run desktop-manager workspace align                    # move strays back
uv run desktop-manager workspace align --dry-run -v       # list every entry's outcome, move nothing
uv run desktop-manager workspace align --desktop Work     # only that desktop's entries
```

`align` moves windows that are already open back to their planned virtual desktop
and screen slot. It never launches, closes or relaunches anything, so it is safe to
run on a schedule while you work.

- Matching reuses restore's rules. A window on another desktop is only pulled back
  when its title (or Chrome URL hint) identifies it; a process-name match alone
  never moves a window across desktops. A terminal also needs its own window title.
- Windows move without switching the visible desktop and without taking focus.
  Minimized and maximized windows, the foreground window, and windows whose planned
  desktop does not exist yet are skipped and reported. A window with no match is
  reported as missing.
- Disabled entries and [feature flags](feature-flags.md) apply as in restore
  (`--enable` / `--disable` too). If too few screens are connected, or a live
  desktop's name differs from the plan's, align skips the run; run `restore` to fix
  the desktops.
- It takes `workspace-restore.lock` without waiting. When a restore holds it, align
  prints `align: skipped (restore in progress)` and exits 0.
- Output is one line, e.g. `align: 2 moved, 9 in place, 1 missing, 1 skipped (minimized)`;
  `--verbose` adds a line per entry. Every run's summary, and each moved, missing,
  skipped or failed entry, is appended to `align.log` (rotating) in the restore logs
  directory.
- Exit codes: `0` when it ran or skipped (missing windows are not errors), `1` for a
  missing config, unknown feature or unknown `--desktop`, `3` when some moves failed
  (for example an elevated window).

## Inspecting live state

```bash
uv run desktop-manager workspace status
uv run desktop-manager workspace status --verbose
```

`status` reports what is visible on each virtual desktop without writing a plan.

## Desktop names

Restore renames each desktop to its plan name on every run, because Windows
sometimes drops the names. To re-apply names, and retire desktops switched off by a
[feature flag](feature-flags.md#desktop-features), without a full restore:

```bash
uv run desktop-manager workspace name-desktops --dry-run
uv run desktop-manager workspace name-desktops
```
