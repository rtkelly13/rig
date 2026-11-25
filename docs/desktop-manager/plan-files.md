# Workspace plan files

A plan is JSON: a list of desktops, each with a `name` and an ordered `launch` list.
`wire-machine` writes the machine's curated plan to the default path,
`%APPDATA%\workspace-manager\windows-workspace.json`. Commands that read a plan,
such as `restore` and `validate-plan`, take `--config` to point at a different one.

## Launch entries

A terminal entry:

```json
{
  "name": "Workspace Terminal 1",
  "type": "terminal",
  "cwd": "{workspace}",
  "command": "claude",
  "shell": "wt-pwsh",
  "placement": {
    "screen": 2,
    "layout": { "mode": "thirds", "slot": 1 }
  }
}
```

An app entry, here Chrome with explicit profile flags:

```json
{
  "name": "Chrome - Default Profile",
  "type": "app",
  "path": "C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe",
  "args": [
    "--user-data-dir={localappdata}\\Google\\Chrome\\User Data",
    "--profile-directory=Default"
  ]
}
```

- `placement` puts the window after launch. It takes a `screen`, a `layout`
  (`mode` of `full`, `halves`, `thirds`, `two-thirds`, `quarters` or `columns`, a
  `slot`, and an `orientation` for halves), and an optional `subdivide` with the
  same shape to split that region again.
- `columns` is resolution aware: `{"mode": "columns", "slot": 3, "min_width": 800}`
  splits the screen into as many columns at least `min_width` pixels wide (default
  800) as its live work area holds. A 2560px screen gets three and a 1920px screen
  two. An entry whose slot is past the last column is switched off for that run, as
  if `disabled`. When the screen's width can't be read, the plan is taken as
  written: as many columns as the highest slot on that desktop and screen.
  `columns` goes in `layout` only, not in `subdivide`.
- Screens fold when fewer are connected than the plan names. On each desktop,
  every screen from the last connected one up shares that screen as equal columns,
  one per screen the desktop uses, in screen order, and each window keeps its own
  layout inside its column. With two screens, a desktop that uses screens 1-3 puts
  2 and 3 side by side on screen 2; with only a laptop screen, all three share it
  as thirds. `restore` and `align` print `fewer screens than planned: screens 2-3
  share screen 2`. The plan file is never rewritten, so the next `align` tick after
  the screen comes back puts every window back where the plan says.
- `disabled: true` keeps an entry in the plan but skips it.
- `feature` ties an entry to a [feature flag](feature-flags.md).
- `"command_at_logon": "deferred"` (terminal entries with shell `wt-pwsh` only)
  makes a sign-in restore open the terminal as a bare shell and leave `command` to
  `claude-sessions startup`, which resumes a session that died into it or runs the
  command fresh (see [Claude sessions at sign-in](startup-and-display-guard.md#claude-sessions-at-sign-in)).
  Every other restore runs `command` as usual, so `command` is still required.

## Background apps

Some apps start at sign-in, belong in the tray, and open a window anyway: Docker
Desktop opens its Containers dashboard. The plan's root `background` list names them,
and a full restore puts their windows away after placing every planned window and
before returning to the starting desktop:

```json
"background": [
  {
    "name": "Docker Desktop",
    "window_process_name": "Docker Desktop.exe",
    "summary": "Tray only; the engine keeps running when the dashboard closes."
  }
]
```

- `window_process_name` (required) is the image name whose visible windows are put away.
- `action` is `close` (the default: the window is asked to close, as its close button
  would, which sends a tray app back to the tray) or `minimize`, for an app that
  would quit if its window closed.
- `window_wait_timeout_ms` waits that long for the window of an app that is running
  but has none open yet. Without it there is no wait: only the windows open when
  restore reaches the app are put away, so an app already sitting in the tray costs
  a restore nothing. An app that is not running is never waited on.
- `summary` says why the app is listed; `disabled: true` keeps it but leaves it alone.

Restoring one desktop (`--desktop`) leaves background apps alone, and the invoking
terminal's window is never touched. A dry run lists what would be put away. Prefer
the app's own setting where it has one (Slack's `hideOnStartup`); this list is for
apps without one.

## Placeholders

Paths, arguments and `cwd` can use placeholders:

- `{workspace}`: the primary working directory, for example `C:\code\workspace`.
- `{personal-workspace}`: the personal project directory, for example
  `C:\code\personal\shared-utilities`.
- `{localappdata}`, `{appdata}`, `{userprofile}` and `{programfiles}`: this
  machine's `LOCALAPPDATA`, `APPDATA`, `USERPROFILE` and `ProgramFiles`, where set.
  An alias of the same name wins.
- Any alias declared in a bundle root (see below).

## Bundled plans

A bundle splits the plan into a root file plus one JSON file per desktop. The
machine's plan is `desktop-manager/workspace-plan.bundle.json`, with its desktops
under `desktop-manager/bundles/`.

```json
{
  "version": 1,
  "aliases": {
    "chrome-path": "C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe",
    "work-profile": "Default"
  },
  "bundle": {
    "desktops": [
      { "path": "bundles/work.json" },
      { "path": "bundles/admin.json" },
      { "path": "bundles/personal.json" }
    ]
  }
}
```

- Desktop paths resolve relative to the bundle file.
- `aliases` become placeholders, like `{workspace}`. An alias can reference other
  aliases, so a shared root such as `{code-root}` can derive project paths.
- `tools.desktop.aliases` in this machine's `~/.workspace/config.json` overrides
  or adds aliases, so the tracked bundle holds defaults and no one's profile paths.
  Profile numbers, the workspace checkout and app paths that differ per machine go
  there:

  ```json
  { "tools": { "desktop": { "aliases": {
      "chrome-profile-work": "Profile 4",
      "workspace-root": "D:\\src\\workspace"
  } } } }
  ```

- Each desktop file holds a single desktop object with `name` and `launch`.
- `features` belongs in the bundle root. The entries it toggles live in the
  per-desktop files.

## Exporting the current layout

```bash
rig desktop workspace export-plan --config "%APPDATA%\workspace-manager\windows-workspace.json"
rig desktop workspace export-plan --workspace C:/code/workspace --bundle --config desktop-manager/captured.bundle.json
```

- Export walks the current virtual desktops with the same capture logic as `status`.
- `--workspace` turns desktop 1 terminal windows into terminal entries rooted at
  that workspace.
- `--bundle` writes a bundle root plus one file per desktop.
- The export is a draft. Browser URLs, profiles and app arguments usually need
  editing.

## Validating a plan

```bash
rig desktop workspace validate-plan
rig desktop workspace validate-plan --strict
rig desktop workspace validate-plan --screens 3
```

Validation runs two passes:

- Schema and launch sanity checks.
- Structural coverage for each desktop across the monitor set: it reports empty
  desktops, unused screens and uncovered regions.

`--strict` also fails on overlapping regions. `--screens N` enforces a monitor
count when detection is unavailable.
