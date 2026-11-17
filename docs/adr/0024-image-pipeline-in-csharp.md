# ADR 0024: The Image Pipeline in C# Uses ImageSharp and Playwright

- **Status:** Proposed
- **Applies to:** `image_drift` (the brand-asset pipeline), rig issues #85 and #317
- **Proposes superseding:** item 7 of [ADR 0022](0022-csharp-is-rigs-primary-language.md) (`image_drift` stays Python)

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
Labors Split License is acceptable.

## Results

Measured on macOS arm64, .NET 10, the same Chromium (headless shell rev 1248) for Python and .NET, two projects
(rtkelly-labs, parquet-source-generator), 23 renders.

| Criterion | 1. ImageSharp + Playwright | 2. SkiaSharp + Svg.Skia | 3. ImageSharp + Svg.Skia |
|---|---|---|---|
| Render max abs diff vs Python (0..255) | 0 | 1 to 76 | 1 to 76 |
| Render mean abs diff | 0 | 0.02 to 0.60 | 0.02 to 0.60 |
| Differing pixels | 0% on all 23 | 2.6% to 14.2% on 21 of 23 (0% on the two autotraced SVGs) | same as 2 |
| Committed production cuts reproduced exactly | 11 of 18 (same as Python on this machine) | 0 of 18 | 0 of 18 |
| Worst metric error: RMSE % / SSIM / PSNR dB | 0.014 / 0.00018 / 0.022 | 0.307 / 0.0058 / 0.474 (cubic); 0.042 / 0.00041 / 0.076 (mipmap) | 0.021 / 0.00032 / 0.033 |
| Worst luminance / chrominance RMSE % | 0.0015 / 0.0090 | 0.397 / 0.0047 (cubic) | 0.030 / 0.0083 |
| Worst edge correlation / composite % | 0.017 / 0.022 | 0.879 / 0.462 (cubic) | 0.182 / 0.042 |
| Promotion verdicts flipped (of 5 per stack) | 0 | 0 | 0 |
| Narrowest promotion margin shift | under 0.001pp | up to 0.18pp (composite), 0.05pp (feature) | up to 0.04pp |
| Native deps | Chromium (already required by Python) plus the Playwright driver | SkiaSharp natives per OS | SkiaSharp natives per OS (Svg.Skia needs them) |
| Package size (nupkgs) | Playwright 211 MB, ImageSharp 1.4 MB, plus Chromium (203 MB headless shell) | SkiaSharp 9 MB plus natives (7 MB macOS, 79 MB win32 nupkg), Svg.Skia 0.8 MB plus Svg.* 3 MB | as 2 plus ImageSharp 1.4 MB |
| Cold start, whole process (3 runs) | 0.7 to 2.9 s (first run 2.9 s) | 0.4 to 0.7 s | 0.34 s |
| Warm 500 px render | 0.085 s (Python: 0.20 s) | 0.014 s | 0.014 s |
| Licence | Apache-2.0 + Six Labors Split | MIT, BSD-3 (Skia) | MIT + Six Labors Split |
| ICO writer | ImageSharp has an `IcoEncoder`; hand-written container also works | none in SkiaSharp (Encode returns null); hand-written container | as 1 |

The SkiaSharp row also has a mipmap-resize variant (2b). Metric deltas in the table are the cubic variant unless
stated. Every row's metrics are compared with Python's own float values: the control rows (C# metrics over Python
PNGs) show the port itself is faithful, with the residual 0.014 RMSE-percentage-point error coming from the ImageSharp
bicubic resize versus Pillow's bicubic, not the metric code.

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

## Recommendation

**Stack 1: ImageSharp + Microsoft.Playwright.**

- It is the only stack that is pixel-identical to Python: 0 differing pixels on all 23 renders, because the
  renderer is the same Chromium. Nothing about [ADR 0009](0009-deterministic-svg-render-pipeline.md)'s
  determinism changes.
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
- **A baseline caveat.** On this machine even Python/Chromium reproduces only 11 of 18 committed cuts exactly
  (parquet avatar, apple-touch-icon and the 16/32/48 favicons differ), so committed cuts were produced with a
  different Chromium, platform or path. Stack 1 equals Python, not the committed bytes.

## Consequences

- If accepted: port `image_drift` in stages, metrics first (the C# metrics in the harness already match Python to
  the figures above), then the renderer, with `Metrics.cs`, `Stacks.cs` and `IcoFile.cs` as the starting point.
- Needs a pinned Chromium revision in CI so that cuts are reproducible across machines.
- If rejected, ADR 0022 item 7 stands and the experiment stays as the record.
