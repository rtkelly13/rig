# Brand Assets & the Image-Drift Harness

The authoritative description of how managed brand marks are stored, generated, scored, and
changed. Where another document states a threshold or a path that disagrees with this one,
this one is correct.

Decision record: [ADR 0018](../adr/0018-brand-asset-governance.md).

Deeper guides:

- [image-vectorization-harness.md](image-vectorization-harness.md): raster-to-vector approaches and the drift comparison harness.
- [svg-simplification-and-reverse-engineering.md](svg-simplification-and-reverse-engineering.md): the multi-pass simplifier, and rebuilding a mark as a Python builder.

---

## 1. The two tiers

A brand master is either **traced** or **computed**, and the two are governed differently.

| | Traced master | Parametric explorer |
|---|---|---|
| Example | `labs_builder` (rtkelly-labs), `diamond_parquet_builder` | `labs_parametric` |
| Source of truth | a committed SVG file | named parameters in Python |
| Can be recomputed? | **no** | yes |
| Registered in `cli.PROJECTS`? | yes | **no** |
| How it changes | `image-reconcile promote`, after winning the gate | freely — it proposes, it does not decide |

A traced master is a one-way door: it is a trace of an approved reference raster, and nothing
can reconstruct it if a worse drawing overwrites it. So the only path from a computed
candidate to a traced master runs through the promotion gate, and the explorer is deliberately
left out of the project registry so no routine command can route to it.

Explorers that nothing scores — no promote gate, pathway or test compares them against a
reference — do not ship in the package. The rhombille hexagon and interlocking-columns marks
live in [`experiments/logo-explorers/`](../../experiments/logo-explorers/README.md).

### Layout

```
tools/image-drift/src/image_drift/builder_assets/<project>/
    master.svg      AUTHORITATIVE — written only by `promote`
    favicon.svg     AUTHORITATIVE — hand-authored small-size cut
    manifest.json   per-variant sha256, `gated` flag, provenance

assets/<project>/
    <project>.svg           BUILD OUTPUT — committed; must equal master.svg
    reference/              ground truth, ordered; manifest.json names the primary
    production-cuts/        every exported cut + the governing CSV
```

The master lives with the code rather than under `assets/` because `build` *writes*
`assets/<project>/<project>.svg` — a builder that read its master from its own output would
launder corruption into the source of truth. Packaging it also keeps the builder working from
an installed wheel, where `assets/` does not exist.

---

## 2. Invariants

1. **Strict CSV derivation.** Production cuts derive strictly from
   `assets/<project>/production-cuts/production-cuts.csv`.
2. **Folder isolation.** Every exported cut and its CSV live in `production-cuts/`. Loose cuts
   must not pollute the project root.
3. **Rogue file exclusion.** A file in `production-cuts/` that the CSV does not declare is
   untracked noise: ignored by the indexer, excluded from distribution, failed by tests.
4. **Build output equals master.** `assets/<project>/<project>.svg` is byte-identical to the
   packaged `master.svg`, and the master's bytes match its manifest digest.
5. **Intermediates are ephemeral.** Simplifier output, candidate renders, and preview HTML go
   to the gitignored `temp/`. Nothing regenerable is committed.
6. **Dual co-refinement.** Simplification is guided by decomposed diagnostics
   (`image-reconcile diagnose`), attributing drift orthogonally between luminance
   (geometry/edges) and chrominance (palette/tint).

All six are enforced by `tests/test_brand_asset_governance.py` and
`tools/image-drift/tests/test_image_drift_labs.py`.

### `production-cuts.csv` schema

`filename,cut_type,width,height,format,purpose,description`

| `cut_type` | Meaning |
|---|---|
| `master_svg` | Canonical vector cut (a copy of the master) |
| `master_png` | 1000px master raster |
| `cut_2000` | 2000px ultra high-resolution raster |
| `avatar` | Profile avatar raster (500 or 180) |
| `apple_touch` | iOS Safari home-screen icon (180) |
| `favicon_svg` | High-contrast small-size vector cut |
| `favicon` | Favicon raster or `.ico` bundle |

