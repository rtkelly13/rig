---
name: vault-reference
description: Shared reference for every vault skill - where the two vault copies live, how to work in the vault repo, the frontmatter and provenance rules CI enforces, and how to lint and land a vault PR. Load it before vault-capture, vault-triage, vault-reconcile, vault-health or vault-portal, and before writing or editing any note in the Obsidian vault.
---

# Vault reference

The other vault skills (`vault-capture`, `vault-triage`, `vault-reconcile`, `vault-health`,
`vault-portal`) each cover one job. What they share lives here, once. Read this first; the
rules below are what the vault's CI checks, and getting them wrong fails the PR.

## Where things are

| What | Where |
|---|---|
| git vault (the record) | `~/code/personal/obsidian-vault`, notes under `Vault/` |
| Vault scripts | `~/code/personal/obsidian-vault/scripts/` |
| Capture and triage ledgers | `~/code/personal/obsidian-vault/data/` |
| Saved images | `Vault/Attachments/` (X images in `Vault/Attachments/x/`) |
| Drive vault (what Obsidian and the phone open) | `~/Library/CloudStorage/GoogleDrive-<account>/My Drive/Obsidian/Vault` |

The skills are installed globally, so the cwd is not guaranteed to be the vault: `cd` there
or use absolute paths.

Google Drive for desktop mounts one folder per signed-in account. The vault is in the
**personal** account's folder, not the work one; the old `~/Google Drive/` shortcut no
longer exists. Find it and export it once per session:

```bash
export DRIVE_VAULT_PATH="$(ls -d ~/Library/CloudStorage/GoogleDrive-*/"My Drive/Obsidian/Vault")"
```

The glob must match exactly one folder. The `<account>` part is an email address: never
write it into a committed file.

**Never write to the Drive copy** except through `vault-reconcile`. `Inbox/Links.md` in
particular has one writer, a person on a phone.

## Working in the vault repo

- **LFS:** the repo carries about 15 GB of LFS audio and books. Prefix checkouts, worktree
  adds, rebases and pulls with `GIT_LFS_SKIP_SMUDGE=1`.
- **Never `git add -A`.** Add explicit paths: `git diff --name-only -z | xargs -0 git add --`.
- **Worktrees** go in the repo's gitignored `temp/`.
- **Scripts:** run with `uv run --no-project <script>`, never bare `python3`.
- **Branches and PRs:** never commit to `main`. One PR per change, squash-merged. Use
  follow-up commits on a pushed branch. After a rebase, push with
  `--force-with-lease=<branch>:<old-sha>`.
- **Rebasing:** stop if `git rebase` stops. Check for conflicts before pushing anything;
  pushing a branch while a rebase is paused can push the base, and GitHub closes the PR.

## Frontmatter, as CI checks it

The `Verify OKF Frontmatter` check runs okf-lint (`rtkelly-labs/github-actions`, pinned in
the vault's `.github/workflows/okf-lint.yml`) on every note a PR touches.

| Field | Rule | Code |
|---|---|---|
| `type` | required; one of `area`, `concept`, `guide`, `index`, `note`, `policy`, `project`, `reference`, `resource`, `resource-hub` | OKF002, OKF003 |
| `status` | required; one of `active`, `archived`, `deprecated`, `draft`, `reference`, `unread` | OKF004, OKF005 |
| `created` | required; starts `YYYY-MM-DD` | OKF006, OKF007 |
| `updated` | optional; starts `YYYY-MM-DD` | OKF008 |
| `generated.by` | required on new notes; a valid actor (below) | OKF009, OKF010, OKF012 |

Older notes use other types and statuses (`internal-note`, `project-note`, `proposed`,
`stable`). They fail as soon as a PR touches them. Map them to the nearest allowed value in
the same PR rather than leaving the check red.

## Provenance

`generated: { by, at }` records who wrote a note (vault ADR 0007).

```yaml
generated:
  by: agent:claude-opus-5-5   # an agent: agent:<model-id>
  at: 2026-11-17
```

| Actor | Form |
|---|---|
| An agent | `agent:<model-id>`, e.g. `agent:claude-opus-5-5`, `agent:gemini-3.8-flash` |
| A tool with a version | `<producer>/<version>` |
| Automation | `process:<name>`, e.g. `process:bookmark-import` |
| A person | `human:<handle>` |

- **A bare model id fails** (`claude-opus-5-5`, `claude`, `codex`): OKF012. Always prefix.
- **An agent never writes `human:` on a new note**: OKF011. A person sets that themselves.
- **Claim only what you can show.** For a note you write, use your own model id. For an
  existing note, find the writer in the session logs (`~/.claude/projects`,
  `~/.codex/sessions`, `~/.gemini/`) before adding `generated:`. If nothing shows who wrote
  it, leave it absent. Absent means unattributed, never human. Review bots check these
  claims and open threads on unsupported ones.
- **`verified: { by: human:<handle>, at }`** is added by a person who has checked
  agent-written content. An agent never adds it.

## Lint before pushing

CI diffs against `origin/main`, not the PR's base. A stacked PR is therefore checked
against everything between `main` and its head, and faults a touched note already had
fail the PR too. Run the same code locally, at the pinned SHA:

```bash
# once, into the vault's gitignored temp/
cd ~/code/personal/obsidian-vault
git clone -q https://github.com/rtkelly-labs/github-actions temp/github-actions
cd temp/github-actions && git checkout -q <sha from .github/workflows/okf-lint.yml> && cd -

# from the vault worktree being pushed
VAULT_PATH=Vault BASE_REF=origin/main REQUIRE_PROVENANCE=true uv run --no-project python -c \
  "import sys; sys.path.insert(0, '$HOME/code/personal/obsidian-vault/temp/github-actions/src'); from estate_actions.cli import run; sys.exit(run('okf-lint'))"
```

An empty `BASE_REF` lints the whole vault, which still reports thousands of older faults.
Use it to find one class (for example every OKF012), not as a gate.

Also check that every wikilink in the changed notes resolves (`vault-health`).

## Landing a vault PR

- The repo is private, so there is no auto-merge. Merge with `gh pr merge <n> --squash`
  once the check is green, then confirm the state is `MERGED`.
- **BLOCKED with green checks means an open review thread.** List them with
  `rig gh threads list <n>`. Fix the issue, or answer it with evidence, then resolve the
  thread with `rig gh threads resolve <n> <thread-id> --sha <fix-sha>`.
- Merge one PR at a time. Hub notes overlap, so each merge can make the next PR conflict;
  the usual conflict is an `updated:` date, and the later date wins.
- When the base PR of a stack merges, the next PR retargets to `main`. Rebase it with
  `git rebase --onto origin/main <old-base-head>` before merging.
