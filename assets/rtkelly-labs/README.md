# rtkelly-labs Brand Identity

Visual identity and production cuts for the GitHub organization
**[rtkelly-labs](https://github.com/rtkelly-labs)**.

## Motif

- **Interlocking hexagonal prisms** — two hollow isometric hexagonal loops on strict
  30°/90°/150° isometric axes.
- **Clean crossover beams** — at the centre the diagonal beams cross, one passing cleanly over
  the other with ambient occlusion beneath. **There are no longitudinal seams, cuts, or
  dividing lines running down the beam faces.** This is the invariant the mark was designed
  around; early auto-traces kept drawing an outline stroke where the neon highlight met the
  darker facet, which cut the central diagonal in half lengthwise.
- **Planar lighting** — luminous electric cyan (`#00F2FE`) through vibrant azure (`#0C96D6`)
  to deep cobalt navy (`#153C9E`).

## Where the master lives

The master is a **trace** of the approved reference raster, not computed geometry. Nothing can
recompute it, so the SVG itself is the artefact of record:

| Role | Path |
|---|---|
| **Authoritative master** | [`src/shared_utilities/image_drift/builder_assets/rtkelly-labs/master.svg`](../../src/shared_utilities/image_drift/builder_assets/rtkelly-labs/master.svg) |
| Authoritative favicon cut | [`.../builder_assets/rtkelly-labs/favicon.svg`](../../src/shared_utilities/image_drift/builder_assets/rtkelly-labs/favicon.svg) |
| Digests and provenance | [`.../builder_assets/rtkelly-labs/manifest.json`](../../src/shared_utilities/image_drift/builder_assets/rtkelly-labs/manifest.json) |
| Build output (committed) | [`rtkelly-labs.svg`](./rtkelly-labs.svg) |

`image-reconcile build` writes `rtkelly-labs.svg` from the packaged master, and a test asserts
the two stay byte-identical. **The only sanctioned way to change the master is
`image-reconcile promote`**, which requires a candidate to beat the incumbent on score. A
hand-edit is caught by the manifest digest test.

## Reference material

[`reference/`](./reference/) holds the ground truth, ordered by sequence, with
[`reference/manifest.json`](./reference/manifest.json) naming which one is primary.

| File | Role |
|---|---|
| [`reference/02-smooth-crossover-baseline.jpg`](./reference/02-smooth-crossover-baseline.jpg) | **Primary baseline** — all drift is scored against this |
| [`reference/01-original-concept.jpg`](./reference/01-original-concept.jpg) | Historical first concept |
| [`reference/rtkelly-labs.png`](./reference/rtkelly-labs.png) | 1024×1024 raster export of the baseline |

## Production cuts

Every cut lives in [`production-cuts/`](./production-cuts/) and is declared in
[`production-cuts/production-cuts.csv`](./production-cuts/production-cuts.csv). Files not
declared there are rogue: ignored by the catalog indexer, excluded from distribution, and
failed by `tests/test_brand_asset_governance.py`.

| File | Size | Purpose |
|---|---|---|
| [`rtkelly-labs.svg`](./production-cuts/rtkelly-labs.svg) | vector | Canonical vector cut (copy of the master) |
| [`rtkelly-labs-logo-2000x2000.png`](./production-cuts/rtkelly-labs-logo-2000x2000.png) | 2000² | Ultra high-resolution cut |
| [`rtkelly-labs-logo-1000x1000.png`](./production-cuts/rtkelly-labs-logo-1000x1000.png) | 1000² | Master raster cut |
| [`avatar-500x500.png`](./production-cuts/avatar-500x500.png) | 500² | GitHub organization avatar |
| [`avatar-180x180.png`](./production-cuts/avatar-180x180.png) | 180² | Web and mobile profile |
| [`apple-touch-icon.png`](./production-cuts/apple-touch-icon.png) | 180² | iOS Safari touch icon |
| [`favicon-512.png`](./production-cuts/favicon-512.png) | 512² | PWA manifest icon |
| [`favicon.svg`](./production-cuts/favicon.svg) | vector | High-contrast small-size cut |
| [`favicon-48/32/16.png`](./production-cuts/) | 48/32/16² | Browser tab favicons |
| [`favicon.ico`](./production-cuts/favicon.ico) | 48/32/16 | Legacy favicon bundle |

**The favicon is a separate drawing, not the master scaled down.** Below roughly 48px the
master's facets and drop shadow collapse into mud, so `favicon.svg` reuses the master's loop
and beam geometry with two flat tones and no shadow. `favicon-48/32/16.png` and `favicon.ico`
are rendered from it; everything larger comes from the master.

## Commands

```bash
# Regenerate the committed asset from the packaged master
uv run image-reconcile build -p rtkelly-labs

# Export every declared production cut
uv run image-reconcile export-avatar -p rtkelly-labs

# Score the master against the primary baseline
uv run image-reconcile compare -p rtkelly-labs --render

# Attribute drift to geometry (luminance) vs palette (chrominance)
uv run image-reconcile diagnose -p rtkelly-labs --render

# Score the master beside a fresh VTracer trace and the constructive pathways
uv run image-reconcile pathways -p rtkelly-labs

# Propose parametric alternatives; promotion is a separate, gated step
uv run image-reconcile explore -p rtkelly-labs --sweep r_outer=150,158,166
```

See [`docs/brand-assets/README.md`](../../docs/brand-assets/README.md) for the governing model and
[ADR 0018](../../docs/adr/0018-brand-asset-governance.md) for why it is shaped this way.
