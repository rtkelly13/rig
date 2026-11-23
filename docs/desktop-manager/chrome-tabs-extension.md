# Chrome tabs on Linux: the extension

**Parent:** [`desktop-manager/`](README.md) · **Decision:** [ADR 0026](../adr/0026-chrome-tabs-on-linux-through-an-extension.md) ·
**Rules and planning:** [chrome-tabs-uia.md](chrome-tabs-uia.md)

On Linux, `rig chrome-tabs` reads and changes tabs through a small extension of rig's own instead of the screen.
The rules (what `prune` closes, how `group` gathers repos, what `organise --unattended` waits for) are the same code
as on Windows and macOS; only the driver differs.

```text
rig chrome-tabs list ──► $XDG_RUNTIME_DIR/rig/chrome-tabs/<pid>.sock ──► rig (native host, started by Chromium)
                                                                          │ native messaging (stdin/stdout)
                                                                          ▼
                                                                  extensions/chrome-tabs (tabs, tabGroups APIs)
```

## Install

```bash
rig chrome-tabs extension install      # copy, register the host, add to --load-extension
# restart Chromium
rig chrome-tabs extension status       # each piece, then each connected profile
rig chrome-tabs list
```

`install` does three things, and running it again changes only what differs:

| Writes | Why |
|---|---|
| `~/.local/share/rig/chrome-extension/` | The extension, outside the runtime folder that `rig runtime install` replaces |
| `~/.config/<browser>/NativeMessagingHosts/dev.ryankelly.rig_chrome_tabs.json` | Lets the extension start rig as its host, for each browser config folder that exists |
| `--load-extension=…` in `~/.config/chromium-flags.conf` | Chromium loads the folder at start, beside the extensions already listed |

The host manifest names rig on `PATH`, so a runtime update needs no reinstall. To try a development build, name
its binary: `rig chrome-tabs extension install --host-program src/Rig.Cli/bin/Debug/net11.0/rig`.

Google Chrome ignores `--load-extension` from version 137. There, load the folder once from `chrome://extensions`
(Developer mode, Load unpacked); the pinned key keeps its id, so the host manifest still matches.

After changing the extension's files, run `install` again and reload it in `chrome://extensions`.
`rig chrome-tabs extension uninstall` removes all three, leaving the rest of the flags file as it was.

## What differs from Windows and macOS

- **Nothing is brought forward and the mouse is never used**, so grouping does not wait for the machine to be idle.
  Calls still hold everything back: audible tabs come from the snapshot.
- **Every tab has its URL**, as on macOS, so duplicates are matched by address rather than title alone.
- **No desktops and no saved groups**: no extension API reaches either.
- **Profiles** come from the browser's `Local State`: matched by the signed-in email, else the browser's only
  profile, and planned as `<browser>/<folder>` so Chromium's and Chrome's `Default` stay apart. A profile that
  cannot be named is planned on its own (`extension:<pid>`), never merged with another.

## When it does not connect

| `status` says | Check |
|---|---|
| `Extension: missing` | Run `install`. |
| `Host manifest (chromium): names another program` | Run `install` again; the manifest names an old build. |
| `Chromium flags: --load-extension does not name the extension` | An Omarchy update rewrote the file; run `install` again. |
| `Connected: none` | Chromium was not restarted, or the extension is off in `chrome://extensions`. Its service worker's console shows host errors such as `Specified native messaging host not found`. |

A host from a crashed browser leaves its socket behind; the next command finds nothing listening and removes it.
