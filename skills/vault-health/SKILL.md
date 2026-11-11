---
name: vault-health
description: Check and repair vault integrity - broken wikilinks, orphans, frontmatter, filenames, duplicate notes. Use after batch promotion, before a cleanup pass, or when asked about the state of the vault.
---

# Health

Maintenance lives in `rig` as the `vault` CLI (ADR 0013 in the vault repo;
`the-vault-system` is archived). Run from there:

```bash
cd ~/code/personal/rig

uv run vault paths                       # which vault is being maintained
uv run vault graph lint                  # frontmatter, duplicates, filename rules
uv run vault graph links list            # broken wikilinks
uv run vault graph orphans list          # notes with no inbound links
uv run vault graph metrics health        # folder density
uv run vault graph metrics folder-scores
```

Maintenance targets the **git** vault, not the Google Drive copy Obsidian opens — repairs
there are reviewable and committable, and the two converge via `vault-reconcile`.

## Repair

Every repair command is read-only until `--apply`. **Always run without it first**
and read the diff of what it proposes.

```bash
uv run vault graph links fix --apply           # repoint resolvable broken links
uv run vault graph orphans link --apply        # link orphans to their parent hub
uv run vault graph repair filenames --apply    # rename + rewrite referring wikilinks
uv run vault graph repair metadata --apply
uv run vault graph repair backfill-types --apply
uv run vault graph repair duplicates --apply
uv run vault graph hubs --apply                # create/maintain PARA root hubs
```

`repair filenames` is the one worth having: it renames a file *and* rewrites the
wikilinks pointing at it in the same pass. Renaming by hand breaks the graph.

## Standing debt

As of Sep 2026: ~763 orphans, ~562 broken wikilinks, ~374 filename issues across
~1,975 notes. **This is a backlog, not a regression.** Do not mass-fix:

- Many orphans are deliberate — top-level thinking notes nobody links to on purpose.
- `Archive/Duplicates/` is intentionally full of near-duplicate filenames.
- The filename rules suggest lowercase slugs, which **conflicts with the vault's
  actual Title Case convention**. Treat `repair filenames` suggestions as advisory
  and reject the ones that would de-capitalise a well-named note.

Fix in small themed batches with a stated intent, and re-run after. A green report is
not the goal; a vault you can navigate is.

## Before adding machinery

This is ~2,900 lines doing one job, inside a repo that already exists. It was 18,246
lines of source and 33,056 of tests doing thirteen, in a repo of its own, and produced
70 notes in its life. If a new rule is worth having, add it to
`src/Rig.Core/Vault/LintRules.cs` and `VaultLinter.cs`. Before adding anything larger, ask what
decision it actually makes for you.
