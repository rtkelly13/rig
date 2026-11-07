---
type: C4 Code
title: rig.db — schema
description: The shared SQLite schema, written by the jobs runner and read by the dashboard.
resource: https://github.com/rtkelly13/rig/blob/main/src/rig/db/migrations.py
generated: { by: claude-code/opus-5-5, at: 2026-11-04 }
---

```mermaid
erDiagram
  job_runs {
    INTEGER id PK
    TEXT job_name
    TEXT trigger
    TEXT environment
    TEXT started_at
    TEXT finished_at
    TEXT status
    INTEGER exit_code
  }
  schema_migrations {
    INTEGER version PK
    TEXT name
    TEXT applied_at
  }
```

Linked from both the web and the desktop-manager component views; `rig c4 check` reports it as `fan-in`.
