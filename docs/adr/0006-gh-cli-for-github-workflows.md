# ADR 0006: GitHub CLI (gh) for Transport, Auth, and Automation

- **Status:** Accepted
- **Applies to:** GitHub automation, PR creation, release management

## Context
Automating GitHub operations (creating PRs, querying repository metadata, managing stacked diffs) often leads to maintaining custom OAuth tokens or fragile REST scripts.

## Decision
Standardize on the official **GitHub CLI (`gh`)** for all GitHub transport, authentication, and PR workflows.
- Reuse existing `gh` authentication credentials for local and CI operations.
- Automate stacked PR creation via `gh pr create --base <parent> --head <branch>`.

## Consequences
- Zero custom credential management or API token exposure in scripts.
- Consistent behavior across macOS, Linux, and Windows.

## References
- [GitHub CLI Manual](https://cli.github.com/manual/)
