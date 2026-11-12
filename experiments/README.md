# Experiments in Rig

This directory houses technical, visual, and architectural experiments directly inside **`rig`**, consolidating developer tooling and prototypes into one unified environment.

---

## Active Experiments

| Experiment | Focus & Technologies | Location / Launch |
|---|---|---|
| **Logo explorers** | Unscored rhombille-hexagon and interlocking-columns marks, moved out of `rig.image_drift`. Not type-checked, ratcheted or run in CI. | [`logo-explorers/`](logo-explorers/README.md) |
| **[C4 as OKF](c4-okf/README.md)** | C4 diagrams kept as an Open Knowledge Format bundle: zoom-in across documents, composition (fan-in, merged model, expand), level and refinement checks; rendered by vendored Pele. | `rig c4 check` · `rig web` → sidebar **C4 architecture**, or `/c4/` |
| **Asset Drift Reconciler** | Deterministic vectorization, SSIM / PSNR / RMSE scoring, and decomposed luminance vs chrominance drift diagnostics. | `uv run --project tools/image-drift image-reconcile serve` |

---

## Architecture & Principles

1. **Zero Resource Waste**: Experiments run on-demand or inside `rig web`'s low-footprint Python server (`~25 MB RAM`, `0% CPU` idle) instead of spawning separate Node/Next.js servers.
2. **Direct Asset Pipeline**: Visual experiments can directly export production cuts to `assets/<project>/` and estate clones.
3. **Estate Visibility**: Experiments are directly accessible through the `rig web` control plane dashboard.