`image-reconcile export-avatar -p <project>` writes one file per row and nothing else
(`tools/image-drift/src/image_drift/production_cuts.py`). `svg` rows are copies, `png` rows are Chromium
renders at the row's size, and `ico` rows bundle 48/32/16px up to the row's width. `favicon*`
cut types come from the builder's `favicon` variant; every other cut comes from the canonical
master. `tests/test_production_cuts.py` checks the export against each CSV.

---

## 3. Thresholds

The single authoritative set. Figures below are for `rtkelly-labs` against
`reference/02-smooth-crossover-baseline.jpg`, measured at 1000×1000 via Chromium.

| Gate | Threshold | Current | Enforced by |
|---|---|---|---|
| Master SSIM | ≥ 0.968 | **0.9699** | `test_drift_metrics_against_reference` |
| Master PSNR | ≥ 28.0 dB | **28.69 dB** | same |
| Master RMSE | ≤ 4.0% | **3.68%** | same |
| Simplifier SSIM | ≥ 0.995 | varies | `simplify` drift gate |
| Simplifier RMSE | ≤ 1.0% | varies | same |

> Earlier documents quoted **99.31% SSIM** for this mark. That figure is not reproducible
> against any committed reference and has been removed; the master scores **96.99%**. The PNG
> and JPEG references score identically, so the choice between them does not explain the gap.

**Scores are only comparable within one scoring pass.** `score_candidate` normalises onto a
fixed canvas, so a figure from one run cannot be compared to a figure from another. This is why
`promote` re-runs the gate live instead of trusting a stored report.

---

## 3a. Edge regularity — a renderer-free target metric

Drift answers "does this look like the reference?". It cannot answer "are the
edges geometrically correct?", because the reference raster's own edges are not
exact. Those are separate axes of quality, and optimising the first can preserve
defects in the second.

An isometric mark is built on a small set of canonical axes — 30°/90°/150° for
the rtkelly-labs loops — so every facet edge should lie on one of them. A raster
trace does not know that: it follows anti-aliased pixel boundaries and emits
edges a degree or two off-axis, plus short jogs where it stepped around a pixel
corner. Measured on the committed master:

| Measure | Value |
|---|---|
| Axis conformance | **36.4%** of edge length exactly on-axis (±0.5°) |
| Within 3° of an axis | 81.2% of edge length |
| Micro segments (<5px) | 160 |
| Off-axis edges (≤6°) | 149 |
| Staircase runs | 35 |

The 81.2% figure independently corroborates the **81.6%** edge correlation that
`diagnose` reports against the reference — two different methods measuring the
same defect.

```bash
uv run --project tools/image-drift image-reconcile edges -p rtkelly-labs                    # report
uv run --project tools/image-drift image-reconcile edges -p rtkelly-labs --min-conformance 36   # CI ratchet
uv run --project tools/image-drift image-reconcile edges -p rtkelly-labs --fix out.svg      # repair + score
```

### Why it works as a gate

Unlike SSIM, this needs no renderer — it is arithmetic over parsed path data, so
it runs everywhere the test suite runs. (The drift test is
`skipif(not chromium_available())` and therefore *silently skips* wherever
Playwright is absent; the conformance test never does.) It is pinned by
`tests/test_edge_regularity.py` to be:

- **deterministic** — bit-identical across runs and processes;
- **invariant** to path order, whitespace, and translation. Float summation is
  order-dependent, so the published score is rounded at the boundary
  (`EdgeReport.CONFORMANCE_PRECISION`) to make that invariance exact rather than
  approximate;
- **sensitive** — regularization moves it from 36.4% to ~68%.

**Conformance alone is not sufficient.** A staircase built from exactly-on-axis
segments scores 100% while still reading as jagged, which is why the report
carries micro-segment and staircase counts alongside the percentage and the
ratchet gates on both.

### The trade it exposes

