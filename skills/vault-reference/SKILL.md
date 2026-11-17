---
name: vault-reference
description: What every vault skill shares - where the git and Drive copies live, how to work in the vault repo, and the frontmatter and provenance rules the vault's CI enforces. Load it before vault-capture, vault-triage, vault-reconcile, vault-health or vault-portal, and before writing any note in the Obsidian vault.
---

# Vault reference

## Where things are

- **git vault (the record):** `~/code/personal/obsidian-vault`: notes in `Vault/`, scripts in
  `scripts/`, ledgers in `data/`, images in `Vault/Attachments/`. Skills run from any cwd,
  so use absolute paths.
- **Drive vault (what Obsidian and the phone open):** one folder per Drive account under
  `~/Library/CloudStorage/`. Use the personal account's, never the work one:

  ```bash
  export DRIVE_VAULT_PATH="$(ls -d ~/Library/CloudStorage/GoogleDrive-*/"My Drive/Obsidian/Vault")"
  ```

  The folder name is an email address, so never commit it. Only `vault-reconcile` writes
  to Drive.

## Working in the repo

- Prefix git commands that check out files with `GIT_LFS_SKIP_SMUDGE=1` (about 15 GB of LFS).
- Add explicit paths, never `git add -A`. Worktrees go in the gitignored `temp/`.
- Run scripts with `uv run --no-project <script>`.
- Branch, PR, squash-merge. If a rebase stops, resolve it before pushing anything.

## Frontmatter CI enforces

The `Verify OKF Frontmatter` check lints every note a PR touches, diffed against
`origin/main`. A touched note's old faults fail the PR too, so fix them in the same PR.

- `type`: `area`, `concept`, `guide`, `index`, `note`, `policy`, `project`, `reference`,
  `resource` or `resource-hub`.
- `status`: `active`, `archived`, `deprecated`, `draft`, `reference` or `unread`.
- `created` (required) and `updated`: `YYYY-MM-DD`.
- `generated: { by, at }`: required on new notes. No trailing `#` comment on these lines;
  the linter reads it as part of the value.

## Provenance

`generated.by` is `agent:<model-id>` (e.g. `agent:claude-opus-5-5`), `<producer>/<version>`
or `process:<name>`. A bare model id fails. Only a person writes `human:<handle>`, and
`verified:`.

Claim only what you can show: your own model id for a note you write. For an existing
note, find the writer in the session logs (`~/.claude/projects`, `~/.codex/sessions`,
`~/.gemini/`). If nothing shows it, leave `generated:` absent, which means unattributed.

## Lint locally

From the vault, with `rtkelly-labs/github-actions` cloned into `temp/github-actions` at the
SHA pinned in `.github/workflows/okf-lint.yml`:

```bash
VAULT_PATH=Vault BASE_REF=origin/main REQUIRE_PROVENANCE=true uv run --no-project python -c \
  "import sys; sys.path.insert(0, 'temp/github-actions/src'); from estate_actions.cli import run; sys.exit(run('okf-lint'))"
```

An empty `BASE_REF` lints the whole vault, which has thousands of older faults.
