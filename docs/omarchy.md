# Omarchy

Omarchy is the Linux desktop on this machine (Hyprland, `omarchy-shell`, and the `omarchy` CLI in `/usr/share/omarchy`).
`config/omarchy.json` declares how the desktop should be set up; `rig omarchy doctor` checks the machine against it, and
`rig omarchy sync --apply` writes the differences.

```bash
rig omarchy doctor          # table; exits 1 on any difference
rig omarchy doctor --json

rig omarchy sync            # dry-run: plan the changes
rig omarchy sync --apply    # write them
```

Both commands take `--config <path>` to read a policy other than the checkout's `config/omarchy.json`.

Settings live in user files under `~/.config/omarchy` and `~/.local/state/omarchy`, so the sync writes them directly.
One system file is owned by rig: `/etc/systemd/logind.conf.d/90-rig-omarchy.conf`, written with `sudo install` and
followed by `systemctl reload systemd-logind`. Only `sync --apply` asks for a password; a dry run never prompts. Nothing
under `/usr/share/omarchy` is edited.

## What `config/omarchy.json` declares

A section left out is neither checked nor written.

| Key | Meaning | |---|---| | `idle.screensaver_seconds` | Seconds of idleness before the screensaver starts. `0`
disables it; the policy uses 1800. | | `idle.lock_seconds` | Seconds of idleness before the session locks. `0` disables
the idle lock; the policy uses 3600. | | `suspend.menu` | Whether Suspend appears in Omarchy's system menu. `false`
writes the `suspend-off` toggle. | | `suspend.lid` | logind's `HandleLidSwitch`, `HandleLidSwitchExternalPower` and
`HandleLidSwitchDocked`: `ignore`, `suspend`, `hibernate` and so on. | | `suspend.power_key`, `suspend.suspend_key`,
`suspend.hibernate_key`, `suspend.idle_action` | The matching logind keys. | | `lock_before_suspend` | Whether
`omarchy-sleep-lock.service` (a systemd user unit) locks the session before it sleeps. | | `theme` | The theme slug,
such as `tokyo-night`, applied with `omarchy theme set`. | | `power.ac`, `power.battery` | The profiles Omarchy
remembers for AC and battery; applied with `omarchy powerprofiles set` and then `autodetect`. | | `font.base_size` |
Omarchy's text size in px (9–20), read from `~/.config/omarchy/shell.toml` and applied with `omarchy display text size`,
which also scales GTK and the terminal font to match. For overall density, change the monitor scale instead (see
[Density](#density)). | | `hyprland.gaps_in`, `hyprland.gaps_out`, `hyprland.border_size` | Hyprland's window
spacing in px (Omarchy's defaults are 5, 10 and 2; the policy uses 2, 5 and 1). Read live with `hyprctl getoption`
and written as a marked block at the end of `~/.config/hypr/looknfeel.lua`, which Hyprland reloads on save. | | `shell` | Objects deep-merged into `~/.config/omarchy/shell.json`: `bar`, `dock`, `plugins`
and so on. Objects merge key by key; arrays and scalars replace. Keys the policy omits are kept. Do not declare
`shell.idle`: use the top-level `idle` section. |

## Sleep on this machine

Orca, not a blanket "never sleep", keeps this machine up while work is running. With *Keep computer awake while agents
run* on (rig's Orca policy checks it, see [orca.md](orca.md#keep-awake)), Orca holds a systemd `sleep:handle-lid-switch`
block lock for as long as any agent is working; `systemd-inhibit --list` shows it as `Orca … Agents are working`. A
suspend from the menu or a key is refused while that lock is held.

Around that, the policy is long rather than off:

- **Idle**: the screensaver starts after 30 minutes and the session locks after an hour. Omarchy's idle service only
  blanks and locks; it never suspends, so a locked session keeps running agents.
- **Suspend** stays in the system menu for a deliberate sleep when nothing is running.
- **logind** ignores the lid and the power, suspend and hibernate keys, so a stray key press never takes the machine
  down, and the idle action stays `ignore`.

The shell hot-reloads `shell.json`, so idle changes take effect immediately. The logind drop-in is a separate file from
Omarchy's own `/etc/systemd/logind.conf.d` entries; because `90-rig-omarchy.conf` sorts after them, its keys win.

The settings rig manages are checked with `rig omarchy doctor` and written with `rig omarchy sync --apply`. Bar
visibility is a toggle, not policy: rig does not put the bar back on its own, so use `omarchy toggle bar off`.

## Density

Three knobs:

- **Monitor scale** sizes everything Wayland-native: windows, the bar, apps. `omarchy hyprland monitor scaling up` (or
  `down`, or a value such as `1.25`) applies it live and records it in `~/.config/hypr/monitors.lua`. Steps are 1, 1.25,
  1.6, 2, 3 and 4. rig does not manage it.
- **Text size** (`font.base_size` above) sizes text only. Set it in `config/omarchy.json` and run
  `rig omarchy sync --apply`; running `omarchy display text size` by hand works too, but the next sync puts the policy's
  value back.
- **Gaps and border** (`hyprland` above) set the space between windows and around them. rig owns the block between the
  `rig:omarchy:hyprland` markers in `~/.config/hypr/looknfeel.lua`; keep your own settings outside it.

## Cheatsheet

`SUPER + K` opens Omarchy's keybinding list; `omarchy menu keybindings --print` prints it.

| Need | Command | |:---|:---| | Show the top bar | `omarchy toggle bar off` | | Show bar and gaps |
`omarchy toggle fullscreen desktop off` | | Restart the shell | `omarchy restart shell` | | Reset shell config |
`omarchy refresh shell` | | Stay awake | `omarchy toggle idle` | | Hide Suspend | `omarchy toggle suspend` | | Night
light | `omarchy toggle nightlight` | | Do not disturb | `omarchy toggle notification silencing` | | Lock now |
`omarchy system lock` | | Screenshot | `omarchy capture screenshot` |
