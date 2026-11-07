---
type: C4 Context
title: Rig — system context
description: Who uses rig and which outside systems it drives.
resource: https://github.com/rtkelly13/rig
tags: [rig, c4, estate]
generated: { by: claude-code/opus-5-5, at: 2026-11-04 }
---

```mermaid
C4Context
title Rig — system context
Person(ryan, "Ryan", "Owns the estate; runs rig from a terminal and the dashboard")
System(rig, "rig", "Personal estate orchestration: governance, desktops, agents, assets", $link="rig/containers.md")
System_Ext(github, "GitHub", "Repos, PRs, rulesets across rtkelly13 / rtkelly-labs / Resultful")
System_Ext(vault, "Obsidian vault", "Knowledge base; OKF bundles")
System_Ext(agents, "Coding agents", "Claude Code, OpenCode, Codex")
System_Ext(desktop, "Desktop OS", "Windows virtual desktops, macOS LaunchAgents")
System_Ext(vercel, "Vercel", "Hosting for estate sites")

Rel(ryan, rig, "Runs commands, opens the dashboard")
Rel(rig, github, "Governs repos, lands PRs", "gh CLI")
Rel(rig, vault, "Lints and syncs notes")
Rel(rig, agents, "Launches and places sessions")
Rel(rig, desktop, "Restores workspaces, installs jobs")
Rel(rig, vercel, "Declares sites", "Pulumi")
```

The root of this bundle. Click **rig** to zoom into its containers.

## rig

One Python repo (`uv`), several CLIs. Read [the container view](rig/containers.md) for the split. Governance
policy lives in the repo, not here: this bundle describes *shape*, not *rules*.

## GitHub

Every push and merge is checked against the remote owner first; work credentials are live on the same machine.

## Coding agents

Agents are launched by `rig agent`, and their terminals are placed by `desktop-manager claude-sessions`.
