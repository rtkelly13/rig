# Asset Studio & Feedback Web Dashboard Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a high-performance local web dashboard (`Asset Studio`) that indexes estate asset repositories, regularizes naming conventions, captures ephemeral pipeline snapshots in a git-ignored directory (`temp/variations/`), and provides a visual comparison workbench with structured feedback tooling for AI agents.

**Architecture:** Python 3.13 backend using `ThreadingHTTPServer` with JSON REST APIs and static asset serving, coupled with an interactive web UI. An asset indexer scans `assets/<project>/`, `assets/<project>/reference/`, and `temp/variations/` using standardized naming tokens. A snapshot manager records pipeline stages without polluting git.

**Tech Stack:** Python 3.13 (`http.server`, `urllib`, `json`, `pathlib`), Typer CLI (`image-reconcile serve`), Pillow & NumPy (drift metrics & diff heatmaps), HTML5/CSS3/Vanilla JS built on `@rtkelly13/design-system` foundational elements (Midnight level, brutalist 0px radius, hard offset shadows, Space Grotesk / JetBrains Mono typography, role-based color tokens).

**Spec:** Local estate asset visualizer with consistent folder indexing, git-ignored ephemeral variation storage, human-in-the-loop agent feedback generation, and foundational brutalist UI tokens from `@rtkelly13/design-system`.


## Global Constraints

- Never commit ephemeral variations or test runs to primary git branches (must live under git-ignored `temp/variations/`).
- Zero loose files in `assets/` root; all project files strictly segregated in `assets/<project>/` and `assets/<project>/reference/`.
- Zero new runtime npm or Python framework dependencies (use standard library `http.server` + existing `pillow`, `jinja2`, `rich`, `typer`).
- All python commands run via `uv run` or `uv`. Never run bare `python` or `python3`.

---

### Task 1: Asset Indexer & Naming Regularization Engine

**Files:**
- Create: `src/shared_utilities/image_drift/indexer.py`
- Test: `tests/test_image_drift_indexer.py`

**Interfaces:**
- Produces: `AssetIndexer`, `ProjectAssetCatalog`, `ReferenceAsset`, `ProductionCut`, `VariationRun`, `SnapshotStep`
- Naming format: `{project}__{run_id}__{step_idx:02d}_{step_name}__{timestamp}.{ext}`

- [x] **Step 1: Write the failing tests for asset indexer**

```python
# tests/test_image_drift_indexer.py
from pathlib import Path
from shared_utilities.image_drift.indexer import AssetIndexer

def test_index_projects(tmp_path: Path) -> None:
    # Setup mock asset hierarchy
    proj = tmp_path / "assets" / "sample-brand"
    proj.mkdir(parents=True)
    (proj / "sample-brand.svg").write_text("<svg></svg>")
    (proj / "sample-brand-logo-1000x1000.png").write_bytes(b"png")
    
    ref = proj / "reference"
    ref.mkdir()
    (ref / "01-concept.jpg").write_bytes(b"jpg")
    (ref / "02-baseline.png").write_bytes(b"png")
    
    indexer = AssetIndexer(assets_root=tmp_path / "assets", variations_root=tmp_path / "temp" / "variations")
    catalog = indexer.index_project("sample-brand")
    
    assert catalog.name == "sample-brand"
    assert len(catalog.references) == 2
    assert catalog.master_svg is not None
    assert catalog.master_png is not None
```

- [x] **Step 2: Run tests to verify failure**

Run: `uv run pytest tests/test_image_drift_indexer.py`

- [x] **Step 3: Implement `indexer.py`**

Implement dataclasses and scanner for `assets/` and `temp/variations/` with regex-based naming parser and sorting.

- [x] **Step 4: Run tests to verify they pass**

Run: `uv run pytest tests/test_image_drift_indexer.py`

- [x] **Step 5: Commit task deliverable**

```bash
git add src/shared_utilities/image_drift/indexer.py tests/test_image_drift_indexer.py
git commit -m "feat(image-drift): implement asset catalog indexer and naming regularizer"
```

---

### Task 2: Git-Ignored Snapshot Manager for Pipeline Variations

**Files:**
- Create: `src/shared_utilities/image_drift/snapshots.py`
- Test: `tests/test_snapshot_manager.py`

**Interfaces:**
- Consumes: `indexer.py` data structures
- Produces: `SnapshotManager.capture_step(...)`, `SnapshotManager.list_runs(...)`, `SnapshotManager.get_run(...)`
- Target path: `<repo_root>/temp/variations/<project>/<run_id>/...`

- [x] **Step 1: Write failing tests for snapshot manager**

```python
# tests/test_snapshot_manager.py
from pathlib import Path
from shared_utilities.image_drift.snapshots import SnapshotManager

def test_capture_and_list_snapshots(tmp_path: Path) -> None:
    mgr = SnapshotManager(variations_root=tmp_path / "temp" / "variations")
    step = mgr.capture_step(
        project="rtkelly-labs",
        run_id="cad-iteration-1",
        step_idx=1,
        step_name="initial-geometry",
        svg_content="<svg viewBox='0 0 100 100'></svg>",
        metrics={"ssim": 0.965, "rmse_pct": 3.8},
        notes="First draft of 15 facets"
    )
    assert step.svg_path.exists()
    assert step.metadata_path.exists()
    assert step.step_name == "initial-geometry"
```

- [x] **Step 2: Run tests to verify failure**

Run: `uv run pytest tests/test_snapshot_manager.py`

- [x] **Step 3: Implement `snapshots.py`**

Implement `SnapshotManager` saving `.svg`, `.png`, and `.json` metadata with timestamping and manifest generation in `temp/variations/`.

- [x] **Step 4: Run tests to verify they pass**

Run: `uv run pytest tests/test_snapshot_manager.py`

