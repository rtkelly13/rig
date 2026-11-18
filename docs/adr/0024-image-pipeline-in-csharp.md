# ADR 0024: The Image Pipeline in C# Has Two Renderers That Cross-Check Each Other

- **Status:** Accepted
- **Applies to:** `image_drift` (the brand-asset pipeline), rig issues #85 and #317
- **Supersedes:** item 7 of [ADR 0022](0022-csharp-is-rigs-primary-language.md) (`image_drift` stays Python)

## Context

[ADR 0022](0022-csharp-is-rigs-primary-language.md) item 7 kept `image_drift` in Python because "no .NET library
reproduces those bytes". Issue #317 asks whether that is still true by measuring three C# stacks against the
Python/Playwright pipeline. The harness is [`experiments/image-stack-eval/`](../../experiments/image-stack-eval/README.md);
every number below is in its generated [`results/results.md`](../../experiments/image-stack-eval/results/results.md).
It is not in `rig.slnx` or CI.

Two axes were separated: the renderer (Chromium or Svg.Skia) and the raster library that decodes, resizes and
encodes (ImageSharp or SkiaSharp). Stacks 2 and 3 share Svg.Skia pixels and differ only in the raster library.
Control rows run the C# metrics over the Python-rendered PNGs, which isolates implementation error from renderer
error.

