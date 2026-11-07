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
- `disabled: true` keeps an entry in the plan but skips it.
- `feature` ties an entry to a [feature flag](feature-flags.md).
- `"command_at_logon": "deferred"` (terminal entries with shell `wt-pwsh` only)
  makes a sign-in restore open the terminal as a bare shell and leave `command` to
  `claude-sessions startup`, which resumes a session that died into it or runs the
  command fresh (see [Claude sessions at sign-in](startup-and-display-guard.md#claude-sessions-at-sign-in)).
  Every other restore runs `command` as usual, so `command` is still required.

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
uv run desktop-manager workspace export-plan --config "%APPDATA%\workspace-manager\windows-workspace.json"
uv run desktop-manager workspace export-plan --workspace C:/code/workspace --bundle --config desktop-manager/captured.bundle.json
```

- Export walks the current virtual desktops with the same capture logic as `status`.
- `--workspace` turns desktop 1 terminal windows into terminal entries rooted at
  that workspace.
- `--bundle` writes a bundle root plus one file per desktop.
- The export is a draft. Browser URLs, profiles and app arguments usually need
  editing.

## Validating a plan

```bash
uv run desktop-manager workspace validate-plan
uv run desktop-manager workspace validate-plan --strict
uv run desktop-manager workspace validate-plan --screens 3
```

Validation runs two passes:

- Schema and launch sanity checks.
- Structural coverage for each desktop across the monitor set: it reports empty
  desktops, unused screens and uncovered regions.

`--strict` also fails on overlapping regions. `--screens N` enforces a monitor
count when detection is unavailable.
