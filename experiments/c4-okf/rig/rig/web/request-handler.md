---
type: C4 Code
title: RigWebHost — code
description: The ASP.NET Core host behind the web control plane and its route modules.
resource: https://github.com/rtkelly13/rig/blob/main/src/Rig.Web/RigWebHost.cs
generated: { by: claude-code/opus-5-5, at: 2026-11-04 }
---

```mermaid
classDiagram
  class RigWebHost {
    <<sealed>>
    +StartAsync()
    +DisposeAsync()
  }
  class C4Routes {
    <<static>>
    +Map(app)
  }
  RigWebHost --> C4Routes : maps /c4 and /api/c4
  click C4Routes href "components.md"
```

The code level is where C4 says to stop drawing and link to the code: `resource` points at the file.