Already decided and not re-evaluated: vectorising runs a pinned vtracer CLI as a process, and ImageSharp's Six
Labors Split License is acceptable (moot after the Decision's amended item 2, which drops ImageSharp).

## Results

Measured on macOS arm64, .NET 10, the same Chromium (headless shell rev 1248) for Python and .NET, two projects
(rtkelly-labs, parquet-source-generator), 23 renders.

| Criterion | 1. ImageSharp + Playwright | 2. SkiaSharp + Svg.Skia | 3. ImageSharp + Svg.Skia |
|---|---|---|---|
| Render max abs diff vs Python (0..255) | 0 | 1 to 76 | 1 to 76 |
| Render mean abs diff | 0 | 0.02 to 0.60 | 0.02 to 0.60 |
| Differing pixels | 0% on all 23 | 2.6% to 14.2% on 21 of 23 (0% on the two autotraced SVGs) | same as 2 |
| Committed PNG cuts reproduced exactly | 11 of 18 (identical to Python's own result) | 0 of 18 | 0 of 18 |
| Worst metric error: RMSE % / SSIM / PSNR dB | 0.014 / 0.00018 / 0.022 | 0.307 / 0.0058 / 0.474 (cubic); 0.042 / 0.00041 / 0.076 (mipmap) | 0.021 / 0.00032 / 0.033 |
| Worst luminance / chrominance RMSE % | 0.0015 / 0.0090 | 0.397 / 0.0047 (cubic) | 0.030 / 0.0083 |
| Worst edge correlation / composite % | 0.017 / 0.022 | 0.879 / 0.462 (cubic) | 0.182 / 0.042 |
| Promotion verdicts flipped (of 5 per stack) | 0 | 0 | 0 |
| Narrowest promotion margin shift | under 0.001pp | up to 0.18pp (composite), 0.05pp (feature) | up to 0.04pp |
| Native deps | Chromium (already required by Python) plus the Playwright driver | SkiaSharp natives per OS | SkiaSharp natives per OS (Svg.Skia needs them) |
| Package size (nupkgs) | Playwright 211 MB, ImageSharp 1.4 MB, plus Chromium (203 MB headless shell) | SkiaSharp 9 MB plus natives (7 MB macOS, 79 MB win32 nupkg), Svg.Skia 0.8 MB plus Svg.* 3 MB | as 2 plus ImageSharp 1.4 MB |
| Cold start, whole process (3 runs, Debug build via `dotnet <dll>`) | 0.7 to 2.9 s (first run 2.9 s) | 0.4 to 0.7 s | 0.34 s |
| Warm 500 px render | 0.085 s (Python: 0.20 s) | 0.014 s | 0.014 s |
| Licence | Playwright MIT, ImageSharp Six Labors Split | SkiaSharp, Svg.Skia, Svg.Model, Svg.Scenegraph MIT; **Svg.Custom MS-PL** (in the Svg.Skia dependency tree) | as 2, plus ImageSharp Six Labors Split |
| ICO writer | ImageSharp has an `IcoEncoder` (single-frame probe works); Pillow builds the 32 and 16 entries with a Lanczos thumbnail and ImageSharp Lanczos3 matches the rtkelly-labs entries to within 2 levels (not exactly; 19% of pixels differ by 1 to 2); a hand-written PNG-in-ICO container also works | none in SkiaSharp (Encode returns null); hand-written container; resize differs by up to 100 levels | as 1 |

The SkiaSharp row also has a mipmap-resize variant (2b). Metric deltas in the table are the cubic variant unless
stated. Every row's metrics are compared with Python's own float values: the control rows (C# metrics over Python
PNGs) bound the error that is not the renderer: at most 0.014 RMSE percentage points and 0.00018 SSIM. That residual
is the sum of the metric port, the resize and the JPEG decode of the second reference file; the harness does not
separate them.

### vtracer

Pinned release: **vtracer 0.6.4** CLI (visioncortex/vtracer GitHub release).

| Artifact | SHA-256 |
|---|---|
| `vtracer-aarch64-apple-darwin.tar.gz` | `4a597fd2df8b961d60620df40a7436109427d86e5c028758e6e8796b02d3d996` |
| `vtracer-x86_64-pc-windows-msvc.zip` | `6b5bc17a6b017129ee40461df254f65d16f3b494c001a8541d41861066b716bf` |
| extracted macOS `vtracer` | `77e495bbe212448240387fba3b6d8bc62ba20ecfb6f3c22967e51600f1cc6e66` |
| extracted Windows `vtracer.exe` | `4ad8d35e566cd15caf582063b8349bd082b8fa2bd461e99d116fc63ad8fdeca0` |

Run on macOS arm64 with the flag mapping in `Program.cs`, 0.8 to 0.9 s per image, the output is identical to the
Python binding's apart from the one-line `Generator:` comment, for both projects. 1.0.0-alpha.4 has a different
flag set and a different output, so it is not a drop-in. Windows x64 execution was not measured (hash only).

## Decision

The owner decided to support **both** renderers and use each to cross-check the other.

1. **A renderer port with two implementations.** `ISvgRenderer` (the name the harness uses) renders an SVG to
   straight-alpha RGBA at a given size. One implementation drives Chromium through Microsoft.Playwright (stack 1's
   renderer); the other draws with Svg.Skia (stacks 2 and 3's renderer). Renderers are interchangeable behind the
   port.
2. **SkiaSharp does all raster work on both** (amended; this item first chose ImageSharp): decode, PNG encode and
   ICO through SkiaSharp, and the `load_normalised` resize through `PillowResize`, a port of Pillow's separable
   resampling filters over SkiaSharp pixel buffers. Stack 2 lost to stack 3 only because SkiaSharp's own cubic and
   mipmap filters are not Pillow's (composite error 0.46 against 0.042); a Pillow-exact resize removes that gap, so
   SkiaSharp, already required by Svg.Skia, replaces ImageSharp and the pipeline carries one raster library with an
   MIT licence instead of two.
3. **Chromium is authoritative** for promotion and for production cuts, because it is the only renderer that keeps
   byte-identity with today's output (0 differing pixels on all 23 renders). It needs a pinned Chromium revision
   in CI.
4. **Svg.Skia is the fast, browser-free renderer** for previews, the gallery and local runs without Chromium. It is
   about 6 times faster warm and about 5 times faster cold, and needs no browser download. Its output never decides
   a promotion or writes a production cut.
5. **Cross-check.** A command (and a CI job) renders every master and every production cut with both renderers and
   reports per file: max abs, mean abs, differing-pixel %, the metric deltas, and whether any promotion verdict
   would differ between the renderers. A divergence above the thresholds below means an SVG feature one renderer
   mishandles (filters, gradients and opacity are where Svg.Skia diverges); it is reported by file, never silently
   averaged or tolerated. The report is a typed, per-file document of values and thresholds, shaped so that it can
   later be emitted as Diffra evidence (rtkelly-labs/Diffra). Nothing depends on Diffra now.
6. **The experiment folder is deleted by the port's final slice.** `experiments/image-stack-eval/` is the
   measurement record and the seed code (`Metrics.cs`, `Stacks.cs`, `IcoFile.cs`); once the port has absorbed it,
   git is the record and the folder goes (see [ADR 0022](0022-csharp-is-rigs-primary-language.md) policy of deleting
   replaced code).
7. **vtracer stays a pinned process**, release 0.6.4 (checksums above).

This supersedes item 7 of [ADR 0022](0022-csharp-is-rigs-primary-language.md): `image_drift` moves to C#.

### Divergence thresholds (Svg.Skia against Chromium, both through the same raster code)

Set from the measured worst cases (stack 3 rows, 23 renders, 2 projects) with headroom. Exceeding any one is a
reported divergence; the verdict-flip row is a hard failure.

**Split by size (amended).** The pixel thresholds apply to cuts of 180 px and up. At 16 and 32 px (the favicon
entries) one anti-aliased edge is a large share of the image, so the two renderers routinely differ on more than
20% of pixels and by more than 1.0 mean abs (parquet favicon.16 measured 1.43) without any visible fault. Those
sizes are reported with every value but fail only on a changed promote/reject decision. Cuts between 32 and 180 px
are gated as large cuts.

| Quantity | Measured worst | Threshold |
|---|---:|---:|
| Mean abs difference (0..255) | 0.60 | 1.0 |
| Differing pixels | 14.2% | 20% |
| Max abs difference | 76 (parquet at 180 px) | reported, not gated: single anti-aliased edge pixels reach it |
| RMSE | 0.021 pp | 0.1 pp |
| SSIM | 0.00032 | 0.001 |
| Composite | 0.042 pp | 0.1 pp |
| Promotion verdict | 0 of 5 flipped | any flip fails |

These are proposals from a two-project sample on macOS arm64. They are recorded here, and revised by amending this
ADR, when Windows and Linux Svg.Skia runs exist. The mipmap and cubic SkiaSharp resize variants are not used and
their larger errors do not apply.

## Recommendation (superseded by the Decision above)

The evaluation's own ranking, kept for the record: stack 1, ImageSharp + Microsoft.Playwright, is the only
pixel-identical stack. The reasons stand and are why Chromium is authoritative.

**Stack 1: ImageSharp + Microsoft.Playwright.**

- It is the only stack that is pixel-identical to Python: 0 differing pixels on all 23 renders, because the
  renderer is the same Chromium. That covers the render path. The ICO path is not yet bit-exact: ImageSharp's
  Lanczos3 resize is within 2 levels of Pillow's, so `favicon.ico` entries below 48 px would change by a
  rounding step unless the resize is matched exactly. [ADR 0009](0009-deterministic-svg-render-pipeline.md)'s
  byte-identical guarantee is therefore met for renders and not yet for the 32 and 16 px icon entries.
- Svg.Skia is a different renderer. It differs on 3 to 14% of pixels (up to 76 levels at 180 px) and reproduces
  none of the committed cuts. No promotion verdict flips, so it would pass the current gate, but every production
  cut would change, which is the cost ADR 0022 named.
- Between the Svg.Skia stacks, ImageSharp (3) tracks Pillow's resize more closely than SkiaSharp (2) does
  (composite error 0.042 versus 0.46), so if a browser-free stack is ever wanted, choose 3 over 2.
- Cost of stack 1: a 211 MB Playwright package and a Chromium download. Python already carries both, so this is
  no regression. Cold start is slower but the pipeline is an offline tool.

If the owner accepts this, ADR 0022 item 7 is superseded for `image_drift`. vtracer stays a pinned process.

## Not measured, and why

- **Windows and Linux rendering.** Only macOS arm64 was available. Svg.Skia and Skia output may differ per OS;
  Chromium certainly does (see below). SkiaSharp needs `SkiaSharp.NativeAssets.Linux` on Linux, which was not restored here.
- **Windows x64 vtracer execution.** Hash recorded, binary not run.
- **The `fitted` pathway.** It needs a scorer and is skipped by the Python reference too.
- **`scour` and `shapely`.** `image_drift` also uses them (SVG optimisation, regularising, parametric geometry).
  This evaluation covers render, raster and metrics, not those two; a port needs its own answer for each.
- **Inline test-fixture SVGs.** Only the committed brand SVGs and pathway outputs were rendered.
- **A baseline caveat.** Python/Chromium reproduces only 11 of 18 committed PNG cuts, and the same 11 under
  stack 1. The seven it misses (rtkelly-labs favicon-512, parquet avatar 500 and 180, apple-touch-icon, favicon
  48/32/16) are not renderer noise: the builder SVGs are byte-identical to the committed ones, and the committed
  rtkelly-labs favicon-512 shows different artwork (different gradients on the loops), so those files are stale
  against the current builders. The committed parquet `favicon.ico` is stale the same way, which makes its
  entries unusable as an ICO oracle; the rtkelly-labs ICO is the usable one. Stack 1 equals Python, not those bytes.
- **pathways.py thresholds.** It has no score threshold to flip. The only comparison on a score is the strict
  `loss < best_loss` tie-break in the `fitted` pathway's parameter walk, which is the pathway that was not run.
  Its sensitivity to metric noise is unmeasured.

## Consequences

- Port `image_drift` in stages, metrics first (the C# metrics in the harness already match Python to the figures
  above), then the renderer port and both implementations, then the cross-check command and its CI job, with
  `Metrics.cs`, `Stacks.cs` and `IcoFile.cs` as the starting point. The final slice deletes
  `experiments/image-stack-eval/`.
- Needs a pinned Chromium revision in CI so that cuts are reproducible across machines.
- The cross-check job is the standing detector for SVG features Svg.Skia mishandles; the stale committed cuts and
  the ICO resize rounding (findings above) are fixed as part of the port, not hidden by the thresholds.
- Windows and Linux Svg.Skia runs are still owed (see above); thresholds are amended once they exist.