- [x] **Step 5: Commit task deliverable**

```bash
git add src/shared_utilities/image_drift/snapshots.py tests/test_snapshot_manager.py
git commit -m "feat(image-drift): add snapshot manager for ephemeral pipeline variations"
```

---

### Task 3: Local Dashboard HTTP Server & REST API

**Files:**
- Create: `src/shared_utilities/image_drift/server.py`
- Test: `tests/test_dashboard_server.py`

**Interfaces:**
- Endpoints:
  - `GET /api/projects` -> list of all managed brands
  - `GET /api/project/{name}` -> detailed catalog (cuts, references, variations)
  - `GET /api/diff?ref={path}&cand={path}` -> on-the-fly diff metrics & heatmap
  - `POST /api/snapshot` -> capture live snapshot
  - `POST /api/feedback` -> record structured feedback for agent
  - Static file handler serving `assets/` and `temp/variations/`

- [x] **Step 1: Write failing test for server endpoints**

```python
# tests/test_dashboard_server.py
from pathlib import Path
from shared_utilities.image_drift.server import create_studio_app

def test_api_projects_endpoint(tmp_path: Path) -> None:
    # Test JSON API response for projects index
    ...
```

- [x] **Step 2: Run tests to verify failure**

Run: `uv run pytest tests/test_dashboard_server.py`

- [x] **Step 3: Implement `server.py` using standard library `http.server`**

Implement `StudioRequestHandler` and `StudioServer` supporting threading and clean REST JSON routing.

- [x] **Step 4: Run tests to verify they pass**

Run: `uv run pytest tests/test_dashboard_server.py`

- [x] **Step 5: Commit task deliverable**

```bash
git add src/shared_utilities/image_drift/server.py tests/test_dashboard_server.py
git commit -m "feat(image-drift): implement local dashboard server and REST API"
```

---

### Task 4: Interactive Web UI Frontend & Agent Feedback Console

**Files:**
- Create: `src/shared_utilities/image_drift/templates/index.html`
- Create: `src/shared_utilities/image_drift/templates/studio.css`
- Create: `src/shared_utilities/image_drift/templates/studio.js`
- Create: `src/shared_utilities/image_drift/templates/design_system/theme.css` (vendored from `@rtkelly13/design-system`)
- Create: `src/shared_utilities/image_drift/templates/design_system/styles.css` (vendored from `@rtkelly13/design-system`)
- Test: `tests/test_dashboard_frontend.py`

**Design System Foundations (`@rtkelly13/design-system`):**
- **Level / Theme**: `data-theme="midnight"` (pure blue-black `--ds-surface-base: #0a0a1a`, raised `--ds-surface-raised: #141428`).
- **Brutalist DNA**: $0\text{px}$ radius everywhere (`--ds-radius-none`), hard offset shadows (`4px 4px 0px var(--ds-accent-primary)`), no blur.
- **Role-Based Inks**: `--ds-accent-primary` (`#22d3ee`), `--ds-accent-secondary` (`#facc15`), `--ds-intent-success` (`#39ff14`), `--ds-intent-danger` (`#f955a4`).
- **Typography & Labels**: Space Grotesk / JetBrains Mono with bracketed terminal headers: `[ ASSET_STUDIO.exe ]`, `[ CATALOG ]`, `[ WORKBENCH ]`, `[ AGENT_FEEDBACK ]`.

**UI Features:**
- **Navigation**: Project switcher (`rtkelly-labs`, `parquet-source-generator`, `testing`), Reference Timeline, Production Cuts, Variations Tab.
- **Comparison Workbench**:
  - Side-by-side mode (Reference vs Candidate)
  - Interactive split-slider overlay (wipe to compare alignment)
  - Synchronized pan & zoom (100% to 800%)
  - Difference Heatmap toggle
- **Agent Feedback Console**:
  - Focus Area selector (Overall, Center Crossover, Fork Lines, Facet Bevels, Silhouette)
  - Issue tags (Alignment, Depth/Shadow, Color/Gradient, Sharpness, Hairline Gaps)
  - Verdict (Approve, Iterate, Reject)
  - Actionable prompt generator with "Copy for Agent" button
  - Saves to `temp/agent_feedback.md`

- [x] **Step 1: Copy foundational CSS from `@rtkelly13/design-system` (`dist/theme.css`, `dist/styles.css`)**
- [x] **Step 2: Build HTML/CSS/JS with Midnight theme and brutalist tokens**
- [x] **Step 3: Wire templates into `server.py`**
- [x] **Step 4: Test template rendering and UI endpoints**

- [x] **Step 4: Verify via Playwright / unit tests**
- [x] **Step 5: Commit task deliverable**

```bash
git add src/shared_utilities/image_drift/templates/ tests/test_dashboard_frontend.py
git commit -m "feat(image-drift): add interactive studio UI and agent feedback console"
```

---

### Task 5: CLI Integration & Verification

**Files:**
- Modify: `src/shared_utilities/image_drift/cli.py`
- Test: `tests/test_image_drift_cli.py`

**CLI Commands:**
- `uv run image-reconcile serve [--port 8080] [--host 127.0.0.1] [--open/--no-open]`
- `uv run image-reconcile snapshot --project <name> --name <step_name>`

- [x] **Step 1: Write CLI tests for `serve` and `snapshot` subcommands**
- [x] **Step 2: Implement commands in `cli.py`**
- [x] **Step 3: Verify all test cases pass**
- [x] **Step 4: Run typecheck (`pyright`) and linter (`ruff`)**
- [x] **Step 5: Commit final CLI integration**

```bash
git add src/shared_utilities/image_drift/cli.py tests/test_image_drift_cli.py
git commit -m "feat(cli): add image-reconcile serve and snapshot commands"
```
