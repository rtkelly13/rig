# ADR 0026: Chrome Tabs on Linux Work Through rig's Own Extension

- **Status:** Proposed
- **Applies to:** `rig chrome-tabs` on Linux; `extensions/chrome-tabs`, `src/Rig.Core/Chrome/Extension/`
- **Related:** the Windows UI Automation and macOS drivers ([chrome-tabs-uia.md](../desktop-manager/chrome-tabs-uia.md));
  the archived tab-tidy extension design (rig-archive PR #158), which this revives in a smaller form

## Context

`rig chrome-tabs` plans in platform-neutral code (`src/Rig.Core/Chrome/`: ranking, PR pruning, de-duplication,
grouping, the unattended organise) and acts through a `ChromeSession`, whose drivers are platform adapters: UI
Automation on Windows, scripting plus accessibility on macOS. Linux had no driver, so only `traces` and a refusing
`organise` ran there.

A screen-driven driver for Linux would be the weakest of the three. Chromium on Wayland exposes its tab strip
through AT-SPI only when accessibility is switched on, Wayland gives a client no way to raise another's window or
move the pointer, and Hyprland, GNOME and KDE each move windows differently. The Chrome DevTools Protocol was ruled
out by the earlier design: it cannot see tab groups and cannot attach to the default profile without restarting the
browser with a debugging port.

The archived design put all the logic in a WXT extension with a Python relay. Installation was the problem on a
managed Windows machine, where unpacked extensions are blocked; on a personal Linux machine it is not.

## Decision

1. **The extension is a thin adapter.** `extensions/chrome-tabs` (plain JavaScript, no build step) answers seven
   commands over Chrome's `tabs`, `tabGroups` and `windows` APIs: `hello`, `windows`, `close`, `activate`, `group`,
   `ungroup`, `move`. It holds no policy. Every rule stays in `Rig.Core.Chrome`, shared with Windows and macOS.
2. **rig is the native-messaging host.** Chrome starts `rig chrome-extension://<id>/`; `Main` rewrites that to
   `chrome-tabs extension host`, which relays between Chrome's pipe and a per-process Unix socket in
   `$XDG_RUNTIME_DIR/rig/chrome-tabs/<pid>.sock` (mode 0700). A `rig chrome-tabs` command connects to every socket
   there, one per running profile. Native messaging is the only channel Chrome offers an extension to a local
   program, and the host must be started by Chrome, so the socket is how a later command reaches it.
3. **The id is pinned** by the public `key` in the manifest (`gpfpmehjedofmgdbecdcieeempijapej`), so the host
   manifest's `allowed_origins` is right wherever the folder is loaded from. A test derives the id from the key.
4. **`ChromeExtension` implements the existing driver interfaces.** Window ids rise with creation, so they serve as
   handle and age. Each step reads again first and refuses a tab that moved, changed title or URL, or went away,
   the same check the macOS driver makes. Grouping mirrors macOS: a new group needs a title unique in its profile,
   joining needs the one group with that title in the home window. Saved groups are not reachable through any
   extension API, so there are none.
5. **Presence is always away.** Nothing waits for the user to stop typing: the extension never takes the keyboard,
   the mouse or the foreground. Call protection still holds, because it reads audible tabs from the snapshot.
6. **`rig chrome-tabs extension install`** copies the extension to `~/.local/share/rig/chrome-extension` (outside the
   runtime folder, which updates replace), writes the host manifest into each browser's `NativeMessagingHosts`
   naming rig's PATH shim, and adds the folder to `--load-extension` in `~/.config/chromium-flags.conf` beside
   whatever is there. Google Chrome ignores that flag from version 137, so there the folder is loaded once by hand.
7. **Profiles** are named from the browser's `Local State`: the signed-in email when there is one, else the
   browser's only profile, else a scope of their own so plans never mix profiles.

## Consequences

- Linux gets `list`, `rank`, `prune`, `group` and `organise` with every rule the other platforms have, and they run
  faster than either: a read takes about 20 ms and a close about 10 ms, with no window brought forward.
- The extension must be loaded and connected before anything works; `extension status` says which profiles are.
- A runtime update needs no reinstall (the host is the PATH shim), but a change to the extension's files needs
  `extension install` again and a reload in `chrome://extensions`.
- Omarchy owns `chromium-flags.conf`; if an update rewrites it, `extension install` puts the entry back.
- A host from a crashed browser leaves its socket; the next command finds nothing listening and removes it.
- The same extension would work on Windows and macOS, but their drivers stay: on the managed Windows machine an
  unpacked extension cannot be installed, which is why the screen-driven drivers exist.
