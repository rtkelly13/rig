# ADR 0002: uv for Python Packaging and Execution

- **Status:** Superseded by [0022](0022-csharp-is-rigs-primary-language.md), except for `image_drift`
- **Applies to:** all Python modules, CLI utilities, and scripts

## Context
Python dependency management and virtual environment management traditionally suffered from slow installation times, competing toolchains (pip, poetry, pipenv), and global environment pollution.

## Decision
Standardize on Astral's **`uv`** as the universal Python package manager and execution engine for all Python projects and CLI tools in `shared-utilities`.
- All Python scripts and tools are invoked via `uv run <command>`.
- Dependencies are locked in `pyproject.toml` / `uv.lock`.
- Python runtimes are installed and managed via `uv python install`.

## Consequences
- Single tool for project management, virtual environments, tool running, and Python version switching.
- Extremely fast, reproducible installs across macOS and Windows.
- Eliminates global Python dependency conflicts.

## References
- [uv Documentation](https://docs.astral.sh/uv/)
