# Feature flags

A feature flag groups launch entries, so a whole layout variant can be paused
without deleting its configuration. Tag an entry (or a desktop ref) with a
`feature` name. The flags themselves are [rig flags](../feature-flags.md): each one's
default is declared in `src/Rig.Core/Flags/FeatureFlags.cs`, and `rig flags enable|disable`
stores a machine override in `rig.db`.

A plan resolves each flag in this order, the later winning:

1. the registry default;
2. the plan's own `features` block, which pins a flag for that plan (the machine
   bundle has none; test plans use it);
3. an override stored with `rig flags enable|disable`;
4. `--enable` / `--disable` for one run.

```json
{
  "name": "Personal Orca",
  "type": "app",
  "path": "{orca-path}",
  "feature": "orca-sessions"
}
```

- When a feature is off, its entries are disabled exactly as `disabled: true` would
  disable them. Restore, placement, validation and the workspace diff all skip them.
- An entry with `disabled: true` stays disabled even when its feature is on. The
  per-entry switch wins.
- Entries with no `feature` are unaffected.
- An entry that names a feature neither the registry nor the plan declares is
  treated as off. `workspace features` exits non-zero and lists these entries, so a typo cannot quietly drop a window.

## Overriding for one run

```bash
# What is on, what is paused, and which entries belong to each
rig desktop workspace features

# Try the Lab desktop for one restore without the Orca windows
rig desktop workspace restore --enable experimental-desktop --disable orca-sessions

# Both flags are repeatable and also work on audit and validate-plan
rig desktop workspace audit --enable experimental-desktop
```

The command fails, rather than restoring the wrong layout, when you name an
undeclared feature or put the same feature in both `--enable` and `--disable`.

## Desktop features

A desktop ref in the bundle can carry a `feature` too. For example,
`personal-desktop` gates the Personal desktop.

- A desktop behind a feature that is off is dropped from the plan, not left empty.
- Restore **retires** it. Any virtual desktop with a switched-off desktop's name is
  removed, wherever it sits, before the desktop count is topped up. So switching off
  a desktop in the middle, such as Personal ahead of Lab, doesn't leave its old
  desktop to be renamed after the next one. Windows moves the removed desktop's
  windows onto the last planned desktop, so nothing is closed.
- Extra desktops with other names are left alone.
- `--enable personal-desktop` creates and names it again.

## Flags in the machine plan

- `orca-sessions` (on) opens the Work and Personal Orca windows on screens 1 and 2
  of the Work desktop. The earlier Windows Terminal, Warp, work-Chrome and Fork
  variants were deleted when the plan moved to two screens; git history keeps them.
- `personal-desktop` and `experimental-desktop` (both off) keep the Personal and
  Lab desktops as opt-ins, and while off they retire any leftover desktop of that
  name.
