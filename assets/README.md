# Brand Assets Estate Repository (`assets/`)

This directory is the centralized, segregated brand identity repository for the personal estate repositories managed by `shared-utilities`.

## 1. Segregation Policy

To prevent asset clashing and accidental overwrites between projects:
- **Zero Loose Root Files**: No raster cuts, SVGs, or favicon files exist directly in the `assets/` root directory.
- **Dedicated Project Namespaces**: Each project or organization brand mark resides in its own dedicated subdirectory under `assets/`.
- **Symmetric Conventions**: Each project namespace provides a complete, standardized set of production cuts (master SVG, master 1000px raster, avatars, apple touch icons, and multi-resolution favicons).

## 2. Managed Brand Directories

| Directory | Project / Brand | Identity Description | Generator Engine |
|---|---|---|---|
| [`parquet-source-generator/`](./parquet-source-generator/) | [Parquet.SourceGenerator](file:///Users/ryankelly/code/personal/Parquet.SourceGenerator) | Isometric oak wood parquet diamond motif with a 3-branch Git fork triad (Electric Cyan, Neon Violet, Solar Amber). | `diamond_parquet_builder.py` |
| [`rtkelly-labs/`](./rtkelly-labs/) | [rtkelly-labs GitHub Org](https://github.com/rtkelly-labs) | Interlocking isometric hexagonal prisms with continuous planar gradient transitions (Electric Cyan to Azure/Cobalt) and ambient occlusion shadows. | `labs_builder.py` |

## 3. Project Layout

Each brand namespace has the same three-part shape:

```
assets/<project>/
    <project>.svg       build output — written by `image-reconcile build`, committed
    README.md
    reference/          ground truth, ordered; manifest.json names the primary
    production-cuts/    every exported cut + production-cuts.csv
```

The **authoritative** master is not here — it lives with the code, at
`src/shared_utilities/image_drift/builder_assets/<project>/master.svg`, because `build` writes
the file above and a builder cannot read its master from its own output. A test keeps the two
byte-identical. See [`docs/brand-assets/README.md`](../docs/brand-assets/README.md).

### `production-cuts/`

Every exported cut lives here, never in the project root, and every one is declared in
`production-cuts.csv`. **A file present in this folder that the CSV does not declare is rogue**:
ignored by the catalog indexer, excluded from distribution, and failed by
`tests/test_brand_asset_governance.py`.

CSV schema: `filename,cut_type,width,height,format,purpose,description`

| `cut_type` | File | Usage |
|---|---|---|
| `master_svg` | `<brand>.svg` | Canonical vector cut (copy of the master) |
| `master_png` | `<brand>-1000x1000.png` | Master high-resolution raster |
| `cut_2000` | `<brand>-2000x2000.png` | Ultra high-resolution raster |
| `avatar` | `avatar-500x500.png`, `avatar-180x180.png` | Profile avatars |
| `apple_touch` | `apple-touch-icon.png` | iOS Safari web clip icon |
| `favicon_svg` | `favicon.svg` | Simplified high-contrast small-size vector cut |
| `favicon` | `favicon-{512,48,32,16}.png`, `favicon.ico` | Browser favicons |

`favicon.svg` is a **separate drawing**, not the master scaled down: below roughly 48px the
master's facets collapse into mud. The 48/32/16 rasters and the `.ico` derive from it;
everything larger derives from the master.

## 4. CLI Automation & Path Routing

All generation, verification, and export operations are driven via the `image-reconcile` CLI with path routing support:

```bash
# Export production cuts (routes to assets/<project>/production-cuts/)
uv run image-reconcile export-avatar --project rtkelly-labs
uv run image-reconcile export-avatar --project parquet-source-generator

# Score every pathway, including a fresh VTracer trace, against the reference
uv run image-reconcile pathways --project rtkelly-labs

# Pixel-by-pixel drift comparison against the primary baseline
uv run image-reconcile compare --project rtkelly-labs --render
uv run image-reconcile compare --project parquet-source-generator --render

# Propose parametric alternatives; promotion is separate and gated
uv run image-reconcile explore --project rtkelly-labs --sweep r_outer=150,158,166
```

## 5. Reference Material Convention (`assets/<project>/reference/`)

Each brand directory contains a dedicated `reference/` subfolder alongside the final copies to preserve the historical sequence of concept images and baseline reference assets:
- `reference/01-*.jpg`: Initial concept generations and exploratory art.
- `reference/02-*.jpg`: Refined/approved baseline photos.
- `reference/<brand>.png`: Lossless ground-truth raster baseline for automated drift calibration.
- `reference/manifest.json`: Names the **primary** baseline that drift is scored against, plus the role and sequence of every other reference.
- `reference/README.md`: Contextual documentation detailing the provenance and decisions behind each reference file.

