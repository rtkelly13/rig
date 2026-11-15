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

### Desktop Workspaces (`rig desktop workspace`)

Manage and restore Windows desktop workspace layouts from JSON plans.

```bash
# Show current workspace status
rig desktop workspace status

# Validate a workspace plan
rig desktop workspace validate-plan --config desktop-manager/workspace-plan.bundle.json

# Preview a restore without launching anything
rig desktop workspace restore --config desktop-manager/workspace-plan.bundle.json --dry-run
```

### Image Reconcile (`image-reconcile`)

Generate, score, and export the managed brand marks under [`assets/`](assets/). Drift is
measured against a committed reference baseline, so a visual change cannot land unnoticed.

```bash
# Regenerate a brand mark from its builder
uv run --project tools/image-drift image-reconcile build -p rtkelly-labs

# Score it against the primary baseline
uv run --project tools/image-drift image-reconcile compare -p rtkelly-labs --render

# Export every declared production cut
uv run --project tools/image-drift image-reconcile export-avatar -p rtkelly-labs

# Browse assets, variations, and diffs in the Asset Studio workbench
uv run --project tools/image-drift image-reconcile serve
```

See [`docs/brand-assets/README.md`](docs/brand-assets/README.md) for the governing model, thresholds, and the
promotion gate that protects the traced masters.

## Platform Support

Commands are portable unless listed below. Off their platform, the listed ones exit 1
with `<command> is only available on <platform>.`, and `--help` tags them `[Windows only]`. The table is generated from the `requires` markers in the code;
regenerate it with `uv run python -m rig.platform_table --write`.

<!-- platform-table:start -->

| Command | Platforms |
| --- | --- |
| `desktop-manager chrome-tabs organise` | Windows/macOS |

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
