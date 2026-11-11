# image-drift

The brand-asset drift reconciler behind `image-reconcile`: it compares each traced SVG master
against its reference image, explores and promotes new masters, derives production cuts from
`production-cuts.csv`, and serves Asset Studio's API.

It is its own uv project, with its own lock file, checks and coverage floor. It imports nothing
from `rig`. Run it from the repository root:

```bash
uv run --project tools/image-drift image-reconcile --help
uv run --project tools/image-drift image-reconcile serve      # Asset Studio API on :8092
```

The brand-asset rules (what may change a master, how cuts are derived) live in
[docs/brand-assets/README.md](../../docs/brand-assets/README.md) and are authoritative.

## Checks

From this directory:

```bash
uv sync
uv run ruff check . && uv run ruff format --check .
uv run basedpyright && uv run ty check src
uv run ratchet
uv run bandit -r src/ -ll
uv run playwright install chromium   # once, for the rendering tests
uv run pytest --cov=src
```

CI runs the same steps in the `image-drift` job (and `image-drift-windows` on the Windows triggers).

## Asset Studio

`image-reconcile serve` runs the API only: project catalogs, drift diffs, snapshots and the media
they point at, with CORS open so the page can call it. The Asset Studio page itself stays in the
rig web host (`http://127.0.0.1:8090/studio`), which reads the API from
`window.RIG_ASSET_STUDIO_URL` or `http://127.0.0.1:8092`.

## Stylesheet

`styles/gallery.src.css` compiles to `src/image_drift/templates/gallery.css` through the
repository's `pnpm css:build`. The output is committed because the CLI inlines it into a
self-contained `preview.html` and must work without Node; `pnpm css:check` verifies it.
