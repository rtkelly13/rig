# Parquet.SourceGenerator Brand Identity

This directory houses the official visual brand identity and production cuts for **[Parquet.SourceGenerator](file:///Users/ryankelly/code/personal/Parquet.SourceGenerator)**.

## Visual Identity Motif
- **Diamond Tile**: An isometric parquet diamond pattern built from warm oak wood grain planks.
- **Git Fork Triad**:
  - **Trunk**: Electric Cyan (`#00F5FF`) with commit node ring.
  - **Left Branch**: Radiant Neon Violet (`#C084FC`) fork with commit node ring.
  - **Right Branch**: Solar Amber (`#F59E0B`) fork blending into warm parquet oak tones with commit node ring.
- **Outer Casing & Isolation**: Dark casing channel preventing color bleed onto wood planks.

## Reference material

[`reference/`](./reference/) holds the ground truth, with
[`reference/manifest.json`](./reference/manifest.json) naming the primary baseline.

| File | Role |
|---|---|
| [`reference/02-simplified-cad-baseline.jpg`](./reference/02-simplified-cad-baseline.jpg) | **Primary baseline** — drift is scored against this |
| [`reference/01-original-wood-tile.jpg`](./reference/01-original-wood-tile.jpg) | Historical first concept |
| [`reference/parquet-diamond.png`](./reference/parquet-diamond.png) | 1024×1024 raster export |

## Production cuts

Every cut lives in [`production-cuts/`](./production-cuts/) and is declared in
[`production-cuts/production-cuts.csv`](./production-cuts/production-cuts.csv). Undeclared
files there are rogue and are failed by `tests/test_brand_asset_governance.py`.

| File | Dimensions | Purpose |
|---|---|---|
| [`parquet-diamond.svg`](./parquet-diamond.svg) | Vector | Canonical master mark (project root) |
| [`parquet-diamond-2000x2000.png`](./production-cuts/parquet-diamond-2000x2000.png) | 2000×2000 | Ultra high-resolution cut |
| [`parquet-diamond-1000x1000.png`](./production-cuts/parquet-diamond-1000x1000.png) | 1000×1000 | Master raster cut |
| [`avatar-500x500.png`](./production-cuts/avatar-500x500.png) | 500×500 | Repository profile avatar |
| [`avatar-180x180.png`](./production-cuts/avatar-180x180.png) | 180×180 | Web and mobile profile cut |
| [`apple-touch-icon.png`](./production-cuts/apple-touch-icon.png) | 180×180 | iOS Safari touch icon |
| [`favicon.svg`](./production-cuts/favicon.svg) | Vector | Simplified small-size variant |
| [`favicon-512.png`](./production-cuts/favicon-512.png) | 512×512 | PWA manifest icon |
| [`favicon-{48,32,16}.png`](./production-cuts/) | Multi-size | Browser tab favicons |
| [`favicon.ico`](./production-cuts/favicon.ico) | 48/32/16 | Legacy favicon bundle |

## Generation & Verification

```bash
# Export every declared production cut into production-cuts/
uv run image-reconcile export-avatar --project parquet-source-generator

# Score the master against the primary baseline
uv run image-reconcile compare --project parquet-source-generator --render
```

See [`docs/brand-assets/README.md`](../../docs/brand-assets/README.md) for the governing model.
