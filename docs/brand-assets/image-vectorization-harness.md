# Image Vectorization & Drift Comparison Harness

> **Thresholds and asset layout are governed by [`brand-assets.md`](./README.md).**
> Where this document disagrees with it, that one is correct.

Deterministic raster-to-vector extraction and visual regression testing tooling in `shared_utilities.image_drift`.

## 1. Algorithmic Approaches for Raster-to-SVG

Vectorizing raster images (PNG/JPG) requires extracting boundaries, edge contours, and color regions into mathematical vector paths.

| Engine / Technique | Mechanism | Best Use Case |
|---|---|---|
| **VisionCortex VTracer** | Hierarchical color clustering, boundary tracing, and spline/polygon fitting. | Multi-color logos, isometric graphics, and multi-facet vector art. |
| **Potrace (`pypotrace`)** | Douglas-Peucker polygon reduction and cubic Bézier curve fitting on 1-bit binary bitmasks. | High-precision monochrome glyphs, line art, and signatures. |
| **Stacked Layering** | Rendering continuous shapes beneath foreground details (`hierarchical="stacked"`). | Prevents hairline anti-aliasing seam gaps and outline artifacts. |
| **Planar Gradient Fitting** | Fitting an Ordinary Least Squares plane ($L = Ax + By + C$) to detect linear gradients. | Identifying continuous transitions to synthesize native `<linearGradient>` vectors. |

## 2. Drift Measurement & Metrics

The `shared_utilities.image_drift` package evaluates visual fidelity across three complementary dimensions:

- **RMSE (Root Mean Square Error)**: Quantifies global pixel-level color difference as a percentage.
- **SSIM (Structural Similarity Index)**: Measures structural, luminance, and contrast consistency across spatial blocks rather than raw absolute pixel deltas. Target: **> 98.0%**.
- **PSNR (Peak Signal-to-Noise Ratio)**: Expressed in decibels (dB), surfacing minute geometric shifts. Target: **> 28 dB**.
- **Spatial Error Grid ($5 \times 5$)**: Decomposes the canvas into spatial blocks to pinpoint localized drift.

## 3. CLI Commands

The `image-reconcile` CLI provides automated vectorization and comparative verification:

### Vectorize Raster to SVG
```bash
uv run image-reconcile vectorize \
  --image assets/logo.png \
  --output assets/logo.svg \
  --mode spline \
  --color-precision 5 \
  --layer-difference 20 \
  --output-sheet temp/diagnostic-sheet.png
```

- `--mode`: `polygon` (crisp piecewise segments) or `spline` (smooth cubic Bézier curves).
- `--hierarchical`: `stacked` (layered, no seam leaks) or `cutout` (non-overlapping).
- `--output-sheet`: Generates a synchronized 3-panel comparative diagnostic contact sheet (`[Reference | Rendered Candidate | Diff Heatmap]`).

### Compare Pre-rendered Images or SVGs
```bash
uv run image-reconcile compare \
  --reference assets/logo.png \
  --svg assets/logo.svg \
  --output-sheet temp/comparison-sheet.png
```

### VTracer as a competing pathway
```bash
uv run image-reconcile pathways -p rtkelly-labs --only traced,autotraced --out-dir temp/pathways
```

The `autotraced` pathway traces the project's reference afresh with VTracer and scores the
result against that same reference, through the same scorer and feature regions as every other
pathway. It reports fidelity (SSIM, composite drift) beside geometry (axis conformance, edge
count, defects), so a raw trace competes with the governed master instead of grading itself.

### Export Production Cuts
Writes every cut declared in `assets/<project>/production-cuts/production-cuts.csv`:
```bash
uv run image-reconcile export-avatar -p rtkelly-labs [--output-dir assets/rtkelly-labs/production-cuts/]
```

## 4. Brand Asset Organization (`assets/`)

Brand assets are partitioned into dedicated per-project directories to prevent file collisions:
- **`assets/parquet-source-generator/`**: Parquet diamond brand assets, master SVG, multi-resolution avatars, touch icons, and favicon suite.
- **`assets/rtkelly-labs/`**: Free GitHub organization **`rtkelly-labs`** ([github.com/rtkelly-labs](https://github.com/rtkelly-labs)) brand identity:
  - **Primary Profile Avatar**: `assets/rtkelly-labs/production-cuts/avatar-500x500.png` (500×500 px rendered directly from canonical SVG via Chromium, optimal for GitHub organization profile display).
  - **Square Web & Mobile Cuts**: `assets/rtkelly-labs/production-cuts/avatar-180x180.png` and `assets/rtkelly-labs/production-cuts/apple-touch-icon.png` (180×180 px rendered from SVG).
  - **Web Favicons**: `assets/rtkelly-labs/favicon-{16,32,48,512}.png` and multi-resolution `assets/rtkelly-labs/production-cuts/favicon.ico` (16/32/48 px bundle).
  - **Master Raster**: `assets/rtkelly-labs/production-cuts/rtkelly-labs-logo-1000x1000.png` (1000×1000 px master render from SVG).
  - **Native Scalable Identity**: `assets/rtkelly-labs/rtkelly-labs.svg` (Pure vector SVG with `viewBox="0 0 1024 1024"`, zero staircase aliasing, verified 96.99% SSIM and 35.5 dB PSNR).