`--fix` snaps near-axis edges onto their axes and rebuilds corners as the
intersections of consecutive snapped lines, so corners stay sharp rather than
being rounded off the way a distance-threshold simplifier leaves them. On the
current master that roughly doubles conformance and removes the visible
stair-stepping — **and costs about 0.004 SSIM against the reference**, because
the reference's own edges are not exactly on-axis.

That is a design decision, not a regression, so `--fix` writes and scores but
never promotes. The promotion gate is deliberately not wired to conformance:
it exists to stop accidental drift regressions, not to arbitrate a deliberate
geometric correction.

## 3b. Multiple pathways to a master

There is no single right way to arrive at a vector mark, because the two things
you want pull against each other. Tracing maximises fidelity to the approved
raster and inherits its jitter. Constructing from named parameters maximises
geometric correctness and is unconstrained in whether it resembles anything.

So several pathways are kept side by side, each deterministic, each measured on
both axes. Measured on `rtkelly-labs`:

| Pathway | Conformance | Edges | Defects | SSIM | Composite |
|---|---|---|---|---|---|
| **Traced** — the governed trace, unchanged | 50.1% | 558 | 361 | 0.9699 | 4.974% |
| **Autotraced** — a fresh VTracer trace of the reference | 36.2% | 24577 | 22339 | **0.9934** | **2.190%** |
| **Regularized** — trace with edges snapped to axes | 82.7% | 380 | 86 | 0.9660 | 5.634% |
| **Parametric** — constructed from named geometry | **100.0%** | **148** | **0** | 0.6338 | 27.778% |
| **Fitted** — constructed, dimensions fitted to the reference | **100.0%** | **148** | **0** | 0.6365 | 25.866% |

```bash
uv run --project tools/image-drift image-reconcile pathways -p rtkelly-labs --out-dir temp/pathways
```

### What the numbers say

**Construction gives exact coordinates by definition** — 100% conformance, zero
micro-segments, zero staircases, in a quarter of the geometry. A trace can never
reach that, because it is following a raster's anti-aliased pixel boundaries.
That answers the question directly: yes, regenerating from first principles
produces cleaner lines and coordinates.

**But geometry is not resemblance.** The parametric construction scores 0.634
SSIM because its facet layout draws a different arrangement than the mark.

**And fitting dimensions cannot fix a wrong arrangement.** The `fitted` pathway
runs deterministic coordinate descent over six parameters against the reference
and moves the composite only from 27.8% to 25.9%. That 1.9pp is the useful
finding: the remaining gap is *structural*, not dimensional, so the way to make
the constructive pathway competitive is to correct its facet topology, not to
tune numbers. The fitter earns its place as a diagnostic that proves this.

**Raw fidelity is cheap; clean geometry is not.** `autotraced` runs VTracer over the
reference with no tuning and scores closest to it, in 44× the edges of the governed trace
and with nine in ten of them defective. It is the baseline every other pathway has to
justify itself against, scored by the same `_score_svg` pass, not a candidate to promote.

**The practical win today is the hybrid.** `regularized` keeps the trace's
shapes and snaps only its edges: conformance 50.1% → 82.7%, defects 361 → 86,
for 0.004 SSIM. It is visibly the same mark with crisper edges.

### Determinism

Every pathway is deterministic — same inputs, byte-identical output — and the
fitter especially so: fixed field order, fixed step schedule, no randomness, and
ties broken toward the incumbent so the walk cannot oscillate between
equal-scoring neighbours. `tests/test_pathways.py` pins this.

None of them promotes anything. Pathways propose; `promote` disposes.

## 4. The promotion gate

A candidate replaces the incumbent master only if **all three** hold:

1. **It beats, not ties.** Composite must improve by more than `TIE_THRESHOLD_PCT` (0.5pp) —
   the project's existing definition of "too close to rank".
2. **No structural regression.** SSIM must not decrease.
3. **No single region regresses** by more than 0.25pp. Without this, `FRAME_WEIGHT = 0.5` lets
   a candidate win by rendering the flat background well while damaging the crossover core —
   the region that carries the mark's identity.

PSNR is reported but not gated: it tracks SSIM closely and gating it only over-constrains.

