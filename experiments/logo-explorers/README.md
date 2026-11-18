# Logo explorers

Two logo marks that were once candidates for the Parquet.SourceGenerator brand and are now
unscored: no promote gate, pathway or test compares them against a reference.

| File | What it is |
|---|---|
| `svg_builder.py` | The interlocking-columns mark (`build_svg_logo`, variants `columns` and `favicon`), plus the document scaffold (`prepare_document`, wood tones, gradients) the rhombille mark draws on. |
| `rhombille_builder.py` | The hexagon-and-circuit mark: a rhombille or herringbone tiling inside a glowing frame, with a circuit network and etched emblems. |
| `hex_shapes.py` | The measured frame, tiling, socket and circuit geometry both marks use. |
| `tests/` | Their tests. |

## Status: not runnable

These files import their types from a Python package that rig no longer ships: the image pipeline is C#
(`rig image`, `src/Rig.Image`), so nothing here is type-checked, ratcheted, linted for types or run in CI, and
the imports do not resolve. The folder is kept as a reference for the two marks' geometry. They import each
other by bare module name, so this folder has to be on `sys.path`: `conftest.py` does that for pytest.

If one of them earns a place again, it goes back into `src/Rig.Image` as a C# builder with a reference to score
it against, not as an explorer.
