# Logo explorers

Two logo marks that were once candidates for the Parquet.SourceGenerator brand and are now
unscored: no promote gate, pathway or test compares them against a reference. They lived in
`src/rig/image_drift/` until plan item C6 of rtkelly13/rig#411 moved them here.

| File | What it is |
|---|---|
| `svg_builder.py` | The interlocking-columns mark (`build_svg_logo`, variants `columns` and `favicon`), plus the document scaffold (`prepare_document`, wood tones, gradients) the rhombille mark draws on. |
| `rhombille_builder.py` | The hexagon-and-circuit mark: a rhombille or herringbone tiling inside a glowing frame, with a circuit network and etched emblems. |
| `hex_shapes.py` | The measured frame, tiling, socket and circuit geometry both marks use. |
| `tests/` | Their tests, moved from `tests/test_image_drift_builder.py` and `tests/test_image_drift_columns.py`. |

They import `FeatureRegion`, `Style`, `variant_style`, `Point` and `hexagon_vertices` from the
package (`rig.image_drift.builder_contract` and `rig.image_drift.hex_geometry`), so a builder
loaded with `--builder-module` passes the CLI's type checks. They import each other by bare module
name, so this folder has to be on `sys.path`: `conftest.py` does that for pytest, `PYTHONPATH` for
the CLI.

## What is not checked

This code is **not** type-checked (basedpyright and ty cover `src/` only), **not** ratcheted,
and its tests are **not** run in CI (pytest collects `tests/` only). CI's `ruff check .` and
`ruff format --check .` do still lint it. If the package changes underneath it, it can break
without anyone noticing.

## Running

From the repository root:

```bash
# Tests
uv run pytest experiments/logo-explorers -p no:randomly

# Build a mark through the image-drift CLI's builder-module hook
PYTHONPATH=experiments/logo-explorers uv run image-reconcile build \
    -B experiments/logo-explorers/svg_builder.py --variant columns --svg temp/columns.svg
PYTHONPATH=experiments/logo-explorers uv run image-reconcile build \
    -B experiments/logo-explorers/rhombille_builder.py --variant rhombille_reference --svg temp/hex.svg
```

If one of them earns a place again, it goes back into the package with a reference to score it
against, not as an explorer.
