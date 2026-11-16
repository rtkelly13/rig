# orca-lost-tabs

Read-only recovery tool for Orca agent tabs lost in a restart. It compares the
newest `profile-state.db.backup.*.db` saved before the running Orca started with
the live state, and prints an `orca terminal create` line per lost tab.

```bash
uv run python -I tools/orca-lost-tabs/orca_lost_tabs.py --orca-data "$APPDATA/orca"
```

It lives here rather than in `skills/` because it outlived the
`claude-session-recover` skill: that skill drove `desktop-manager claude-sessions`,
which was retired in #224, while this script only reads Orca's SQLite backup and
has no dependency on the deleted CLI. `docs/orca.md` is the guide.

Standard library only, so it runs without the repo's environment.
