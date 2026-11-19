# Omarchy

Omarchy is the Linux desktop on this machine (Hyprland, `omarchy-shell`, and the `omarchy`
CLI in `/usr/share/omarchy`). `config/omarchy.json` declares how the desktop should be set
up; `rig omarchy doctor` checks the machine against it, and `rig omarchy sync --apply`
writes the differences.

```bash
rig omarchy doctor          # table; exits 1 on any difference
rig omarchy doctor --json

rig omarchy sync            # dry-run: plan the changes
rig omarchy sync --apply    # write them
```

Both commands take `--config <path>` to read a policy other than the checkout's
`config/omarchy.json`.

Settings live in user files under `~/.config/omarchy` and `~/.local/state/omarchy`, so the
sync writes them directly. One system file is owned by rig:
`/etc/systemd/logind.conf.d/90-rig-omarchy.conf`, written with `sudo install` and followed by
`systemctl reload systemd-logind`. Only `sync --apply` asks for a password; a dry run never
prompts. Nothing under `/usr/share/omarchy` is edited.

## What `config/omarchy.json` declares

A section left out is neither checked nor written.

| Key | Meaning |
|---|---|
| `idle.screensaver_seconds` | Seconds of idleness before the screensaver starts. `0` disables it. |
| `idle.lock_seconds` | Seconds of idleness before the session locks. `0` disables the idle lock. |
| `suspend.menu` | Whether Suspend appears in Omarchy's system menu. `false` writes the `suspend-off` toggle. |
| `suspend.lid` | logind's `HandleLidSwitch`, `HandleLidSwitchExternalPower` and `HandleLidSwitchDocked`: `ignore`, `suspend`, `hibernate` and so on. |
| `suspend.power_key`, `suspend.suspend_key`, `suspend.hibernate_key`, `suspend.idle_action` | The matching logind keys. |
| `lock_before_suspend` | Whether `omarchy-sleep-lock.service` (a systemd user unit) locks the session before it sleeps. |
| `theme` | The theme slug, such as `tokyo-night`, applied with `omarchy theme set`. |
| `power.ac`, `power.battery` | The profiles Omarchy remembers for AC and battery; applied with `omarchy powerprofiles set` and then `autodetect`. |
| `font.base_size` | The shell's base font size in `~/.config/omarchy/shell.toml`. |
| `shell` | Objects deep-merged into `~/.config/omarchy/shell.json`: `bar`, `dock`, `plugins` and so on. Objects merge key by key; arrays and scalars replace. Keys the policy omits are kept. Do not declare `shell.idle`: use the top-level `idle` section. |

## Sleep on this machine

This desktop never idle-locks, suspends or hibernates: the idle timers are `0`, Suspend is
hidden from the menu, and logind ignores the lid and every power key. The policy is the
record of that intent, so a fresh install can be brought to the same state with one
`rig omarchy sync --apply`.

The shell hot-reloads `shell.json`, so idle changes take effect immediately. The logind
drop-in is a separate file from Omarchy's own `/etc/systemd/logind.conf.d` entries; because
`90-rig-omarchy.conf` sorts after them, its keys win.

The settings rig manages are checked with `rig omarchy doctor` and written with
`rig omarchy sync --apply`. Bar visibility is a toggle, not policy: rig does not put the bar
back on its own, so use `omarchy toggle bar off`.

## Cheatsheet

`SUPER + K` opens Omarchy's keybinding list; `omarchy menu keybindings --print` prints it.

| Need | Command |
|:---|:---|
| Show the top bar | `omarchy toggle bar off` |
| Show bar and gaps | `omarchy toggle fullscreen desktop off` |
| Restart the shell | `omarchy restart shell` |
| Reset shell config | `omarchy refresh shell` |
| Stay awake | `omarchy toggle idle` |
| Hide Suspend | `omarchy toggle suspend` |
| Night light | `omarchy toggle nightlight` |
| Do not disturb | `omarchy toggle notification silencing` |
| Lock now | `omarchy system lock` |
| Screenshot | `omarchy capture screenshot` |
