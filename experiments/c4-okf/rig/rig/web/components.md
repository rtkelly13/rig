---
type: C4 Component
title: Web control plane — components
description: The handler, its route modules and the static pages it serves.
resource: https://github.com/rtkelly13/rig/tree/main/src/rig/image_drift
tags: [rig, web, c4]
generated: { by: claude-code/opus-5-5, at: 2026-11-04 }
---

```mermaid
C4Component
title Web control plane — components
Person(ryan, "Ryan")
Container(dm, "desktop-manager", "Python")
ContainerDb(db, "rig.db", "SQLite", $link="../rig-db.md")

Container_Boundary(web, "Web control plane") {
  Component(server, "StudioServer", "ThreadingHTTPServer", "Routes /api/* and static files", $link="request-handler.md")
  Component(indexer, "AssetIndexer", "Python", "Indexes assets/ and temp/variations/")
  Component(c4, "C4 viewer", "rig.c4_okf", "OKF bundle API and zoom UI")
  Component(studio, "studio.js", "Vanilla JS, Three.js", "Estate, Asset Studio, 3D prism tabs")
  Component(c4ui, "c4.js", "Vanilla JS, Pele", "Renders diagrams, zooms on click")
}

Rel(ryan, studio, "Uses")
Rel(ryan, c4ui, "Uses")
Rel(studio, server, "Fetches", "JSON")
Rel(c4ui, c4, "Fetches", "JSON")
Rel(server, indexer, "Catalogues assets")
Rel(server, c4, "Delegates /c4 and /api/c4")
Rel(server, db, "Reads runs, saves presets")
Rel(dm, server, "Starts")
```

## C4 viewer

This experiment. The decision to render in the browser rather than in a Node process is recorded in
[no Node at runtime](decisions/no-node-at-runtime.md).

## StudioServer

`server.py` sits at the 800-line ratchet cap, which is why the C4 routes live in their own module and the server
only delegates to them.
