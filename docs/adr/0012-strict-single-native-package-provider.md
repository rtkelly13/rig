# ADR 0012: Strict Single Native Package Provider per OS

- **Status:** Accepted
- **Applies to:** `devenv` package management

## Context
Cross-platform developer tooling management can easily become fragile when attempting to juggle multiple secondary package managers (e.g. mixing Homebrew and MacPorts on macOS, or Winget and Scoop on Windows).

## Decision
`devenv` enforces a strictly single native package manager provider per operating system:
- **macOS / Linux**: Homebrew (`brew`)
- **Windows**: Windows Package Manager (`winget`)

Package managers are abstracted behind the `PackageManager` interface with methods for `is_installed`, `install`, `update`, and `update_all`. Unsupported tools on a given OS provider are skipped with clear diagnostic guidance rather than chaining secondary managers.

## Consequences
- Maximizes reliability, minimizes prerequisite dependencies, and avoids binary collisions.
- Native silent/unattended flags are consistently supported.

## References
- [`src/shared_utilities/devenv/providers/base.py`](../../src/rig/devenv/providers/base.py)
