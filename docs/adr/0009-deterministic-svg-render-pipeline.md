# ADR 0009: Deterministic SVG Logo and Graphic Render Pipeline

- **Status:** Accepted
- **Applies to:** `Rig.Image` / visual assets

## Context
Exporting graphic assets, SVG icons, and visual branding elements often suffered from non-deterministic rendering, platform-specific font drift, and uncommitted image artifacts.

## Decision
Adopt a deterministic SVG pipeline in `Rig.Image`:
- Reusable modular renderers producing pixel-identical SVGs across macOS, Linux, and Windows.
- Automated drift detection to catch unrendered visual changes in CI.

## Consequences
- Visual artifacts are testable and deterministic in version control.

## References
- [`src/Rig.Image`](../../src/Rig.Image)
