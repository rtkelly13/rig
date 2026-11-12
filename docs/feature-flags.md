# Feature flags

Flags that any rig tool can read live in one place. Each flag's name, default and
purpose are declared in `src/Rig.Core/Flags/FeatureFlags.cs`. A stored override in the
`feature_flags` table of `rig.db` (`%LOCALAPPDATA%\shared-utilities\rig.db`)
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
- Python's `desktop_manager` still reads flags through `rig.flags`, a copy of
  the registry and store kept until its last caller is ported (#85 s15/s28).
  `tests/test_rig_flags.py` holds that copy to the list recorded in
  `tests/Rig.Cli.Tests/Flags/flags-oracle.json`, which the C# tests replay, so
  a flag added on one side only fails CI. Declare a new flag in both until then.
