# ADR 0004: ruff for Sub-Second Linting and Code Formatting

- **Status:** Superseded by [0022](0022-csharp-is-rigs-primary-language.md), except for `image_drift`
- **Applies to:** all Python code in the repository

## Context
Using separate tools for formatting (Black), import sorting (isort), and linting (Flake8/Pylint) introduced configuration fragmentation and sluggish execution in local and CI loops.

## Decision
Standardize on **`ruff`** for all Python code linting and formatting.
- Enforce rules for import organization (`I`), modern Python idioms (`UP`), bug prevention (`B`), and path practices (`PTH`).
- Run formatting and linting via `uv run ruff check --fix` and `uv run ruff format`.

## Consequences
- Sub-second linting and formatting across hundreds of files.
- Single configuration block in `pyproject.toml`.

## References
- [Ruff Documentation](https://docs.astral.sh/ruff/)
