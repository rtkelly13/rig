# Feature flags

Flags that any rig tool can read live in one place. Each flag's name, default and
purpose are declared in `src/rig/flags/registry.py`. A stored override in the
`feature_flags` table of `rig.db` (`%LOCALAPPDATA%\shared-utilities\rig.db`)
replaces the default until it is reset.

```bash
uv run rig flags list                          # effective state, default, override, purpose
uv run rig flags enable claude-session-restore # store an override
uv run rig flags disable orca-sessions
uv run rig flags reset orca-sessions           # back to the default
```

- Change a default by editing the registry in a PR. Use an override for a
  machine-local switch.
- Naming an undeclared flag fails, so a typo cannot silently do nothing.
- In code, call `rig.flags.is_enabled(name)` or `rig.flags.effective_flags()`.
  Both run pending migrations first, so a new machine needs no setup step.
