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

This repository currently contains desktop workspace management tooling, the brand-asset image pipeline, repository governance automation, shared assets, helper scripts, and small standalone files that support personal development workflows.

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

### Image pipeline (`rig image`)

Generate, score, and export the managed brand marks under [`assets/`](assets/). Drift is
measured against a committed reference baseline, so a visual change cannot land unnoticed.

```bash
# Regenerate a brand mark from its builder
rig image build -p rtkelly-labs

# Score it against the primary baseline
rig image compare -p rtkelly-labs

# Export every declared production cut
rig image export-avatar -p rtkelly-labs

# Browse assets, variations, and diffs in the Asset Studio workbench (served by rig web)
rig web   # the Asset Studio is at /studio
```

See [`docs/brand-assets/README.md`](docs/brand-assets/README.md) for the governing model, thresholds, and the
promotion gate that protects the traced masters.

## Development

```bash
dotnet build rig.slnx -m:2        # build
dotnet test --solution rig.slnx   # run the tests
dotnet format rig.slnx            # format
```

See [docs/development.md](docs/development.md) for every gate.

## License

Personal project - use at your own risk.
