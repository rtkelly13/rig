---
type: Decision
title: No Node at runtime for the C4 viewer
description: Pele renders in the browser from a vendored build; rig's Python server never shells out to Node.
generated: { by: claude-code/opus-5-5, at: 2026-11-04 }
---

**Context.** Pele is JavaScript. rig's web control plane is a stdlib Python server that deliberately avoids
spawning Node processes (`experiments/README.md`: zero resource waste).

**Decision.** Vendor `pele.global.js` (1.13.0) beside the viewer, as three.js already is. Python reads diagram
*structure* with a small line-level reader; Pele does the parsing and rendering in the browser. Full Pele
validation of every block belongs to the mermaid-toolkit lint, which already renders each block through Pele.

**Consequences.** Two readers of the same source: a diagram Pele rejects can still pass `rig c4 check`. Run
`mermaid-lint` over the bundle as well.
