# ADR 0014: Multi-Identity Git Routing via Conditional Directory Includes

- **Status:** Accepted
- **Applies to:** `devenv` Git identity and SSH setup step

## Context
Developers frequently contribute to personal open-source projects, employer repositories, and client organizations from the same workstation. Managing multiple Git identities via manual configuration switches often leads to accidental commit author misattribution or signing key leakage.

## Decision
Git identity management (`rig devenv git`) adopts native Git conditional directory includes (`[includeIf "gitdir:..."]`).
- Each configured identity in `DevenvConfig.identities` generates a modular profile file (`~/.gitconfig-<id>`) defining `user.name`, `user.email`, and optional `signingkey`.
- The root `~/.gitconfig` is updated with directory match rules (e.g. `[includeIf "gitdir:~/code/personal/"] path = ~/.gitconfig-personal`).

## Consequences
- Context switching is 100% automated based on folder path; impossible to commit personal work under a work email.
- Supports distinct GPG/SSH commit signing keys per identity.

## References
- [`src/Rig.Core/Devenv/GitSetup.cs`](../../src/Rig.Core/Devenv/GitSetup.cs)
- [Git Documentation — Conditional Includes](https://git-scm.com/docs/git-config#_conditional_includes)
