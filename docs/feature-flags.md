# Feature flags

Flags that any rig tool can read live in one place. Each flag's name, default and
purpose are declared in `src/Rig.Core/Flags/FeatureFlags.cs`. A stored override in the
`feature_flags` table of `rig.db` (`%LOCALAPPDATA%\rig\rig.db`)
replaces the default until it is reset.

```bash
rig flags list                          # effective state, default, override, purpose
rig flags enable claude-session-restore # store an override
rig flags disable orca-sessions
rig flags reset orca-sessions           # back to the default
```

- Change a default by editing the registry in a PR. Use an override for a
  machine-local switch.
- Naming an undeclared flag fails, so a typo cannot silently do nothing.
- In C#, read flags through `Rig.Core.Flags.FlagStore` (`IsEnabled`, `Effective`,
  `States`). It creates the `feature_flags` table if it is missing, so a new
  machine needs no setup step.
- The Python copy of the registry and store (`rig.flags`) is deleted (#281). The
  C# tests replay `tests/Rig.Cli.Tests/Flags/flags-oracle.json`, the list the
  Python registry recorded; a new flag goes in the C# registry and, by hand, in
  that fixture: its `flags` list and the rows of each `list` step.
