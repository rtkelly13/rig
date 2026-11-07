# SVG Multi-Tool Simplification Pipeline: Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:subagent-driven-development` (recommended) or `superpowers:executing-plans` to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a multi-pass SVG simplification pipeline combining geometric curve/node decimation (`shapely` + Bézier refitting), syntactic document scrubbing (`scour`), AST-level production minification (`svgo`), and closed-loop visual drift verification (`image-reconcile` Playwright rendering with SSIM/PSNR/RMSE gating).

**Architecture:** A 5-pass progressive optimization pipeline:
1. **Pass 1 (Scour / AST Normalization):** Scrub XML metadata, doctypes, comments, namespaces, and empty groups.
2. **Pass 2 (Geometric & Topology Decimation):** Tokenize SVG path commands, partition into subpaths, classify open strokes vs closed polygons vs compound holes, apply GEOS Douglas-Peucker (`shapely`) to polylines, and apply curvature-aware knot decimation to Bézier segments.
3. **Pass 3 (Syntactic Minification & Coordinate Quantization):** Apply viewBox-relative coordinate quantization and invoke `svgo` (via non-interactive `npx -y svgo`) for relative command folding, arc transformation, and path merging.
4. **Pass 4 (Closed-Loop Drift Verification):** Render pre- and post-optimization SVGs to high-resolution PNGs via Playwright, compute SSIM, PSNR, and RMSE metrics, and execute an adaptive tolerance backoff if drift exceeds the visual error budget ($\text{SSIM} < 0.995$).
5. **Pass 5 (Estate & Studio Integration):** Emit production assets and optionally snapshot the optimization run directly into `temp/variations/<project>/` for real-time comparative inspection in the Asset Studio workbench.

**Tech Stack:** Python 3.13, `shapely` (GEOS C-bindings), `scour` (pure Python SVG scrubber), `svgo` (Node.js via `npx -y svgo`), `playwright` (Chromium raster rendering), `numpy`, `pillow`, `rich`, `typer`.

---

## 1. Multi-Tool Pipeline Architecture

```mermaid
flowchart TD
    In["Raw Canonical SVG"] --> P1["Pass 1: Document Scrubbing (scour)"]
    P1 -->|Clean Document AST| P2["Pass 2: Geometric & Topological Decimation"]
    
    subgraph P2Details["Pass 2: Geometry Engine"]
        P2A["Path Tokenizer & Subpath Classifier"]
        P2B["Closed Polygons: Shapely RDP (preserve_topology=True)"]
        P2C["Open Strokes: Shapely LineString RDP"]
        P2D["Bézier Curves: Tangent & Curvature Knot Decimation"]
        P2A --> P2B
        P2A --> P2C
        P2A --> P2D
    end
    
    P2Details --> P3["Pass 3: Syntactic Minification (svgo + ViewBox Quantize)"]
    P3 --> P4["Pass 4: Closed-Loop Drift Verification"]
    
    subgraph P4Details["Pass 4: Visual Regression Gating"]
        Render["Render Pre/Post SVGs via Playwright"]
        Metrics["Compute SSIM, PSNR, RMSE"]
        Gate{"SSIM >= 0.995\n& RMSE <= 1.0%?"}
        Render --> Metrics --> Gate
    end
    
    Gate -->|Yes| Out["Optimized Production SVG"]
    Gate -->|No (Drift Exceeded)| Backoff["Adaptive Tolerance Binary Backoff"]
    Backoff --> P2
    
    Out --> P5["Pass 5: Snapshot into Asset Studio (temp/variations/)"]
```

---

## 2. Proposed Module Structure

All new code lives inside `src/shared_utilities/image_drift/simplifier/`:

```
src/shared_utilities/image_drift/simplifier/
├── __init__.py               # Public API exports
├── ast_parser.py             # Full SVG path tokenizer, command model, subpath partitioner
├── geometric.py              # Shapely GEOS polyline decimation & Bézier curve decimation
├── syntactic.py              # scour & svgo subprocess execution, coordinate quantization
├── pipeline.py               # 5-pass orchestrator, drift verification, and adaptive search
└── report.py                 # Structured dataclasses for optimization metrics & drift
```

