# ADR 0018: Brand Asset Governance and the Traced-Master Promotion Gate

- **Status:** Accepted
- **Applies to:** `shared_utilities.image_drift` / managed brand assets under `assets/`
- **Supersedes:** the operational half of [ADR 0009](./0009-deterministic-svg-render-pipeline.md), which predates per-project namespacing and states no thresholds or storage convention.

## Context

ADR 0009 adopted a deterministic SVG pipeline but said nothing about where brand assets live,
which file is authoritative, or what must be true before one changes. In the absence of that,
the `rtkelly-labs` mark accumulated four problems:

- Its builder declared a `variant` API and a `favicon` variant, and implemented neither — the
  argument was accepted and discarded, so every favicon raster was the full logo scaled down.
- The master SVG existed as three uncoordinated copies with nothing keeping them in sync.
- Production cuts moved into `production-cuts/` and roughly twenty documentation links died,
  with four documents quoting four different fidelity thresholds and none authoritative.
- No test asserted that the committed asset still matched its builder, that a declared variant
  rendered anything distinct, or that the cuts still matched the master.

The underlying tension is that the master is a **trace** of an approved reference raster. It
scores well precisely because it was traced, and nothing can recompute it — but a traced blob
cannot be explored, swept, or reasoned about parametrically.

## Decision

**Separate the governed master from the parametric explorer, and make promotion the only
bridge between them.**

1. **The traced master is the source of truth**, stored as a committed SVG in
   `src/shared_utilities/image_drift/builder_assets/<project>/`, alongside a `manifest.json`
   recording each variant's sha256, gating, and provenance. `assets/<project>/<project>.svg` is
   build output, kept byte-identical by test.
2. **The parametric explorer (`labs_parametric`) is deliberately not registered** in
   `cli.PROJECTS`. No routine command can route to it.
3. **`image-reconcile promote` is the only path** from a candidate to the master. It re-runs
   the gate live — scores are only comparable within one scoring pass — backs up the incumbent,
   and requires `--confirm`.
4. **The gate requires all three of:** composite beating the incumbent by more than
   `TIE_THRESHOLD_PCT`; no SSIM regression; no single feature region regressing by more than
   0.25pp. The third exists because `FRAME_WEIGHT = 0.5` otherwise lets a candidate win on the
   flat background while damaging the crossover core.
5. **An undeclared variant raises.** No builder falls back silently to its default.
6. **Production cuts derive strictly from `production-cuts.csv`**; undeclared files in that
   folder are rogue; every invariant above is enforced by tests rather than by convention.

## Consequences

- Changing the master is deliberate, reviewable, and reversible: a backup is written before any
  promotion, and an unprovenanced hand-edit fails the manifest digest test.
- Parametric exploration is cheap and safe, because a candidate that loses simply cannot land.
  The current explorer scores far worse than the trace and is correctly refused — that is the
  gate working, not a defect.
- The favicon becomes a real hand-authored cut. It cannot be generated: the simplifier is a
  fidelity-preserving optimiser, and a favicon must deliberately render differently.
- One more file per project to keep in sync (`master.svg` vs the committed asset), paid for by
  a byte-equality test rather than by discipline.
- `docs/brand-assets/README.md` becomes the single authoritative source for thresholds and layout;
  `LOGO_REBASELINE_HANDOVER.md` is retired into it.

## References
- [`docs/brand-assets/README.md`](../brand-assets/README.md)
- [`src/shared_utilities/image_drift/promotion.py`](../../src/rig/image_drift/promotion.py)
- [`src/shared_utilities/image_drift/labs_parametric.py`](../../src/rig/image_drift/labs_parametric.py)
- [`tests/test_brand_asset_governance.py`](../../tests/test_brand_asset_governance.py)
