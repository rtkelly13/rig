# Shared Utilities

> **`the-vault-system` is archived.** Its simplified remainder is the `vault` CLI here
> (`src/Rig.Cli/Vault/`, `src/Rig.Core/Vault/`); see [docs/vault/](docs/vault/README.md).

## Purpose

This repository is for lightweight shared utilities, experiments, helper scripts, and small standalone assets that do not yet justify their own dedicated repository.

It is expected to expand again over time as new utilities are added.

## Extraction Policy

Utilities may be moved out of this repository when they become sufficiently complex or useful as standalone projects.

Typical reasons to extract a utility into its own repository:

- it develops its own tests, CI, or release workflow
- it accumulates substantial documentation or configuration
- it becomes reusable outside this repo's original context
- it has its own roadmap, lifecycle, or contributor surface

When that happens, this repository should keep only a short pointer to the new home rather than carrying partial copies of the extracted project back in.

## Current Contents

This repository currently contains desktop workspace management tooling, the brand-asset and image-drift harness, repository governance automation, shared assets, helper scripts, and small standalone files that support personal development workflows.

## Installation

This project uses [uv](https://docs.astral.sh/uv/) for Python package management.

```bash
# Install uv if you haven't already
curl -LsSf https://astral.sh/uv/install.sh | sh

# Clone the repository
git clone https://github.com/rtkelly13/shared-utilities.git
cd shared-utilities

# Install dependencies
uv sync
```

## Available Tools

### Desktop Manager (`desktop-manager`)

Manage and restore desktop workspace layouts from JSON plans.

```bash
# Show current workspace status
uv run desktop-manager workspace status

# Validate a workspace plan
uv run desktop-manager workspace validate-plan --config desktop-manager/workspace-plan.bundle.json

# Preview a restore without launching anything
uv run desktop-manager workspace restore --config desktop-manager/workspace-plan.bundle.json --dry-run
```

### Image Reconcile (`image-reconcile`)

Generate, score, and export the managed brand marks under [`assets/`](assets/). Drift is
measured against a committed reference baseline, so a visual change cannot land unnoticed.

```bash
# Regenerate a brand mark from its builder
uv run image-reconcile build -p rtkelly-labs

# Score it against the primary baseline
uv run image-reconcile compare -p rtkelly-labs --render

# Export every declared production cut
uv run image-reconcile export-avatar -p rtkelly-labs

# Browse assets, variations, and diffs in the Asset Studio workbench
uv run image-reconcile serve
```

See [`docs/brand-assets/README.md`](docs/brand-assets/README.md) for the governing model, thresholds, and the
promotion gate that protects the traced masters.

## Platform Support

Commands are portable unless listed below. Off their platform, the listed ones exit 1
with `<command> is only available on <platform>.`, and `--help` tags them `[Windows only]`. The table is generated from the `requires` markers in the code;
regenerate it with `uv run python -m shared_utilities.platform_table --write`.

<!-- platform-table:start -->

| Command | Platforms |
| --- | --- |
| `desktop-manager chrome-tabs group` | Windows/macOS |
| `desktop-manager chrome-tabs install` | Windows |
| `desktop-manager chrome-tabs list` | Windows/macOS |
| `desktop-manager chrome-tabs organise` | Windows/macOS |
| `desktop-manager chrome-tabs prune` | Windows/macOS |
| `desktop-manager chrome-tabs rank` | Windows/macOS |
| `desktop-manager chrome-tabs saved-groups` | Windows |
| `desktop-manager chrome-tabs uninstall` | Windows |
| `desktop-manager claude-sessions place` | Windows |
| `desktop-manager claude-sessions terminals` | Windows |
| `desktop-manager jobs install` | Windows |
| `desktop-manager jobs uninstall` | Windows |
| `desktop-manager workspace align` | Windows |
| `desktop-manager workspace audit` | Windows |
| `desktop-manager workspace detect-app` | Windows |
| `desktop-manager workspace ensure-desktops` | Windows |
| `desktop-manager workspace export-plan` | Windows |
| `desktop-manager workspace guard` | Windows |
| `desktop-manager workspace launch-file` | Windows |
| `desktop-manager workspace name-desktops` | Windows |
| `desktop-manager workspace restart-app` | Windows |
| `desktop-manager workspace restore` | Windows |
| `desktop-manager workspace status` | Windows |
| `desktop-manager workspace sync-desktops` | Windows |
| `desktop-manager workspace test-item` | Windows |
| `desktop-manager workspace update-orca` | Windows |
| `desktop-manager workspace wire-machine` | Windows |

<!-- platform-table:end -->

## Development

```bash
# Run tests
uv run pytest

# Format code
uv run ruff format src/

# Lint code
uv run ruff check src/

# Type check
uv run basedpyright
```

## License

Personal project - use at your own risk.