Dependencies added to `pyproject.toml`:
- `shapely>=2.0.0`
- `scour>=0.38.2`

Tooling prerequisites:
- `node` / `npx` (already available at `/opt/homebrew/bin/npx`, running `npx -y svgo`)

---

## 3. Detailed Work Breakdown & Tasks

### Task 1: Environment & Dependencies
- [ ] Add `shapely` and `scour` to `pyproject.toml` dependencies.
- [ ] Run `uv sync` to update the virtual environment and lockfile.
- [ ] Verify `import shapely` and `import scour` succeed under `uv run python`.
- [ ] Commit: `chore(deps): add shapely and scour for SVG simplification pipeline`.

### Task 2: SVG Path AST Parser (`ast_parser.py`)
- [ ] Implement `PathCommand` dataclass representing `(command_char, list[float])`.
- [ ] Implement `SVGPathTokenizer` with full SVG 1.1/2.0 specification compliance:
  - Supports `M/m`, `L/l`, `H/h`, `V/v`, `C/c`, `S/s`, `Q/q`, `T/t`, `A/a`, `Z/z`.
  - Supports scientific notation: `1.23e-4`.
  - Supports negative numbers without delimiter: `10-20` $\to$ `[10.0, -20.0]`.
  - Supports compact coordinates without leading zero: `.5.2` $\to$ `[0.5, 0.2]`.
  - Strictly preserves binary boolean flags in elliptical arc commands (`A rx ry angle large_arc sweep x y`).
- [ ] Implement `SubPath` data model:
  - Partitions full path `d` attribute into distinct subpaths separated by `M/m`.
  - Detects closed contours (`is_closed`) vs open stroke lines.
  - Detects compound paths (outer boundary vs inner cutout holes).
- [ ] Implement path re-serializer:
  - Formats commands with configurable float precision.
  - Implements relative delta folding (emits `m/l/c` if shorter than `M/L/C`).
- [ ] Write unit tests in `tests/test_svg_ast_parser.py`.
- [ ] Commit: `feat(image_drift): implement robust SVG path tokenizer and subpath model`.

### Task 3: Geometric & Topological Decimation Engine (`geometric.py`)
- [ ] Implement `simplify_polyline_with_shapely`:
  - For closed contours: construct `shapely.geometry.Polygon` (with interior hole rings preserved if compound) and apply `poly.simplify(tolerance, preserve_topology=True)`.
  - For open contours: construct `shapely.geometry.LineString` and apply `line.simplify(tolerance, preserve_topology=True)`.
  - Never add an artificial closing chord or `Z` to open strokes.
- [ ] Implement Bézier curve decimation:
  - Collinear segment detection: if control points lie within distance $\epsilon$ of the baseline chord, convert cubic `C` into linear `L`.
  - Redundant node pruning: collapse zero-length segments and merge consecutive collinear lines sharing tangent direction.
  - Tangent preservation: protect $C^1$ continuous control handles across adjacent curve segments.
- [ ] Assemble `simplify_path_geometry(d_string, tolerance)` returning clean, optimized path data.
- [ ] Write unit tests in `tests/test_svg_geometric_simplifier.py`.
- [ ] Commit: `feat(image_drift): add curve-preserving geometric decimation via shapely and bezier pruning`.

### Task 4: Syntactic Minification & Multi-Tool Scrubbing (`syntactic.py`)
- [ ] Implement `run_scour_pass(svg_text)`:
  - Invokes `scour.scour.scourString` to strip metadata, XML namespaces, comments, empty `<g>` elements, and unused defs.
- [ ] Implement `run_svgo_pass(svg_text)`:
  - Executes `npx -y svgo --input - --output -` via subprocess.
  - Passes optimal configuration: `multipass: true`, `removeViewBox: false`, `collapseGroups: true`.
  - Gracefully falls back to pure-Python syntactic minification if `npx` is unavailable.
- [ ] Implement dynamic viewBox coordinate quantization:
  - Extracts SVG `viewBox` width and height.
  - Computes precision: $\max(1, \lceil \log_{10}(1000 / \max(W, H)) \rceil + 1)$.
