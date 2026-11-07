---
type: C4 Container
title: Rig — containers
description: The CLIs, the web control plane and the shared SQLite store inside rig.
resource: https://github.com/rtkelly13/rig/tree/main/src
tags: [rig, c4]
generated: { by: claude-code/opus-5-5, at: 2026-11-04 }
---

```mermaid
C4Container
title Rig — containers
Person(ryan, "Ryan")
System_Ext(github, "GitHub")
System_Ext(vault, "Obsidian vault")
System_Ext(agents, "Coding agents")
System_Ext(desktop, "Desktop OS")
System_Ext(vercel, "Vercel")

System_Boundary(rig, "rig") {
  Container(rigcli, "rig CLI", "Python, Typer", "workspace, estate, repo-governance, vault, agent", $link="cli.md")
  Container(dm, "desktop-manager", "Python, Typer", "Desktop IaC, claude-sessions, jobs, web launcher", $link="desktop-manager/components.md")
  Container(web, "Web control plane", "http.server, vanilla JS", "Estate dashboard, Asset Studio, C4 viewer", $link="web/components.md")
  ContainerDb(db, "rig.db", "SQLite, WAL", "Job runs, triage and feature flags", $link="rig-db.md")
  Container(infra, "Vercel infra", "TypeScript, Pulumi", "sites.ts registry")
}

Rel(ryan, rigcli, "Runs")
Rel(ryan, web, "Browses", "HTTP :8080")
Rel(rigcli, github, "Governs", "gh CLI")
Rel(rigcli, vault, "Lints, syncs")
Rel(rigcli, agents, "Launches")
Rel(dm, desktop, "Restores, schedules")
Rel(dm, agents, "Places sessions")
Rel(dm, db, "Records job runs")
Rel(web, db, "Reads and backs up job runs")
Rel(dm, web, "Starts and installs", "LaunchAgent / Task Scheduler")
Rel(infra, vercel, "Declares", "Pulumi")
```

Two containers have no deeper view yet; `rig CLI` zooms into a plain note, which `rig c4 check` reports as an
information finding rather than an error.

## rig.db

Shared by the jobs runner and the dashboard. Its code-level view is linked from both component diagrams below:
one document composed into two views.