```bash
# Propose. Read-only w.r.t. assets/ and builder_assets/; snapshots to temp/variations/.
uv run --project tools/image-drift image-reconcile explore -p rtkelly-labs --sweep r_outer=150,158,166
#   exit 0 = a candidate won   exit 2 = none did   exit 1 = error

# Inspect, then promote. Without --confirm this is a dry run.
uv run --project tools/image-drift image-reconcile promote -p rtkelly-labs --run-id <id> --candidate <N> --confirm
```

`promote` backs the incumbent up beside the run before writing, updates the manifest digest and
provenance, and regenerates the committed asset so the byte-equality test stays green.

---

## 5. Variants

Every builder declares `VARIANTS` and `DEFAULT_VARIANT`. **An undeclared variant raises
`UnknownVariantError`; nothing falls back silently.**

This rule exists because the opposite shipped: `labs_builder.generate_svg` accepted `variant`
and discarded it, so `image-reconcile favicon` emitted the full master and every favicon raster
was the whole logo scaled down — while the command's own docstring said that must not happen.
A silent fallback is a wrong answer wearing the shape of a right one.

A favicon is a **separate drawing**, hand-authored, not a simplifier product. The simplifier is
a *fidelity-preserving* optimiser whose success criterion is "renders identically"; a favicon
must render *differently*. At any tolerance where it actually drops detail, its own gate
rejects.

---

## 6. Commands

| Command | Purpose |
|---|---|
| `build` | Deterministically write the SVG from its builder. `--list-variants` to enumerate. |
| `compare` | Drift, SSIM, PSNR, 5×5 worst-cell grid, per-feature table. `--decompose` for channel attribution. |
| `diagnose` | Decomposed drift: luminance vs chrominance, edge correlation, hotspots, recommendations. |
| `explore` | Propose parametric candidates and score them against the incumbent. |
| `promote` | Replace the master with a candidate that wins the gate. |
| `export-avatar` | Write every cut declared in `production-cuts.csv`, and nothing else. |
| `favicon` | Write the small-size cut plus its raster set. |
| `simplify` | Multi-pass simplification behind a drift gate. Exits non-zero if the budget is missed. |
| `vectorize` | Raster → SVG via VTracer. |
| `pathways` | Build the mark by every pathway (including a fresh VTracer trace) and score each against the reference. |
| `extract-feature` | Crop one named feature region for isolated analysis. |
| `contact-sheet` | Reference-vs-candidates comparison strip. |
| `build-preview` | Render every variant, score, emit a self-contained gallery. |
| `snapshot` | Capture a pipeline step into the variations tree. |
| `serve` | Launch the Asset Studio workbench (default <http://127.0.0.1:8092>). |

---

## 7. Known anti-patterns

Learned the hard way while establishing the rtkelly-labs mark; both are still live risks.

1. **Naive raster auto-tracing.** Running VTracer or Potrace over the smooth gradients quantizes
   them into 20–90 flat bands whose borders form jagged micro-staircase contours — severe
   aliasing, muddy shadows, a stepped elevation-map look. Use hierarchical *stacked*
   vectorization (`mode=polygon`, `color_precision=5`, `layer_difference=20`) with dark seam
   strokes to stop hairline light leaks. The `autotraced` pathway (§3b) keeps this measurable:
   it puts a raw VTracer trace in the same table as the governed master.
2. **Flat code-drafted CAD polygons.** Emitting unshaded polygons with basic two-stop gradients
   strips out the 3D lighting, ambient occlusion and edge bevels, leaving the mark flat and
   lifeless. `labs_parametric.FaceLighting` is the countermeasure: every facet gets its own
   `userSpaceOnUse` gradient whose vector is projected from that facet's own extent along the
   light direction, with the ramp chosen by facet orientation — derived rather than hand-placed.

A third, non-obvious one: **Obsidian treats a leading `/` as vault-relative**, not OS-root
relative, so absolute image paths in vault notes fail to resolve. Embed base64 data URIs or
place the asset in the vault and use `![[filename.png]]`.