- [ ] Write unit tests in `tests/test_svg_syntactic_minifier.py`.
- [ ] Commit: `feat(image_drift): add multi-tool syntactic minification passes with scour and svgo`.

### Task 5: Closed-Loop Drift-Guided Pipeline (`pipeline.py`)
- [ ] Build `SVGSimplificationPipeline`:
  - Coordinates Pass 1 (Scour) $\to$ Pass 2 (Geometric) $\to$ Pass 3 (SVGO/Syntactic).
  - Renders pre- and post-optimization SVGs using Playwright `render_svg`.
  - Measures visual regression: SSIM, PSNR, RMSE.
  - If $\text{SSIM} < \text{min\_ssim}$ (default 0.995), initiates binary search backoff on `tolerance` ($\epsilon / 2$) until visual quality threshold is satisfied.
  - Returns `OptimizationReport` with size reduction, node decimation percentage, and drift metrics.
- [ ] Write integration tests in `tests/test_svg_simplifier_pipeline.py`.
- [ ] Commit: `feat(image_drift): implement drift-guided closed-loop SVG simplification pipeline`.

### Task 6: CLI & Asset Studio Integration
- [ ] In `src/shared_utilities/image_drift/cli.py`, add the `simplify` command:
  ```bash
  image-reconcile simplify <svg_path> [OPTIONS]
    -t, --tolerance FLOAT       Geometric error tolerance (default: 0.5 px)
    --min-ssim FLOAT            Minimum allowed SSIM threshold (default: 0.995)
    --max-rmse FLOAT            Maximum allowed RMSE % (default: 1.0%)
    -o, --output PATH           Output path for simplified SVG
    -i, --in-place              Overwrite input SVG directly
    -s, --snapshot              Save snapshot variation run for Asset Studio inspection
    --passes TEXT               Comma-separated passes: scour,geom,svgo (default: all)
  ```
- [ ] Integrate with `temp/variations/<project>/` so running with `--snapshot` immediately shows the simplified candidate in the Asset Studio workbench sidebar.
- [ ] Write CLI tests in `tests/test_image_drift_cli.py`.
- [ ] Commit: `feat(cli): add image-reconcile simplify command with Asset Studio snapshot integration`.

### Task 7: Estate Benchmark Verification & Documentation
- [ ] Run the pipeline on `assets/parquet-source-generator/parquet-diamond.svg`:
  - Measure initial size (72.8 KB) vs optimized size and SSIM.
- [ ] Run the pipeline on `assets/rtkelly-labs/rtkelly-labs.svg`:
  - Measure initial size (11.4 KB) vs optimized size and SSIM.
- [ ] Open Asset Studio and visually inspect before/after in side-by-side, split-slider, and diff-heatmap modes.
- [ ] Document the simplification workflow in `docs/brand-assets/image-vectorization-harness.md`.
- [ ] Commit: `docs: document SVG simplification pipeline and estate benchmark results`.

---

## 4. Verification Plan

### Automated Test Suite
- `pytest tests/test_svg_ast_parser.py`: Unit test path parsing, tokenization, arc flags, and compact delimiters.
- `pytest tests/test_svg_geometric_simplifier.py`: Unit test polyline RDP, Bézier curve preservation, and open stroke safety.
- `pytest tests/test_svg_syntactic_minifier.py`: Unit test `scour` and `svgo` subprocess passes.
- `pytest tests/test_svg_simplifier_pipeline.py`: End-to-end multi-pass pipeline test with drift threshold assertions.
- `pytest tests/test_image_drift_cli.py`: Test `image-reconcile simplify` CLI execution and argument parsing.
- Full suite verification: `uv run pytest tests/test_dashboard_*.py tests/test_image_drift_*.py`.
- Quality gates: `uv run ruff check src/ tests/` and `uv run pyright`.

### Manual & Visual Verification
1. Run `uv run image-reconcile simplify assets/parquet-source-generator/parquet-diamond.svg --snapshot`.
2. Open `http://127.0.0.1:8080` in `agent-browser`.
3. Select the newly captured simplification variation in the `[ VARIATIONS_&_SNAPSHOTS ]` tree.
4. Toggle to `[ SPLIT_SLIDER ]` and `[ DIFF_HEATMAP ]` to verify zero perceptible geometric shift.
